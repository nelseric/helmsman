using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Helmsman.Wheel;
using UnityEngine;

namespace Helmsman
{
    [BepInPlugin(Guid, "Helmsman", Version)]
    [BepInProcess("valheim")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "helmsman.wheel";
        public const string Version = "0.1.0";

        internal static Plugin Instance;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<string> _devicePath;
        private ConfigEntry<float> _degreesAtFullRudder;
        private ConfigEntry<bool> _invertSteering;
        private ConfigEntry<float> _syncTolerance;
        private ConfigEntry<float> _fallbackRange;
        private ConfigEntry<float> _maxForce;
        private ConfigEntry<float> _stiffness;
        private ConfigEntry<float> _damping;
        private ConfigEntry<float> _deadband;
        private ConfigEntry<float> _minForce;
        private ConfigEntry<bool> _invertForce;
        private ConfigEntry<float> _idleAutocenter;
        private ConfigEntry<float> _idleCenterForce;
        private ConfigEntry<float> _idleCenterDelay;
        private float _idleSince = float.NegativeInfinity;
        private ConfigEntry<string> _speedUpButtons;
        private ConfigEntry<string> _speedDownButtons;
        private int[] _speedUpCodes = new int[0];
        private int[] _speedDownCodes = new int[0];
        private ConfigEntry<string> _centerButtons;
        private int[] _centerCodes = new int[0];
        private bool _centering;
        private ConfigEntry<string> _logPath;

        private readonly WheelController.Settings _settings = new WheelController.Settings();
        private WheelController _wheel;
        private Harmony _harmony;

        private Ship _helmShip;
        private bool _synced;
        private bool _keyboardResync;
        private int _pendingSpeedSteps;
        private bool _injectedSpeedLastTick;

        private void Awake()
        {
            Instance = this;
            _enabled = Config.Bind("General", "Enabled", true, "Steer ships with the racing wheel.");
            _devicePath = Config.Bind("Device", "DevicePath", "", "evdev device (e.g. /dev/input/event24). Empty = auto-detect the first force-feedback wheel.");
            _fallbackRange = Config.Bind("Device", "FallbackRangeDegrees", 900f, "Wheel rotation range used if the driver does not report one.");
            _degreesAtFullRudder = Config.Bind("Steering", "DegreesAtFullRudder", 450f, "Wheel angle (from center) that equals full rudder.");
            _invertSteering = Config.Bind("Steering", "InvertSteering", false, "Swap left/right steering.");
            _syncTolerance = Config.Bind("Steering", "SyncToleranceDegrees", 15f, "After taking the helm or steering by keyboard, the wheel must come within this many degrees of the rudder before it takes over.");
            _maxForce = Config.Bind("ForceFeedback", "MaxForce", 0.7f, new ConfigDescription("Maximum force (fraction of the wheel's peak torque).", new AcceptableValueRange<float>(0f, 1f)));
            _stiffness = Config.Bind("ForceFeedback", "Stiffness", 0.03f, "Force per degree the wheel is away from the rudder.");
            _damping = Config.Bind("ForceFeedback", "Damping", 0.0015f, "Force per degree/second the wheel moves faster than the rudder.");
            _deadband = Config.Bind("ForceFeedback", "DeadbandDegrees", 0.5f, "Position error ignored by the spring, to avoid buzzing.");
            _minForce = Config.Bind("ForceFeedback", "MinForce", 0.12f, new ConfigDescription("Extra force to overcome wheel friction once it is a few degrees off the rudder.", new AcceptableValueRange<float>(0f, 0.5f)));
            _invertForce = Config.Bind("ForceFeedback", "InvertForce", false, "Flip force direction if the wheel is pushed away from the rudder instead of toward it.");
            _idleCenterForce = Config.Bind("ForceFeedback", "IdleCenterForce", 0.5f, new ConfigDescription("Max force holding the wheel centered when not at the helm. 0 = use IdleAutocenter instead.", new AcceptableValueRange<float>(0f, 1f)));
            _idleCenterDelay = Config.Bind("ForceFeedback", "IdleCenterDelaySeconds", 3f, "After leaving the helm, the wheel stays free this long before it is centered.");
            _idleAutocenter = Config.Bind("ForceFeedback", "IdleAutocenter", 0.25f, new ConfigDescription("Driver's built-in centering spring when not at the helm and IdleCenterForce is 0 (0 = limp).", new AcceptableValueRange<float>(0f, 1f)));
            _speedUpButtons = Config.Bind("Buttons", "SpeedUpButtons", "292,290", "Comma-separated evdev key codes that raise sail speed (G29: right paddle = 292, circle = 290).");
            _centerButtons = Config.Bind("Buttons", "CenterButtons", "294,295,298,299", "Comma-separated evdev key codes that swing the rudder (and wheel) back to straight (G29: R2 = 294, L2 = 295, R3 = 298, L3 = 299).");
            _speedDownButtons = Config.Bind("Buttons", "SpeedDownButtons", "293,288", "Comma-separated evdev key codes that lower sail speed (G29: left paddle = 293, cross = 288).");

            _logPath = Config.Bind("Debug", "LogPath", "", "If set, writes a CSV of wheel position, rudder target and force (~500 lines/s) to this file.");

            ApplySettings();
            Config.SettingChanged += (_, __) => ApplySettings();

            _wheel = new WheelController(_settings, msg => Logger.LogInfo(msg));
            _harmony = Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, Guid);
        }

