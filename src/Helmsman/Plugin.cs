using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Helmsman.Core;
using Helmsman.Wheel;
using UnityEngine;

namespace Helmsman
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInProcess("valheim")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<string> _devicePath;
        private ConfigEntry<float> _fallbackRange;
        private ConfigEntry<float> _degreesAtFullRudder;
        private ConfigEntry<bool> _invertSteering;
        private ConfigEntry<float> _syncTolerance;
        private ConfigEntry<float> _maxForce;
        private ConfigEntry<float> _stiffness;
        private ConfigEntry<float> _damping;
        private ConfigEntry<float> _deadband;
        private ConfigEntry<float> _minForce;
        private ConfigEntry<bool> _invertForce;
        private ConfigEntry<float> _idleCenterForce;
        private ConfigEntry<float> _idleCenterDelay;
        private ConfigEntry<float> _idleAutocenter;
        private ConfigEntry<string> _speedUpButtons;
        private ConfigEntry<string> _speedDownButtons;
        private ConfigEntry<string> _centerButtons;
        private ConfigEntry<string> _logPath;

        private readonly WheelSettings _wheelSettings = new WheelSettings();
        private readonly HelmSettings _helmSettings = new HelmSettings();
        private int[] _speedUpCodes = new int[0];
        private int[] _speedDownCodes = new int[0];
        private int[] _centerCodes = new int[0];

        private GameApi _game;
        private WheelController _wheel;
        private HelmController _helm;
        private Harmony _harmony;
        private object _helmShip;
        private float _idleSince = float.NegativeInfinity;

        private void Awake()
        {
            Instance = this;
            _enabled = Config.Bind("General", "Enabled", true, "Steer ships with the racing wheel.");
            _devicePath = Config.Bind("Device", "DevicePath", "", "evdev device (e.g. /dev/input/event24). Empty = auto-detect the first force-feedback wheel.");
            _fallbackRange = Config.Bind("Device", "FallbackRangeDegrees", 900f, "Wheel rotation range used if the driver does not report one.");
            _degreesAtFullRudder = Config.Bind("Steering", "DegreesAtFullRudder", 450f, "Wheel angle (from center) that equals full rudder.");
            _invertSteering = Config.Bind("Steering", "InvertSteering", false, "Swap left/right steering.");
            _syncTolerance = Config.Bind("Steering", "SyncToleranceDegrees", 15f, "When taking the helm, the wheel must come within this many degrees of the rudder before it takes over.");
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
            _speedDownButtons = Config.Bind("Buttons", "SpeedDownButtons", "293,288", "Comma-separated evdev key codes that lower sail speed (G29: left paddle = 293, cross = 288).");
            _centerButtons = Config.Bind("Buttons", "CenterButtons", "294,295,298,299", "Comma-separated evdev key codes that swing the rudder (and wheel) back to straight (G29: R2 = 294, L2 = 295, R3 = 298, L3 = 299).");
            _logPath = Config.Bind("Debug", "LogPath", "", "If set, writes a CSV of wheel position, rudder target and force (~500 lines/s) to this file.");

            ApplySettings();
            Config.SettingChanged += (_, __) => ApplySettings();

            _game = GameApi.Resolve(msg => Logger.LogError(msg));
            if (_game == null) return;
            _helm = new HelmController(_helmSettings);
            _wheel = new WheelController(_wheelSettings, msg => Logger.LogInfo(msg));
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            _harmony.Patch(_game.ApplyControlls,
                prefix: new HarmonyMethod(typeof(ShipApplyControllsPatch), nameof(ShipApplyControllsPatch.Prefix)),
                postfix: new HarmonyMethod(typeof(ShipApplyControllsPatch), nameof(ShipApplyControllsPatch.Postfix)));
        }

        private void ApplySettings()
        {
            _wheelSettings.DevicePath = _devicePath.Value;
            _wheelSettings.FallbackRangeDegrees = _fallbackRange.Value;
            _wheelSettings.MaxForce = _maxForce.Value;
            _wheelSettings.Stiffness = _stiffness.Value;
            _wheelSettings.Damping = _damping.Value;
            _wheelSettings.DeadbandDegrees = _deadband.Value;
            _wheelSettings.MinForce = _minForce.Value;
            _wheelSettings.InvertForce = _invertForce.Value;
            _wheelSettings.IdleAutocenter = _idleAutocenter.Value;
            _wheelSettings.LogPath = _logPath.Value;
            _helmSettings.DegreesAtFullRudder = _degreesAtFullRudder.Value;
            _helmSettings.InvertSteering = _invertSteering.Value;
            _helmSettings.SyncToleranceDegrees = _syncTolerance.Value;
            _speedUpCodes = ButtonCodes.Parse(_speedUpButtons.Value);
            _speedDownCodes = ButtonCodes.Parse(_speedDownButtons.Value);
            _centerCodes = ButtonCodes.Parse(_centerButtons.Value);
        }

        private int TakePresses(int[] codes)
        {
            int n = 0;
            foreach (var code in codes) n += _wheel.TakePresses(code);
            return n;
        }

        private void Update()
        {
            if (_game == null) return;
            if (_enabled.Value && _game.ControlledShip() != null) return;

            if (_helmShip != null) _idleSince = Time.unscaledTime;
            _helmShip = null;
            _helm.Reset();
            _wheel.ClearPresses();
            bool delayOver = Time.unscaledTime - _idleSince >= _idleCenterDelay.Value;
            if (_enabled.Value && _idleCenterForce.Value > 0f && delayOver) _wheel.Follow(0f, 0f, _idleCenterForce.Value);
            else _wheel.StopFollowing();
        }

        internal void BeforeShipControls(object ship, ref Vector3 dir)
        {
            if (!_enabled.Value || !_wheel.Connected || _game.ControlledShip() != ship) return;
            if (ship != _helmShip)
            {
                _helmShip = ship;
                _helm.Reset();
                _wheel.ClearPresses();
            }

            var input = new HelmInput
            {
                WheelDegrees = _wheel.AngleDegrees,
                Rudder = _game.RudderValue(ship),
                RudderSpeed = _game.RudderSpeed(ship),
                DeltaTime = Time.fixedDeltaTime,
                ManualX = dir.x,
                ManualZ = dir.z,
                CenterPressed = TakePresses(_centerCodes) > 0,
                SpeedSteps = TakePresses(_speedUpCodes) - TakePresses(_speedDownCodes),
            };
            _helm.Step(input, out dir.x, out dir.z);
        }

        internal void AfterShipControls(object ship)
        {
            if (!_enabled.Value || ship != _helmShip) return;
            _helm.WheelTargetFor(_game.RudderValue(ship), _game.RudderInput(ship), _game.RudderSpeed(ship), out var angle, out var velocity);
            _wheel.Follow(angle, velocity);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _wheel?.Dispose();
            Instance = null;
        }
    }

    // Applied to Ship.ApplyControlls(Vector3 dir) in Plugin.Awake.
    internal static class ShipApplyControllsPatch
    {
        internal static void Prefix(object __instance, ref Vector3 dir) => Plugin.Instance?.BeforeShipControls(__instance, ref dir);

        internal static void Postfix(object __instance) => Plugin.Instance?.AfterShipControls(__instance);
    }
}
