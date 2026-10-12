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
        public string LogPath = "";
    }
}