        private void ApplySettings()
        {
            _settings.DevicePath = _devicePath.Value;
            _settings.FallbackRangeDegrees = _fallbackRange.Value;
            _settings.MaxForce = _maxForce.Value;
            _settings.Stiffness = _stiffness.Value;
            _settings.Damping = _damping.Value;
            _settings.DeadbandDegrees = _deadband.Value;
            _settings.MinForce = _minForce.Value;
            _settings.InvertForce = _invertForce.Value;
            _settings.IdleAutocenter = _idleAutocenter.Value;
            _settings.LogPath = _logPath.Value;
            _speedUpCodes = ParseCodes(_speedUpButtons.Value);
            _speedDownCodes = ParseCodes(_speedDownButtons.Value);
            _centerCodes = ParseCodes(_centerButtons.Value);
        }

        private static int[] ParseCodes(string list)
        {
            var codes = new System.Collections.Generic.List<int>();
            foreach (var part in list.Split(','))
                if (int.TryParse(part.Trim(), out var code)) codes.Add(code);
            return codes.ToArray();
        }

        private int TakePresses(int[] codes)
        {
            int n = 0;
            foreach (var code in codes) n += _wheel.TakePresses(code);
            return n;
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (!_enabled.Value || player == null || player.GetControlledShip() == null)
            {
                if (_helmShip != null) _idleSince = Time.unscaledTime;
                bool delayOver = Time.unscaledTime - _idleSince >= _idleCenterDelay.Value;
                if (_enabled.Value && _idleCenterForce.Value > 0f && delayOver) _wheel.Follow(0f, 0f, _idleCenterForce.Value);
                else _wheel.StopFollowing();
                _wheel.ClearPresses();
                _helmShip = null;
                _pendingSpeedSteps = 0;
                _centering = false;
            }
        }

        private const float CenteredWheelDegrees = 3f;
        private const float KeyboardResyncDegrees = 2f;

        private float SteerSign => _invertSteering.Value ? -1f : 1f;

        internal void BeforeShipControls(Ship ship, ref Vector3 dir)
        {
            if (!_enabled.Value || !_wheel.Connected) return;
            var player = Player.m_localPlayer;
            if (player == null || player.GetControlledShip() != ship) return;

            if (ship != _helmShip)
            {
                _helmShip = ship;
                _synced = false;
                _keyboardResync = false;
                _centering = false;
                _pendingSpeedSteps = 0;
                _wheel.ClearPresses();
            }

            float rudder = ship.GetRudderValue();
            float target = Mathf.Clamp(_wheel.AngleDegrees / _degreesAtFullRudder.Value * SteerSign, -1f, 1f);
            bool manualSteer = Mathf.Abs(dir.x) > 0.01f;
            if (TakePresses(_centerCodes) > 0) _centering = true;
            if (_centering)
            {
                // Steer the rudder to straight; the wheel follows it. Grabbing the wheel away from the rudder cancels.
                bool grabbed = Mathf.Abs(target - rudder) * _degreesAtFullRudder.Value > _syncTolerance.Value;
                bool arrived = Mathf.Abs(rudder) < 0.001f && Mathf.Abs(_wheel.AngleDegrees) < CenteredWheelDegrees;
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
                float tolerance = _keyboardResync ? KeyboardResyncDegrees : _syncTolerance.Value;
                if (!_synced && Mathf.Abs(target - rudder) * _degreesAtFullRudder.Value <= tolerance)
                {
                    _synced = true;
                    _keyboardResync = false;
                }
                if (_synced)
                {
                    // Mirrors Ship.ApplyControlls so the rudder lands exactly on the target without overshoot.
                    float step = Mathf.Lerp(0.5f, 1f, Mathf.Abs(rudder)) * ship.m_rudderSpeed * Time.fixedDeltaTime;
                    float delta = target - rudder;
                    dir.x = Mathf.Abs(delta) < 0.001f || step <= 0f ? 0f : Mathf.Clamp(delta / step, -1f, 1f);
                }
            }

            _pendingSpeedSteps += TakePresses(_speedUpCodes) - TakePresses(_speedDownCodes);
            // Ship only reacts to a fresh press, so leave a released tick between injected presses.
            if (_injectedSpeedLastTick)
            {
                _injectedSpeedLastTick = false;
            }
            else if (_pendingSpeedSteps != 0 && Mathf.Abs(dir.z) < 0.5f)
            {
                int step = _pendingSpeedSteps > 0 ? 1 : -1;
                dir.z = step;
                _pendingSpeedSteps -= step;
                _injectedSpeedLastTick = true;
            }
        }

        internal void AfterShipControls(Ship ship)
        {
            if (!_enabled.Value || ship != _helmShip) return;
            float rudder = ship.GetRudderValue();
            float rate = ship.GetRudder() * ship.m_rudderSpeed;
            if (Mathf.Abs(rudder) >= 1f && Mathf.Sign(rate) == Mathf.Sign(rudder)) rate = 0f;
            float scale = _degreesAtFullRudder.Value * SteerSign;
            _wheel.Follow(rudder * scale, rate * scale);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _wheel?.Dispose();
            Instance = null;
        }
    }

    [HarmonyPatch(typeof(Ship), nameof(Ship.ApplyControlls))]
    internal static class ShipApplyControllsPatch
    {
        private static void Prefix(Ship __instance, ref Vector3 dir) => Plugin.Instance?.BeforeShipControls(__instance, ref dir);

        private static void Postfix(Ship __instance) => Plugin.Instance?.AfterShipControls(__instance);
    }
}
