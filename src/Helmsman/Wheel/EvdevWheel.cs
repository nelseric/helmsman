using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Helmsman.Wheel
{
    // A force-feedback wheel opened through Linux evdev.
    internal sealed class EvdevWheel : IWheelDevice
    {
        private const int BtnJoystick = 0x120;
        private const int KeyCount = 0x300;

        private int _fd;
        private readonly int _absMin, _absMax;
        // evdev key code -> button number, in the same order SDL and DirectInput number them.
        private readonly int[] _buttonIndex;
        private readonly byte[] _buf = new byte[Evdev.InputEventSize * 64];
        private int _rawX;
        private int _effectId = -1;

        public string Description { get; }
        public string Name { get; }
        public float? RangeDegrees { get; }

        private EvdevWheel(int fd, string path, string name, ushort vendor, ushort product, int value, int min, int max, byte[] keyBits)
        {
            _fd = fd;
            Name = name;
            _rawX = value;
            _absMin = min;
            _absMax = max;
            _buttonIndex = ButtonNumbers(keyBits);
            RangeDegrees = ReadSysfsRange(path);
            Description = $"{name} ({vendor:x4}:{product:x4}) at {path} via evdev";
            Evdev.WriteEvent(fd, Evdev.EV_FF, Evdev.FF_GAIN, 0xFFFF);
        }

        // Opens devicePath, or the first device exposing ABS_X and FF_CONSTANT (preferring names containing "wheel").
        public static EvdevWheel Open(string devicePath, Action<string> log)
        {
            if (!string.IsNullOrEmpty(devicePath)) return TryOpen(devicePath, log, true);

            var candidates = Directory.GetFiles("/dev/input", "event*")
                .OrderBy(p => int.TryParse(System.IO.Path.GetFileName(p).Substring(5), out var n) ? n : int.MaxValue)
                .Select(p => TryOpen(p, log, false))
                .Where(d => d != null)
                .ToList();
            var pick = candidates.FirstOrDefault(d => d.Name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? candidates.FirstOrDefault();
            foreach (var d in candidates.Where(d => d != pick)) d.Dispose();
            return pick;
        }

        private static EvdevWheel TryOpen(string path, Action<string> log, bool explicitPath)
        {
            int fd = Evdev.open(path, Evdev.O_RDWR | Evdev.O_NONBLOCK);
            if (fd < 0)
            {
                if (explicitPath) log?.Invoke($"Cannot open {path} (errno {Marshal.GetLastWin32Error()})");
                return null;
            }

            var absBits = new byte[8];
            var ffBits = new byte[16];
            bool ok = Evdev.ioctl(fd, Evdev.EVIOCGBIT(Evdev.EV_ABS, absBits.Length), absBits) >= 0
                      && Evdev.ioctl(fd, Evdev.EVIOCGBIT(Evdev.EV_FF, ffBits.Length), ffBits) >= 0
                      && Evdev.TestBit(absBits, Evdev.ABS_X)
                      && Evdev.TestBit(ffBits, Evdev.FF_CONSTANT);
            var absInfo = new byte[24];
            if (!ok || Evdev.ioctl(fd, Evdev.EVIOCGABS(Evdev.ABS_X), absInfo) < 0)
            {
                if (explicitPath) log?.Invoke($"{path} has no steering axis with constant-force support");
                Evdev.close(fd);
                return null;
            }

            var keyBits = new byte[KeyCount / 8];
            Evdev.ioctl(fd, Evdev.EVIOCGBIT(Evdev.EV_KEY, keyBits.Length), keyBits);
            var id = new byte[8];
            Evdev.ioctl(fd, Evdev.EVIOCGID, id);
            var nameBuf = new byte[256];
            Evdev.ioctl(fd, Evdev.EVIOCGNAME(nameBuf.Length), nameBuf);
            string name = Encoding.UTF8.GetString(nameBuf).TrimEnd('\0');

            return new EvdevWheel(fd, path, name,
                BitConverter.ToUInt16(id, 2), BitConverter.ToUInt16(id, 4),
                BitConverter.ToInt32(absInfo, 0), BitConverter.ToInt32(absInfo, 4), BitConverter.ToInt32(absInfo, 8), keyBits);
        }

        // Numbers joystick buttons first, then any lower key codes, like SDL's Linux driver.
        private static int[] ButtonNumbers(byte[] keyBits)
        {
            var index = Enumerable.Repeat(-1, KeyCount).ToArray();
            int n = 0;
            for (int code = BtnJoystick; code < KeyCount; code++)
                if (Evdev.TestBit(keyBits, code)) index[code] = n++;
            for (int code = 0; code < BtnJoystick; code++)
                if (Evdev.TestBit(keyBits, code)) index[code] = n++;
            return index;
        }

        public bool Poll(Action<int> buttonPressed, out float position)
        {
            while (true)
            {
                long n = (long)Evdev.read(_fd, _buf, (IntPtr)_buf.Length);
                if (n < 0)
                {
                    if (Marshal.GetLastWin32Error() == Evdev.EAGAIN) break;
                    position = 0f;
                    return false;
                }
                if (n == 0) break;
                for (int off = 0; off + Evdev.InputEventSize <= n; off += Evdev.InputEventSize)
                {
                    ushort type = BitConverter.ToUInt16(_buf, off + 16);
                    ushort code = BitConverter.ToUInt16(_buf, off + 18);
                    int value = BitConverter.ToInt32(_buf, off + 20);
                    if (type == Evdev.EV_ABS && code == Evdev.ABS_X) _rawX = value;
                    else if (type == Evdev.EV_KEY && value == 1 && code < KeyCount && _buttonIndex[code] >= 0) buttonPressed(_buttonIndex[code]);
                }
            }
            float mid = (_absMin + _absMax) / 2f;
            float half = Math.Max(1f, (_absMax - _absMin) / 2f);
            position = (_rawX - mid) / half;
            return true;
        }

        public bool SetForce(short level)
        {
            int id = Evdev.UploadConstant(_fd, _effectId, level);
            if (id < 0) return false;
            if (_effectId < 0) Evdev.WriteEvent(_fd, Evdev.EV_FF, (ushort)id, 1);
            _effectId = id;
            return true;
        }

        public void SetAutocenter(float strength) =>
            Evdev.WriteEvent(_fd, Evdev.EV_FF, Evdev.FF_AUTOCENTER, (int)(Math.Max(0f, Math.Min(1f, strength)) * 0xFFFF));

        // hid-logitech (lg4ff / new-lg4ff) exposes the configured rotation range in sysfs.
        private static float? ReadSysfsRange(string devPath)
        {
            try
            {
                string file = $"/sys/class/input/{System.IO.Path.GetFileName(devPath)}/device/device/range";
                if (File.Exists(file) && float.TryParse(File.ReadAllText(file).Trim(), out var deg) && deg > 0) return deg;
            }
            catch (Exception) { }
            return null;
        }

        public void Dispose()
        {
            if (_fd < 0) return;
            if (_effectId >= 0)
            {
                Evdev.WriteEvent(_fd, Evdev.EV_FF, (ushort)_effectId, 0);
                Evdev.ioctl(_fd, Evdev.EVIOCRMFF, (IntPtr)_effectId);
            }
            Evdev.close(_fd);
            _fd = -1;
        }
    }
}
