namespace Helmsman.Core
{
    // Turns the player's view with the ship: reports how far the ship's heading moved since the last frame.
    public sealed class ViewFollower
    {
        private float? _lastYaw;

        // Call when taking or leaving the helm, so the next frame doesn't jump.
        public void Reset() => _lastYaw = null;

        // Degrees to add to the view's yaw this frame, given the ship's heading in degrees.
        public float Step(float shipYawDegrees)
        {
            float delta = _lastYaw.HasValue ? DeltaAngle(_lastYaw.Value, shipYawDegrees) : 0f;
            _lastYaw = shipYawDegrees;
            return delta;
        }

        private static float DeltaAngle(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }
    }
}
