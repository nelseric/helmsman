using System;

namespace Helmsman.Core
{
    internal sealed class HelmSettings
    {
        public float DegreesAtFullRudder = 450f;
        public bool InvertSteering;
        public float SyncToleranceDegrees = 15f;
    }

    // One physics tick of input to the helm.
    internal struct HelmInput
    {
        public float WheelDegrees;
        public float Rudder;
        public float RudderSpeed;
        public float DeltaTime;
        // Steering input from the game's own controls (keyboard/gamepad): dir.x and dir.z.
        public float ManualX;
        public float ManualZ;
        public bool CenterPressed;
        // Net sail speed button presses this tick (up minus down).
        public int SpeedSteps;
    }

    // Turns wheel position and buttons into the steering input Valheim expects, one physics tick at a time.
    internal sealed class HelmController
    {
        private const float CenteredWheelDegrees = 3f;
        private const float KeyboardResyncDegrees = 2f;

        private readonly HelmSettings _settings;
        private bool _synced;
        private bool _keyboardResync;
        private bool _centering;
        private int _pendingSpeedSteps;
        private bool _injectedSpeedLastTick;

        public HelmController(HelmSettings settings) => _settings = settings;

        public bool Synced => _synced;
        public bool Centering => _centering;

        // Call when taking a (different) helm or leaving it.
        public void Reset()
        {
            _synced = false;
            _keyboardResync = false;
            _centering = false;
            _pendingSpeedSteps = 0;
            _injectedSpeedLastTick = false;
        }

        // Returns the dir.x / dir.z to pass to Ship.ApplyControlls this tick.
        public void Step(in HelmInput input, out float x, out float z)
        {
            float deg = _settings.DegreesAtFullRudder;
            float rudder = input.Rudder;
            float target = Clamp(input.WheelDegrees / deg * Sign, -1f, 1f);
            bool manualSteer = Math.Abs(input.ManualX) > 0.01f;
            x = input.ManualX;
            z = input.ManualZ;

            if (input.CenterPressed) _centering = true;
            if (_centering)
            {
                // Steer the rudder to straight; the wheel follows it. Grabbing the wheel away from the rudder cancels.
                bool grabbed = Math.Abs(target - rudder) * deg > _settings.SyncToleranceDegrees;
                bool arrived = Math.Abs(rudder) < 0.001f && Math.Abs(input.WheelDegrees) < CenteredWheelDegrees;
                if (manualSteer || grabbed || arrived) _centering = false;
                else
                {
                    target = 0f;
                    _synced = true;
                }
            }

            if (manualSteer)
            {
                _synced = false;
                _keyboardResync = true;
            }
            else
            {
                // After keyboard steering, wait for the wheel to catch up so the rudder doesn't step back to a trailing wheel.
                float tolerance = _keyboardResync ? KeyboardResyncDegrees : _settings.SyncToleranceDegrees;
                if (!_synced && Math.Abs(target - rudder) * deg <= tolerance)
                {
                    _synced = true;
                    _keyboardResync = false;
                }
                if (_synced)
                {
                    // Matches Ship's own step so the rudder lands exactly on the target without overshoot.
                    float step = ShipRudder.MaxStep(rudder, input.RudderSpeed, input.DeltaTime);
                    float delta = target - rudder;
                    x = Math.Abs(delta) < 0.001f || step <= 0f ? 0f : Clamp(delta / step, -1f, 1f);
                }
            }

            _pendingSpeedSteps += input.SpeedSteps;
            // Ship only reacts to a fresh press, so leave a released tick between injected presses.
            if (_injectedSpeedLastTick)
            {
                _injectedSpeedLastTick = false;
            }
            else if (_pendingSpeedSteps != 0 && Math.Abs(z) < 0.5f)
            {
                int step = _pendingSpeedSteps > 0 ? 1 : -1;
                z = step;
                _pendingSpeedSteps -= step;
                _injectedSpeedLastTick = true;
            }
        }

        // The wheel angle and velocity (degrees, degrees/s) that correspond to the rudder after this tick.
        public void WheelTargetFor(float rudder, float rudderInput, float rudderSpeed, out float angle, out float velocity)
        {
            float rate = rudderInput * rudderSpeed;
            if (Math.Abs(rudder) >= 1f && Math.Sign(rate) == Math.Sign(rudder)) rate = 0f;
            float scale = _settings.DegreesAtFullRudder * Sign;
            angle = rudder * scale;
            velocity = rate * scale;
        }

        private float Sign => _settings.InvertSteering ? -1f : 1f;

        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    }
}
