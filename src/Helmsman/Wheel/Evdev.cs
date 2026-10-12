using System;
using System.Runtime.InteropServices;

namespace Helmsman.Wheel
{
    // Minimal Linux evdev bindings (x86_64 layouts).
    internal static class Evdev
    {
        public const int O_RDWR = 2;
        public const int O_NONBLOCK = 0x800;
        public const int EAGAIN = 11;

        public const ushort EV_KEY = 0x01;
        public const ushort EV_ABS = 0x03;
        public const ushort EV_FF = 0x15;
        public const ushort ABS_X = 0x00;
        public const ushort FF_CONSTANT = 0x52;
        public const ushort FF_GAIN = 0x60;
        public const ushort FF_AUTOCENTER = 0x61;

        public const int InputEventSize = 24;
        public const int FfEffectSize = 48;

        private static ulong Ioc(ulong dir, ulong nr, ulong size) => (dir << 30) | (size << 16) | ((ulong)'E' << 8) | nr;
        private const ulong IocWrite = 1, IocRead = 2;

        public static readonly ulong EVIOCGID = Ioc(IocRead, 0x02, 8);
        public static ulong EVIOCGNAME(int len) => Ioc(IocRead, 0x06, (ulong)len);
        public static ulong EVIOCGBIT(int ev, int len) => Ioc(IocRead, 0x20 + (ulong)ev, (ulong)len);
        public static ulong EVIOCGABS(int abs) => Ioc(IocRead, 0x40 + (ulong)abs, 24);
        public static readonly ulong EVIOCSFF = Ioc(IocWrite, 0x80, FfEffectSize);
        public static readonly ulong EVIOCRMFF = Ioc(IocWrite, 0x81, 4);

        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int open(string path, int flags);

        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int close(int fd);

        [DllImport("libc.so.6", SetLastError = true)]
        public static extern IntPtr read(int fd, byte[] buf, IntPtr count);

        [DllImport("libc.so.6", SetLastError = true)]
        public static extern IntPtr write(int fd, byte[] buf, IntPtr count);

        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int ioctl(int fd, ulong request, byte[] arg);

        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int ioctl(int fd, ulong request, IntPtr arg);

        public static bool TestBit(byte[] bits, int bit) => (bits[bit / 8] & (1 << (bit % 8))) != 0;

        public static bool WriteEvent(int fd, ushort type, ushort code, int value)
        {
            var buf = new byte[InputEventSize];
            BitConverter.GetBytes(type).CopyTo(buf, 16);
            BitConverter.GetBytes(code).CopyTo(buf, 18);
            BitConverter.GetBytes(value).CopyTo(buf, 20);
            return (long)write(fd, buf, (IntPtr)InputEventSize) == InputEventSize;
        }

        // Uploads (id < 0) or updates an infinite FF_CONSTANT effect; positive level = clockwise. Returns the effect id or -1.
        public static int UploadConstant(int fd, int id, short level)
        {
            var buf = new byte[FfEffectSize];
            BitConverter.GetBytes(FF_CONSTANT).CopyTo(buf, 0);
            BitConverter.GetBytes((short)id).CopyTo(buf, 2);
            // 0xC000 makes positive levels turn the wheel clockwise (positive ABS_X) on lg4ff.
            BitConverter.GetBytes((ushort)0xC000).CopyTo(buf, 4);
            BitConverter.GetBytes(level).CopyTo(buf, 16);
            if (ioctl(fd, EVIOCSFF, buf) < 0) return -1;
            return BitConverter.ToInt16(buf, 2);
        }
    }
}
