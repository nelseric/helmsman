using System;

namespace Helmsman.Wheel
{
    // The force law that holds the wheel to the rudder; pure state and math, so it can be unit tested without a wheel.
    internal sealed class ForceModel
    {
        private const float TargetVelocitySmoothingSeconds = 0.03f;
        private const float TargetCorrectionGain = 0.25f;
        private const float RudderLeadSeconds = 0.05f;
        private const float MinForceRampDegrees = 2f;
        private const float MinForceFadeDegreesPerSecond = 30f;

        private readonly WheelSettings _settings;
        private float _tracked;
        private float _trackedVelocity;

        public ForceModel(WheelSettings settings) => _settings = settings;

        // Where the model believes the rudder (in wheel degrees) is right now.
        public float Target => _tracked;

        public void Reset(float target, float velocity)
        {
            _tracked = target;
            _trackedVelocity = velocity;
        }

        // Advances by dt and returns the force (-cap..cap, positive = clockwise); newSample is true when the game published (target, targetVelocity) since the last step.
        public float Step(float dt, float angle, float vel, float target, float targetVelocity, bool newSample, float cap)
        {
            // Game updates arrive in frame-timed bursts, so advance our own copy of the rudder at its rate and only nudge it toward each update.
            _trackedVelocity += (targetVelocity - _trackedVelocity) * (dt / (TargetVelocitySmoothingSeconds + dt));
            float tv = _trackedVelocity;
            _tracked += tv * dt;
            if (newSample) _tracked += (target - _tracked) * TargetCorrectionGain;

            // Free zone just ahead of a moving rudder so keeping up costs no effort; it never pulls the wheel past the rudder.
            float lead = _tracked + tv * RudderLeadSeconds;
            float lo = Math.Min(_tracked, lead), hi = Math.Max(_tracked, lead);
            float err = angle < lo ? lo - angle : angle > hi ? hi - angle : 0f;

            float db = _settings.DeadbandDegrees;
            err = Math.Abs(err) <= db ? 0f : err - Math.Sign(err) * db;
            // Damping only resists moving faster than the rudder; pushing a slower wheel along would let wheel and rudder chase each other.
            float overspeed = vel - tv;
            bool resists = tv == 0f || Math.Sign(overspeed) == Math.Sign(tv);
            float force = _settings.Stiffness * err - (resists ? _settings.Damping * overspeed : 0f);
            // Overcome static gearbox friction: fades in over the first few degrees (no buzz at the target) and out as the wheel moves.
            float minForceScale = Math.Min(1f, Math.Abs(err) / MinForceRampDegrees) * Math.Max(0f, 1f - Math.Abs(vel) / MinForceFadeDegreesPerSecond);
            force += Math.Sign(err) * _settings.MinForce * minForceScale;
            return Math.Max(-cap, Math.Min(cap, force));
        }
    }
}
