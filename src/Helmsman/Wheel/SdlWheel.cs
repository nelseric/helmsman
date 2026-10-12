using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Helmsman.Wheel
{
    // A force-feedback wheel opened through SDL3 (DirectInput or SDL's own Logitech driver on Windows, IOKit on macOS).
    internal sealed class SdlWheel : IWheelDevice
    {
        private static int? _version;
        private static bool _loadAttempted, _initialized;

        private IntPtr _joystick;
        private IntPtr _haptic;
        private readonly uint _features;
        private readonly bool[] _buttonDown;
        private readonly byte[] _effect = new byte[Sdl.HapticEffectSize];
        private int _effectId = -1;
        private readonly short _initialAxis;
        private bool _axisKnown;
        private readonly System.Diagnostics.Stopwatch _probe = new System.Diagnostics.Stopwatch();

        public string Name { get; }
        public string Description { get; }
        // SDL doesn't report rotation range; SDL's own Logitech driver sets 900°.
        public float? RangeDegrees => null;

        private SdlWheel(IntPtr joystick, IntPtr haptic, string name)
        {
            _joystick = joystick;
            _haptic = haptic;
            Name = name;
            _features = Sdl.SDL_GetHapticFeatures(haptic);
            _buttonDown = new bool[Math.Max(0, Sdl.SDL_GetNumJoystickButtons(joystick))];
            Description = $"{name} ({Sdl.SDL_GetJoystickVendor(joystick):x4}:{Sdl.SDL_GetJoystickProduct(joystick):x4}) via SDL {Sdl.VersionString(_version ?? 0)}";
            if ((_features & Sdl.HAPTIC_GAIN) != 0) Sdl.SDL_SetHapticGain(haptic, 100);
            _initialAxis = Sdl.SDL_GetJoystickAxis(joystick, 0);
        }

        // Opens the first joystick with constant-force support whose name contains nameFilter (any if empty), preferring wheels.
        public static SdlWheel Open(string nameFilter, Action<string> log)
        {
            if (!Init(log)) return null;
            Sdl.SDL_UpdateJoysticks();
            var candidates = new List<(SdlWheel wheel, bool isWheel)>();
            foreach (uint id in JoystickIds())
            {
                IntPtr joystick = Sdl.SDL_OpenJoystick(id);
                if (joystick == IntPtr.Zero) continue;
                string name = Marshal.PtrToStringUTF8(Sdl.SDL_GetJoystickName(joystick)) ?? "";
                IntPtr haptic = IntPtr.Zero;
                bool usable = (string.IsNullOrEmpty(nameFilter) || name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                              && Sdl.SDL_GetNumJoystickAxes(joystick) > 0
                              && Sdl.SDL_IsJoystickHaptic(joystick)
                              && (haptic = Sdl.SDL_OpenHapticFromJoystick(joystick)) != IntPtr.Zero
                              && (Sdl.SDL_GetHapticFeatures(haptic) & Sdl.HAPTIC_CONSTANT) != 0;
                if (!usable)
                {
                    if (haptic != IntPtr.Zero) Sdl.SDL_CloseHaptic(haptic);
                    Sdl.SDL_CloseJoystick(joystick);
                    continue;
                }
                bool isWheel = Sdl.SDL_GetJoystickType(joystick) == Sdl.JOYSTICK_TYPE_WHEEL || name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0;
                candidates.Add((new SdlWheel(joystick, haptic, name), isWheel));
            }
            var pick = candidates.Where(c => c.isWheel).Select(c => c.wheel).FirstOrDefault() ?? candidates.Select(c => c.wheel).FirstOrDefault();
            foreach (var c in candidates.Where(c => c.wheel != pick)) c.wheel.Dispose();
            return pick;
        }

        private static bool Init(Action<string> log)
        {
            if (_initialized) return true;
            if (!_loadAttempted)
            {
                _loadAttempted = true;
                _version = Sdl.Load();
                if (_version == null) log?.Invoke("SDL3 library not found; put SDL3.dll next to Helmsman.dll");
            }
            if (_version == null) return false;
            // The game window has focus, not us; keep reading the wheel and pump joystick messages on SDL's own thread.
            Sdl.SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            Sdl.SDL_SetHint("SDL_JOYSTICK_THREAD", "1");
            if (!Sdl.SDL_InitSubSystem(Sdl.INIT_JOYSTICK | Sdl.INIT_HAPTIC))
            {
                log?.Invoke("SDL3 init failed: " + Sdl.Error());
                _version = null;
                return false;
            }
            _initialized = true;
            return true;
        }

        // Shuts SDL down; call from the wheel thread after closing the wheel.
        public static void Shutdown()
        {
            if (!_initialized) return;
            Sdl.SDL_QuitSubSystem(Sdl.INIT_JOYSTICK | Sdl.INIT_HAPTIC);
            _initialized = false;
        }

        private static uint[] JoystickIds()
        {
            IntPtr list = Sdl.SDL_GetJoysticks(out int count);
            if (list == IntPtr.Zero) return new uint[0];
            var ids = new int[Math.Max(0, count)];
            Marshal.Copy(list, ids, 0, ids.Length);
            Sdl.SDL_free(list);
            return ids.Select(i => (uint)i).ToArray();
        }

        public bool Poll(Action<int> buttonPressed, out float position)
        {
            Sdl.SDL_UpdateJoysticks();
            if (!Sdl.SDL_JoystickConnected(_joystick))
            {
                position = 0f;
                return false;
            }
            for (int i = 0; i < _buttonDown.Length; i++)
            {
                bool down = Sdl.SDL_GetJoystickButton(_joystick, i);
                if (down && !_buttonDown[i]) buttonPressed(i);
                _buttonDown[i] = down;
            }
            short axis = Sdl.SDL_GetJoystickAxis(_joystick, 0);
            if (!_axisKnown && !ProbeAxis(axis))
            {
                position = float.NaN;
                return true;
            }
            position = (axis + 0.5f) / 32767.5f;
            return true;
        }

        // SDL's Logitech driver only learns the angle from a movement report, so nudge the wheel back and forth until one arrives.
        private bool ProbeAxis(short axis)
        {
            const long ProbeMs = 1000, FlipMs = 60;
            if (!_probe.IsRunning) _probe.Start();
            long ms = _probe.ElapsedMilliseconds;
            if (axis == _initialAxis && ms < ProbeMs)
            {
                SetForce((short)(ms / FlipMs % 2 == 0 ? 8000 : -8000));
                return false;
            }
            _axisKnown = true;
            if (_effectId >= 0) SetForce(0);
            return true;
        }

        public bool SetForce(short level)
        {
            // A positive steering-axis force turns the wheel counterclockwise in SDL's Linux and Logitech drivers.
            Sdl.WriteSteeringConstant(_effect, (short)-Math.Max((short)-32767, level));
            if (_effectId >= 0) return Sdl.SDL_UpdateHapticEffect(_haptic, _effectId, _effect);
            int id = Sdl.SDL_CreateHapticEffect(_haptic, _effect);
            if (id < 0) return false;
            if (!Sdl.SDL_RunHapticEffect(_haptic, id, 1))
            {
                Sdl.SDL_DestroyHapticEffect(_haptic, id);
                return false;
            }
            _effectId = id;
            return true;
        }

        public void SetAutocenter(float strength)
        {
            if ((_features & Sdl.HAPTIC_AUTOCENTER) != 0) Sdl.SDL_SetHapticAutocenter(_haptic, (int)(Math.Max(0f, Math.Min(1f, strength)) * 100f));
        }

        public void Dispose()
        {
            if (_haptic != IntPtr.Zero)
            {
                if (_effectId >= 0)
                {
                    Sdl.SDL_StopHapticEffect(_haptic, _effectId);
                    Sdl.SDL_DestroyHapticEffect(_haptic, _effectId);
                }
                Sdl.SDL_CloseHaptic(_haptic);
                _haptic = IntPtr.Zero;
            }
            if (_joystick != IntPtr.Zero) Sdl.SDL_CloseJoystick(_joystick);
            _joystick = IntPtr.Zero;
        }
    }
}
