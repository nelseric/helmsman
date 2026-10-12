using System;

namespace Helmsman.Wheel
{
    // An opened force-feedback wheel, used only from the wheel thread.
    internal interface IWheelDevice : IDisposable
    {
        // Name, ids and location, for the log.
        string Description { get; }
        // Rotation range reported by the driver, if it reports one.
        float? RangeDegrees { get; }

        // Reads pending input; position is -1..1 across the rotation range, positive = clockwise, or NaN until known. Returns false once the wheel is gone.
        bool Poll(Action<int> buttonPressed, out float position);

        // Sets the constant force, -32767..32767, positive = clockwise.
        bool SetForce(short level);

        // Sets the driver's centering spring, 0..1.
        void SetAutocenter(float strength);
    }

    internal static class WheelDevices
    {
        public const string Auto = "Auto", Evdev = "Evdev", Sdl = "SDL";

        // Opens the configured wheel with the configured backend; Auto uses evdev on Linux and SDL3 elsewhere.
        public static IWheelDevice Open(WheelSettings settings, Action<string> log)
        {
            bool linux = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);
            bool sdl = string.Equals(settings.Backend, Sdl, StringComparison.OrdinalIgnoreCase)
                       || (!string.Equals(settings.Backend, Evdev, StringComparison.OrdinalIgnoreCase) && !linux);
            return sdl ? SdlWheel.Open(settings.DevicePath, log) : EvdevWheel.Open(settings.DevicePath, log);
        }
    }
}
