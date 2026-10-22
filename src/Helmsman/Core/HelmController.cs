using System;

namespace Helmsman.Core
{
    internal sealed class HelmSettings
    {
        public float DegreesAtFullRudder = 450f;
        public bool InvertSteering;
        public float SyncToleranceDegrees = 15f;
        public bool ReturnToCenter;
        // Pull toward center at full rudder and ReturnToCenterFullSpeed or faster.
        public float ReturnToCenterForce = 0.1f;
        public float ReturnToCenterFullSpeed = 8f;
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
        // After centering, a wheel still swinging home faster than this would drag the rudder with it.
        private const float TightResyncMaxDegreesPerSecond = 90f;
        private const float GrabAwayFromCenterDegreesPerSecond = 30f;
        // While centering, a wheel this far behind, under half the rudder's speed (and slow outright) and not speeding up for this long is being held.
        private const float GripLagDegrees = 15f;
        private const float GripSpeedFraction = 0.5f;
        private const float GripMaxDegreesPerSecond = 150f;
        private const float GripAccelerationDegreesPerSecondSquared = 1500f;
        private const float GripSeconds = 0.06f;
        // Once centering has reached straight, a wheel held this far off and slower than this toward the rudder is being held.
        private const float SettleGripMaxDegreesPerSecond = 40f;

        private readonly HelmSettings _settings;
        private bool _synced;
        private bool _tightResync;
        private bool _settleFirst;
        private bool _centering;
        private int _pendingSpeedSteps;
        private bool _injectedSpeedLastTick;
        private float _lastError = float.NaN;
        private int _centerFromSide;
        private float _slowSeconds;
        private float _lastTowardSpeed;
        private int _releaseKick;

        public HelmController(HelmSettings settings) => _settings = settings;

        // Receives a line per state change (centering, wheel taking over), for debugging.
        public Action<string> Log { get; set; }

        public bool Synced => _synced;
        public bool Centering => _centering;

        // Returns and clears the wheel direction (+1 clockwise, -1, or 0) of a brief push signalling that a grip released the homing.
        public int TakeReleaseKick()
        {
            int kick = _releaseKick;
            _releaseKick = 0;
            return kick;
        }

        // Call when taking a (different) helm or leaving it.
        public void Reset()
        {
            _synced = false;
            _tightResync = false;
            _settleFirst = false;
            _centering = false;
            _pendingSpeedSteps = 0;
            _injectedSpeedLastTick = false;
            _lastError = float.NaN;
            _releaseKick = 0;
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
                _slowSeconds = 0f;
                _lastTowardSpeed = -input.WheelVelocity * Sign * _centerFromSide;
                Log?.Invoke($"Centering from rudder {rudder * deg:0.0}°, wheel {input.WheelDegrees:0.0}°");
            }
            if (_centering)
            {
                // The wheel trails the rudder home and may overshoot center; only pulling it back toward the starting side cancels.
                float backSpeed = input.WheelVelocity * _centerFromSide;
                bool pulledBack = backSpeed > GrabAwayFromCenterDegreesPerSecond && input.WheelDegrees * _centerFromSide > CenteredWheelDegrees;
                float lag = (target - rudder) * _centerFromSide * deg;
                float rudderRate = ShipRudder.MaxStep(rudder, input.RudderSpeed, input.DeltaTime) / input.DeltaTime * deg;
                bool heldBack = Gripped(lag, -input.WheelVelocity * Sign * _centerFromSide, Math.Min(GripSpeedFraction * rudderRate, GripMaxDegreesPerSecond), input.DeltaTime);
                bool arrived = Math.Abs(rudder) < 0.001f;
                if (manualSteer || pulledBack || heldBack || arrived)
                {
                    string why = manualSteer ? "keyboard steering" : pulledBack ? "wheel pulled back" : heldBack ? "wheel gripped" : "arrived";
                    Log?.Invoke($"Centering ended ({why}): rudder {rudder * deg:0.0}°, wheel {input.WheelDegrees:0.0}° at {input.WheelVelocity:0}°/s");
                    _centering = false;
                    if (pulledBack || heldBack)
                    {
                        // The wheel is in the player's hand, so it steers from where they hold it instead of being pulled to the rudder.
                        _synced = true;
                        _tightResync = false;
                        _settleFirst = false;
                        _lastError = float.NaN;
                        _releaseKick = (int)Sign * _centerFromSide;
                    }
                    else
                    {
                        // Hand back to the wheel only once it has caught up and slowed, so a wheel still swinging home can't drag the rudder.
                        StartTightResync();
                        _settleFirst = true;
                        _slowSeconds = 0f;
                        _lastTowardSpeed = 0f;
                    }
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
                bool settled = !_settleFirst || Math.Abs(input.WheelVelocity) < TightResyncMaxDegreesPerSecond;
                // A wheel held off, or turned away, while it settles after centering is in the player's hand.
                bool held = !_synced && _settleFirst && Gripped(Math.Abs(error), -input.WheelVelocity * Sign * Math.Sign(error), SettleGripMaxDegreesPerSecond, input.DeltaTime);
                if (held) _releaseKick = Math.Sign(error) * (int)Sign;
                if (!_synced && (held || settled && (Math.Abs(error) <= tolerance || crossed || atRestNearby)))
                {
                    Log?.Invoke($"Wheel took over ({(held ? "gripped" : Math.Abs(error) <= tolerance ? "close" : crossed ? "crossed" : "at rest nearby")}): rudder {rudder * deg:0.0}°, wheel {input.WheelDegrees:0.0}°");
                    _synced = true;
                    _tightResync = false;
                    _settleFirst = false;
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

        // The tiller's pull toward center for the wheel's force model: per wheel degree, and the share of friction help (0..1). Only while the wheel steers.
        public void ReturnToCenterFor(float shipSpeed, out float perDegree, out float friction)
        {
            float speedShare = _settings.ReturnToCenterFullSpeed > 0f ? Math.Min(1f, Math.Abs(shipSpeed) / _settings.ReturnToCenterFullSpeed) : 1f;
            bool active = _settings.ReturnToCenter && _synced && !_centering && speedShare > 0f;
            perDegree = active ? _settings.ReturnToCenterForce * speedShare / _settings.DegreesAtFullRudder : 0f;
            // Below a third of full speed the water is too weak to turn a released wheel against its gearbox.
            friction = active ? Math.Min(1f, speedShare * 3f) : 0f;
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

        // True once the wheel has stayed lagDeg behind its target, slower than maxSpeed toward it and not speeding up, for GripSeconds.
        private bool Gripped(float lagDeg, float towardSpeed, float maxSpeed, float dt)
        {
            float acceleration = (towardSpeed - _lastTowardSpeed) / dt;
            _lastTowardSpeed = towardSpeed;
            bool slow = lagDeg > GripLagDegrees && towardSpeed < maxSpeed && acceleration < GripAccelerationDegreesPerSecondSquared;
            _slowSeconds = slow ? _slowSeconds + dt : 0f;
            return _slowSeconds >= GripSeconds - 1e-4f;
        }

        private void StartTightResync()
        {
            _synced = false;
            _tightResync = true;
            _settleFirst = false;
            _lastError = float.NaN;
        }

        private float Sign => _settings.InvertSteering ? -1f : 1f;

        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    }
}
