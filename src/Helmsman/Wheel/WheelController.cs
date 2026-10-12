using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

        private readonly int[] _presses = new int[0x300];

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

        // Returns and clears the number of presses of an evdev key code since the last call.
        public int TakePresses(int code) => code >= 0 && code < _presses.Length ? Interlocked.Exchange(ref _presses[code], 0) : 0;

        public void ClearPresses()
        {
            for (int i = 0; i < _presses.Length; i++) _presses[i] = 0;
        }

        private void Run()
        {
            while (_running)
            {
                WheelDevice device = null;
                try
                {
                    device = WheelDevice.Open(_settings.DevicePath, _log);
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
        }

        private void Drive(WheelDevice dev)
        {
            float range = dev.SysfsRangeDegrees ?? _settings.FallbackRangeDegrees;
            _deviceName = dev.Name;
            _log?.Invoke($"Using {dev.Name} ({dev.Vendor:x4}:{dev.Product:x4}) at {dev.Path}, range {range}°");

            Evdev.WriteEvent(dev.Fd, Evdev.EV_FF, Evdev.FF_GAIN, 0xFFFF);
            int effectId = -1;
            short lastLevel = 0;
            bool wasFollowing = false;
            SetAutocenter(dev, _settings.IdleAutocenter);

            int rawX = dev.InitialX;
            float mid = (dev.AbsMin + dev.AbsMax) / 2f;
            float half = Math.Max(1f, (dev.AbsMax - dev.AbsMin) / 2f);
            float degPerUnit = range / 2f / half;
            float lastAngle = (rawX - mid) * degPerUnit;
            long lastTick = Stopwatch.GetTimestamp();
            var buf = new byte[Evdev.InputEventSize * 64];
            _connected = true;
            System.IO.StreamWriter log = null;
            string logPath = "";
            var logClock = Stopwatch.StartNew();

            try
            {
                while (_running)
                {
                    // Drain pending input events.
                    while (true)
                    {
                        long n = (long)Evdev.read(dev.Fd, buf, (IntPtr)buf.Length);
                        if (n < 0)
                        {
                            int errno = Marshal.GetLastWin32Error();
                            if (errno == Evdev.EAGAIN) break;
                            _log?.Invoke($"Wheel disconnected (errno {errno})");
                            return;
                        }
                        if (n == 0) break;
                        for (int off = 0; off + Evdev.InputEventSize <= n; off += Evdev.InputEventSize)
                        {
                            ushort type = BitConverter.ToUInt16(buf, off + 16);
                            ushort code = BitConverter.ToUInt16(buf, off + 18);
                            int value = BitConverter.ToInt32(buf, off + 20);
                            if (type == Evdev.EV_ABS && code == Evdev.ABS_X) rawX = value;
                            else if (type == Evdev.EV_KEY && value == 1 && code < _presses.Length) Interlocked.Increment(ref _presses[code]);
                        }
                    }

                    long now = Stopwatch.GetTimestamp();
                    float dt = Math.Max(1e-4f, (float)(now - lastTick) / Stopwatch.Frequency);
                    lastTick = now;
                    float angle = (rawX - mid) * degPerUnit;
                    float vel = _velocity + ((angle - lastAngle) / dt - _velocity) * Math.Min(1f, dt * 40f);
                    lastAngle = angle;
                    _angle = angle;
                    _velocity = vel;

                    bool follow = _follow;
                    if (follow != wasFollowing)
                    {
                        SetAutocenter(dev, follow ? 0f : _settings.IdleAutocenter);
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
                    // The driver only resolves 8 bits of force, so skip sub-step updates.
                    if (effectId < 0 || (level >> 8) != (lastLevel >> 8))
                    {
                        int id = Evdev.UploadConstant(dev.Fd, effectId, level);
                        if (id >= 0)
                        {
                            if (effectId < 0) Evdev.WriteEvent(dev.Fd, Evdev.EV_FF, (ushort)id, 1);
                            effectId = id;
                            lastLevel = level;
                        }
                    }

                    Thread.Sleep(LoopSleepMs);
                }
            }
            finally
            {
                log?.Dispose();
                if (effectId >= 0)
                {
                    Evdev.WriteEvent(dev.Fd, Evdev.EV_FF, (ushort)effectId, 0);
                    Evdev.ioctl(dev.Fd, Evdev.EVIOCRMFF, (IntPtr)effectId);
                }
                SetAutocenter(dev, _settings.IdleAutocenter);
            }
        }

        private static void SetAutocenter(WheelDevice dev, float strength) =>
            Evdev.WriteEvent(dev.Fd, Evdev.EV_FF, Evdev.FF_AUTOCENTER, (int)(Clamp(strength, 0f, 1f) * 0xFFFF));

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
