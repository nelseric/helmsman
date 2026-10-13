namespace Helmsman.Wheel
{
    // Device and force-feedback tuning, read live by the wheel thread.
    internal sealed class WheelSettings
    {
        // Auto, Evdev or SDL.
        public string Backend = WheelDevices.Auto;
        public string DevicePath = "";
        public float FallbackRangeDegrees = 900f;
        public float MaxForce = 0.7f;
        public float Stiffness = 0.03f;
        public float Damping = 0.0015f;
        public float DeadbandDegrees = 0.5f;
        public float MinForce = 0.12f;
        public bool InvertForce = false;
        public float IdleAutocenter = 0.25f;
        // Brief push the other way when a grip releases the homing, so it can be felt letting go.
        public float ReleaseKickForce = 0.4f;
        public float ReleaseKickSeconds = 0.05f;
        public string LogPath = "";
    }
}
