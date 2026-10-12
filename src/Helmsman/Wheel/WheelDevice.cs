using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Helmsman.Wheel
{
    // An opened force-feedback wheel event device.
    internal sealed class WheelDevice : IDisposable
    {
        public int Fd { get; private set; }
        public string Path { get; }
        public string Name { get; }
        public ushort Vendor { get; }
        public ushort Product { get; }
        public int AbsMin { get; }
        public int AbsMax { get; }
        public int InitialX { get; }
        public float? SysfsRangeDegrees { get; }

        private WheelDevice(int fd, string path, string name, ushort vendor, ushort product, int value, int min, int max)
        {
            Fd = fd;
            Path = path;
            Name = name;
            Vendor = vendor;
            Product = product;
            InitialX = value;
            AbsMin = min;
            AbsMax = max;
            SysfsRangeDegrees = ReadSysfsRange(path);
        }

        // Opens devicePath, or the first device exposing ABS_X and FF_CONSTANT (preferring names containing "wheel").
        public static WheelDevice Open(string devicePath, Action<string> log)
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

        private static WheelDevice TryOpen(string path, Action<string> log, bool explicitPath)
        {
            int fd = Evdev.open(path, Evdev.O_RDWR | Evdev.O_NONBLOCK);
            if (fd < 0)
            {
                if (explicitPath) log?.Invoke($"Cannot open {path} (errno {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
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

            var id = new byte[8];
            Evdev.ioctl(fd, Evdev.EVIOCGID, id);
            var nameBuf = new byte[256];
            Evdev.ioctl(fd, Evdev.EVIOCGNAME(nameBuf.Length), nameBuf);
            string name = Encoding.UTF8.GetString(nameBuf).TrimEnd('\0');

            return new WheelDevice(fd, path, name,
                BitConverter.ToUInt16(id, 2), BitConverter.ToUInt16(id, 4),
                BitConverter.ToInt32(absInfo, 0), BitConverter.ToInt32(absInfo, 4), BitConverter.ToInt32(absInfo, 8));
        }

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
            if (Fd >= 0) Evdev.close(Fd);
            Fd = -1;
        }
    }
}
