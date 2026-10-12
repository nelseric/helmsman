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
        public float WheelVelocity;
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
        private const float TightResyncDegrees = 2f;
        private const float WheelAtRestDegreesPerSecond = 20f;
        private const float TightRestDegrees = 5f;
        private const float GrabAwayFromCenterDegreesPerSecond = 30f;
        private const float GrabHoldMultiple = 3f;

        private readonly HelmSettings _settings;
        private bool _synced;
        private bool _tightResync;
        private bool _centering;
        private int _pendingSpeedSteps;
        private bool _injectedSpeedLastTick;
        private float _lastError = float.NaN;
        private int _centerFromSide;

        public HelmController(HelmSettings settings) => _settings = settings;

        // Receives a line per state change (centering, wheel taking over), for debugging.
        public Action<string> Log { get; set; }

        public bool Synced => _synced;
        public bool Centering => _centering;

        // Call when taking a (different) helm or leaving it.
        public void Reset()
        {
            _synced = false;
            _tightResync = false;
            _centering = false;
            _pendingSpeedSteps = 0;
            _injectedSpeedLastTick = false;
            _lastError = float.NaN;
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

            if (input.CenterPressed && !_centering)
            {
                _centering = true;
                _centerFromSide = Math.Sign(rudder);
                Log?.Invoke($"Centering from rudder {rudder * deg:0.0}°, wheel {input.WheelDegrees:0.0}°");
            }
            if (_centering)
            {
                // The wheel trails the rudder home and may overshoot center; only pulling it back toward the starting side cancels.
                float backSpeed = input.WheelVelocity * Math.Sign(input.WheelDegrees) * _centerFromSide;
                bool pulledBack = backSpeed > GrabAwayFromCenterDegreesPerSecond && input.WheelDegrees * _centerFromSide > CenteredWheelDegrees;
                bool homeward = -input.WheelVelocity * _centerFromSide > WheelAtRestDegreesPerSecond;
                bool heldBack = !homeward && (target - rudder) * _centerFromSide * deg > _settings.SyncToleranceDegrees * GrabHoldMultiple;
                bool arrived = Math.Abs(rudder) < 0.001f;
                if (manualSteer || pulledBack || heldBack || arrived)
                {
                    string why = arrived ? "arrived" : manualSteer ? "keyboard steering" : pulledBack ? "wheel pulled back" : "wheel held back";
                    Log?.Invoke($"Centering ended ({why}): rudder {rudder * deg:0.0}°, wheel {input.WheelDegrees:0.0}° at {input.WheelVelocity:0}°/s");
                    _centering = false;
                    // Hand back to the wheel only once it has caught up, so a wheel still swinging home can't drag the rudder.
                    StartTightResync();
                }
                else
                {
                    target = 0f;
                    _synced = true;
                }
            }

            if (manualSteer)
            {
                StartTightResync();
            }
            else
            {
                // Take over when the wheel is close (tightly so after keyboard/centering), passes through the rudder, or rests near it despite friction.
                float error = (target - rudder) * deg;
                float tolerance = _tightResync ? TightResyncDegrees : _settings.SyncToleranceDegrees;
                bool crossed = !float.IsNaN(_lastError) && Math.Sign(error) != Math.Sign(_lastError);
                bool atRestNearby = Math.Abs(input.WheelVelocity) < WheelAtRestDegreesPerSecond && Math.Abs(error) <= (_tightResync ? TightRestDegrees : _settings.SyncToleranceDegrees);
                if (!_synced && (Math.Abs(error) <= tolerance || crossed || atRestNearby))
                {
                    Log?.Invoke($"Wheel took over ({(Math.Abs(error) <= tolerance ? "close" : crossed ? "crossed" : "at rest nearby")}): rudder {rudder * deg:0.0}°, wheel {input.WheelDegrees:0.0}°");
                    _synced = true;
                    _tightResync = false;
                }
                _lastError = error;
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

        private void StartTightResync()
        {
            _synced = false;
            _tightResync = true;
            _lastError = float.NaN;
        }

        private float Sign => _settings.InvertSteering ? -1f : 1f;

        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    }
}
