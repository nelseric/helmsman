using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Helmsman.Wheel
{
    // Minimal SDL3 joystick and haptic bindings.
    internal static class Sdl
    {
        private const string Lib = "SDL3";

        public const uint INIT_JOYSTICK = 0x200;
        public const uint INIT_HAPTIC = 0x1000;
        public const uint HAPTIC_CONSTANT = 1u << 0;
        public const uint HAPTIC_GAIN = 1u << 16;
        public const uint HAPTIC_AUTOCENTER = 1u << 17;
        public const byte HAPTIC_STEERING_AXIS = 3;
        public const uint HAPTIC_INFINITY = uint.MaxValue;
        public const int JOYSTICK_TYPE_WHEEL = 2;
        // Comfortably larger than sizeof(SDL_HapticEffect).
        public const int HapticEffectSize = 128;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        // Prefers an SDL3 library shipped next to the plugin, then the system one. Returns the version, or null if there is none.
        public static int? Load()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string local = Path.Combine(Path.GetDirectoryName(typeof(Sdl).Assembly.Location) ?? "", "SDL3.dll");
                    if (File.Exists(local)) LoadLibraryW(local);
                }
                return SDL_GetVersion();
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        public static string VersionString(int v) => $"{v / 1000000}.{v / 1000 % 1000}.{v % 1000}";

        public static string Error() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetVersion();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_GetError();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SDL_SetHint([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SDL_InitSubSystem(uint flags);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_QuitSubSystem(uint flags);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_free(IntPtr mem);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_UpdateJoysticks();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_GetJoysticks(out int count);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_OpenJoystick(uint instanceId);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_CloseJoystick(IntPtr joystick);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SDL_JoystickConnected(IntPtr joystick);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_GetJoystickName(IntPtr joystick);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ushort SDL_GetJoystickVendor(IntPtr joystick);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ushort SDL_GetJoystickProduct(IntPtr joystick);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetJoystickType(IntPtr joystick);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetNumJoystickAxes(IntPtr joystick);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetNumJoystickButtons(IntPtr joystick);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern short SDL_GetJoystickAxis(IntPtr joystick, int axis);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SDL_GetJoystickButton(IntPtr joystick, int button);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SDL_IsJoystickHaptic(IntPtr joystick);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_OpenHapticFromJoystick(IntPtr joystick);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_CloseHaptic(IntPtr haptic);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetHapticFeatures(IntPtr haptic);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_CreateHapticEffect(IntPtr haptic, byte[] effect);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SDL_UpdateHapticEffect(IntPtr haptic, int effect, byte[] data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SDL_RunHapticEffect(IntPtr haptic, int effect, uint iterations);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SDL_StopHapticEffect(IntPtr haptic, int effect);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_DestroyHapticEffect(IntPtr haptic, int effect);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SDL_SetHapticGain(IntPtr haptic, int gain);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SDL_SetHapticAutocenter(IntPtr haptic, int autocenter);

        // Fills an SDL_HapticConstant (x86_64/arm64 layout) for an endless steering-axis force.
        public static void WriteSteeringConstant(byte[] effect, short level)
        {
            Array.Clear(effect, 0, effect.Length);
            BitConverter.GetBytes((ushort)HAPTIC_CONSTANT).CopyTo(effect, 0);
            effect[4] = HAPTIC_STEERING_AXIS;
            BitConverter.GetBytes(HAPTIC_INFINITY).CopyTo(effect, 20);
            BitConverter.GetBytes(level).CopyTo(effect, 30);
        }
    }
}
