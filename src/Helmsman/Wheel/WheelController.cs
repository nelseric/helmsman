using System;
using System.Diagnostics;
using System.Threading;

namespace Helmsman.Wheel
{
    // Owns the wheel on a background thread: reads its angle/buttons and drives a position-following constant force.
    internal sealed class WheelController : IDisposable
    {
        private const int LoopSleepMs = 2;

        private readonly WheelSettings _settings;
        private readonly ForceModel _force;
        private readonly Action<string> _log;
        private readonly Thread _thread;
        private volatile bool _running = true;

        private readonly int[] _presses = new int[256];

        private volatile bool _connected;
        private volatile float _angle;
        private volatile float _velocity;
        private volatile string _deviceName = "";

        private volatile bool _follow;
        private volatile float _forceCap;
        private volatile float _targetAngle;
        private volatile float _targetVelocity;
        private long _targetStamp;
        private long _seenTargetStamp;

        public WheelController(WheelSettings settings, Action<string> log)
        {
            _settings = settings;
            _force = new ForceModel(settings);
            _log = log;
            _thread = new Thread(Run) { IsBackground = true, Name = "Helmsman wheel" };
            _thread.Start();
        }

        public bool Connected => _connected;
        public string DeviceName => _deviceName;
        // Wheel angle in degrees from center, positive = clockwise.
        public float AngleDegrees => _angle;
        public float VelocityDegreesPerSecond => _velocity;
        public float LastForce { get; private set; }
        public float LastTarget { get; private set; }

        // Pull the wheel toward angleDeg, which is moving at velocityDegPerSec, with at most maxForce (default Settings.MaxForce).
        public void Follow(float angleDeg, float velocityDegPerSec, float maxForce = -1f)
        {
            _forceCap = maxForce < 0f ? _settings.MaxForce : maxForce;
            _targetAngle = angleDeg;
            _targetVelocity = velocityDegPerSec;
            Interlocked.Exchange(ref _targetStamp, Stopwatch.GetTimestamp());
            _follow = true;
        }

        public void StopFollowing() => _follow = false;

        // Returns and clears the number of presses of a button (numbered from 0) since the last call.
        public int TakePresses(int button) => button >= 0 && button < _presses.Length ? Interlocked.Exchange(ref _presses[button], 0) : 0;

        public void ClearPresses()
        {
            for (int i = 0; i < _presses.Length; i++) _presses[i] = 0;
        }

        private void Run()
        {
            while (_running)
            {
                IWheelDevice device = null;
                try
                {
                    device = WheelDevices.Open(_settings, _log);
                    if (device == null)
                    {
                        Sleep(3000);
                        continue;
                    }
                    Drive(device);
                }
                catch (Exception e)
                {
                    _log?.Invoke("Wheel thread error: " + e);
                    Sleep(3000);
                }
                finally
                {
                    _connected = false;
                    device?.Dispose();
                }
            }
            SdlWheel.Shutdown();
        }

        private void Drive(IWheelDevice dev)
        {
            float range = dev.RangeDegrees ?? _settings.FallbackRangeDegrees;
            _deviceName = dev.Description;
            _log?.Invoke($"Using {dev.Description}, range {range}°");

            bool hasLevel = false;
            short lastLevel = 0;
            bool wasFollowing = false;
            dev.SetAutocenter(_settings.IdleAutocenter);

            Action<int> pressed = button => { if (button < _presses.Length) Interlocked.Increment(ref _presses[button]); };
            float position;
            do
            {
                if (!dev.Poll(pressed, out position)) return;
                Thread.Sleep(LoopSleepMs);
            } while (float.IsNaN(position) && _running);
            float lastAngle = position * range / 2f;
            long lastTick = Stopwatch.GetTimestamp();
            _connected = true;
            System.IO.StreamWriter log = null;
            string logPath = "";
            var logClock = Stopwatch.StartNew();

            try
            {
                while (_running)
                {
                    if (!dev.Poll(pressed, out position))
                    {
                        _log?.Invoke("Wheel disconnected");
                        return;
                    }

                    long now = Stopwatch.GetTimestamp();
                    float dt = Math.Max(1e-4f, (float)(now - lastTick) / Stopwatch.Frequency);
                    lastTick = now;
                    float angle = position * range / 2f;
                    float vel = _velocity + ((angle - lastAngle) / dt - _velocity) * Math.Min(1f, dt * 40f);
                    lastAngle = angle;
                    _angle = angle;
                    _velocity = vel;

                    bool follow = _follow;
                    if (follow != wasFollowing)
                    {
                        dev.SetAutocenter(follow ? 0f : _settings.IdleAutocenter);
                        wasFollowing = follow;
                        _force.Reset(_targetAngle, _targetVelocity);
                    }

                    long stamp = Interlocked.Read(ref _targetStamp);
                    bool newSample = stamp != _seenTargetStamp;
                    _seenTargetStamp = stamp;
                    float force = follow ? _force.Step(dt, angle, vel, _targetAngle, _targetVelocity, newSample, _forceCap) : 0f;
                    LastForce = force;
                    LastTarget = _force.Target;

                    if (_settings.LogPath != logPath)
                    {
                        log?.Dispose();
                        log = null;
                        logPath = _settings.LogPath;
                        if (!string.IsNullOrEmpty(logPath))
                        {
                            try
                            {
                                log = new System.IO.StreamWriter(logPath, append: false);
                                log.WriteLine("ms,follow,wheel,wheelVel,published,publishedVel,target,force");
                            }
                            catch (Exception e)
                            {
                                _log?.Invoke($"Cannot write log {logPath}: {e.Message}");
                            }
                        }
                    }
                    log?.WriteLine(FormattableString.Invariant(
                        $"{logClock.Elapsed.TotalMilliseconds:0.0},{(follow ? 1 : 0)},{angle:0.00},{vel:0.0},{_targetAngle:0.00},{_targetVelocity:0.0},{LastTarget:0.00},{force:0.000}"));
                    short level = (short)Math.Round(Clamp(force, -1f, 1f) * 32767f * (_settings.InvertForce ? -1f : 1f));
                    // Wheels resolve about 8 bits of force, so skip sub-step updates.
                    if (!hasLevel || (level >> 8) != (lastLevel >> 8))
                    {
                        if (dev.SetForce(level))
                        {
                            hasLevel = true;
                            lastLevel = level;
                        }
                    }

                    Thread.Sleep(LoopSleepMs);
                }
            }
            finally
            {
                log?.Dispose();
                dev.SetForce(0);
                dev.SetAutocenter(_settings.IdleAutocenter);
            }
        }

        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

        private void Sleep(int ms)
        {
            for (int t = 0; t < ms && _running; t += 100) Thread.Sleep(100);
        }

        public void Dispose()
        {
            _running = false;
            _thread.Join(1000);
        }
    }
}
