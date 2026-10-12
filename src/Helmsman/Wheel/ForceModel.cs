using System;

namespace Helmsman.Wheel
{
    // The force law that holds the wheel to the rudder; pure state and math, so it can be unit tested without a wheel.
    internal sealed class ForceModel
    {
        private const float TargetCorrectionGain = 0.25f;
        private const float RudderLeadSeconds = 0.05f;
        private const float MinForceRampDegrees = 2f;
        private const float MinForceFadeDegreesPerSecond = 30f;
        private const float MaxReturnDegreesPerSecond = 300f;

        private readonly WheelSettings _settings;
        private float _tracked;

        public ForceModel(WheelSettings settings) => _settings = settings;

        // Where the model believes the rudder (in wheel degrees) is right now.
        public float Target => _tracked;

        public void Reset(float target, float velocity) => _tracked = target;

        // Brakes a wheel going faster than the rudder, or against it; never pushes a slower wheel along, which would let wheel and rudder chase each other.
        private float Damping(float vel, float tv)
        {
            float along = vel * Math.Sign(tv);
            if (tv == 0f || along < 0f) return -_settings.Damping * vel;
            return along > Math.Abs(tv) ? -_settings.Damping * (vel - tv) : 0f;
        }

        // Advances by dt and returns the force (-cap..cap, positive = clockwise); newSample is true when the game published (target, targetVelocity) since the last step.
        public float Step(float dt, float angle, float vel, float target, float targetVelocity, bool newSample, float cap)
        {
            // Game updates arrive in frame-timed bursts, so advance our own copy of the rudder at its (exact) rate and only nudge it toward each update.
            float tv = targetVelocity;
            _tracked += tv * dt;
            if (newSample) _tracked += (target - _tracked) * TargetCorrectionGain;

            // Free zone just ahead of a moving rudder so keeping up costs no effort; it never pulls the wheel past the rudder.
            float lead = _tracked + tv * RudderLeadSeconds;
            float lo = Math.Min(_tracked, lead), hi = Math.Max(_tracked, lead);
            float err = angle < lo ? lo - angle : angle > hi ? hi - angle : 0f;

            float db = _settings.DeadbandDegrees;
            err = Math.Abs(err) <= db ? 0f : err - Math.Sign(err) * db;
            float spring = _settings.Stiffness * err;
            // Limit how fast the wheel is pulled home: past MaxReturn the spring eases off, and at twice that it brakes.
            float approach = (vel - tv) * Math.Sign(err);
            if (approach > MaxReturnDegreesPerSecond) spring *= Math.Max(-1f, 2f - approach / MaxReturnDegreesPerSecond);
            float force = spring + Damping(vel, tv);
            // Overcome static gearbox friction: fades in over the first few degrees (no buzz at the target) and out as the wheel moves.
            float minForceScale = Math.Min(1f, Math.Abs(err) / MinForceRampDegrees) * Math.Max(0f, 1f - Math.Abs(vel) / MinForceFadeDegreesPerSecond);
            force += Math.Sign(err) * _settings.MinForce * minForceScale;
            return Math.Max(-cap, Math.Min(cap, force));
        }
    }
}
