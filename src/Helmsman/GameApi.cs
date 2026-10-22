using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Helmsman
{
    // Valheim members used by the mod, resolved at runtime so the build needs no game assemblies.
    internal sealed class GameApi
    {
        public readonly Type ShipType;
        public readonly MethodInfo ApplyControlls;
        private readonly FieldInfo _localPlayer;
        private readonly MethodInfo _getControlledShip;
        private readonly MethodInfo _getRudderValue;
        private readonly MethodInfo _getRudder;
        private readonly FieldInfo _rudderSpeed;
        private readonly MethodInfo _setMouseLook;
        private readonly MethodInfo _getSpeed;
        private readonly Type _messageHud = AccessTools.TypeByName("MessageHud");

        private GameApi(Type ship, Type player)
        {
            ShipType = ship;
            ApplyControlls = AccessTools.Method(ship, "ApplyControlls");
            _getRudderValue = AccessTools.Method(ship, "GetRudderValue");
            _getRudder = AccessTools.Method(ship, "GetRudder");
            _rudderSpeed = AccessTools.Field(ship, "m_rudderSpeed");
            _getSpeed = AccessTools.Method(ship, "GetSpeed");
            _localPlayer = AccessTools.Field(player, "m_localPlayer");
            _getControlledShip = AccessTools.Method(player, "GetControlledShip");
            _setMouseLook = AccessTools.Method(player, "SetMouseLook");
        }

        // Returns null and reports what is missing if this Valheim version doesn't match.
        public static GameApi Resolve(Action<string> logError)
        {
            var ship = AccessTools.TypeByName("Ship");
            var player = AccessTools.TypeByName("Player");
            if (ship == null || player == null)
            {
                logError("Valheim's Ship/Player types not found.");
                return null;
            }
            var api = new GameApi(ship, player);
            foreach (var (member, name) in new (MemberInfo, string)[]
                     {
                         (api.ApplyControlls, "Ship.ApplyControlls"), (api._getRudderValue, "Ship.GetRudderValue"),
                         (api._getRudder, "Ship.GetRudder"), (api._rudderSpeed, "Ship.m_rudderSpeed"), (api._getSpeed, "Ship.GetSpeed"),
                         (api._localPlayer, "Player.m_localPlayer"), (api._getControlledShip, "Player.GetControlledShip"),
                         (api._setMouseLook, "Player.SetMouseLook"),
                     })
            {
                if (member == null)
                {
                    logError($"{name} not found; this Valheim version is not supported.");
                    return null;
                }
            }
            return api;
        }

        // The ship the local player is steering, or null.
        public object ControlledShip()
        {
            var player = _localPlayer.GetValue(null) as UnityEngine.Object;
            return player ? _getControlledShip.Invoke(player, null) : null;
        }

        public float RudderValue(object ship) => (float)_getRudderValue.Invoke(ship, null);

        // Current rudder input (dir.x scaled by Ship's speed factor), as last applied.
        public float RudderInput(object ship) => (float)_getRudder.Invoke(ship, null);

        public float RudderSpeed(object ship) => (float)_rudderSpeed.GetValue(ship);

        // Forward speed in m/s (negative when going backward).
        public float Speed(object ship) => (float)_getSpeed.Invoke(ship, null);

        // Turns the local player's view by this many degrees of yaw, as if the mouse had moved.
        public void TurnView(float yawDegrees)
        {
            var player = _localPlayer.GetValue(null) as UnityEngine.Object;
            if (player) _setMouseLook.Invoke(player, new object[] { new UnityEngine.Vector2(yawDegrees, 0f) });
        }

        // Shows a short message in the top-left corner; does nothing if this Valheim version has no MessageHud.
        public void ShowMessage(string text)
        {
            try
            {
                var hud = AccessTools.Property(_messageHud, "instance")?.GetValue(null) as UnityEngine.Object;
                var show = AccessTools.Method(_messageHud, "ShowMessage");
                if (!hud || show == null) return;
                var args = show.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
                args[0] = Enum.ToObject(show.GetParameters()[0].ParameterType, 1);
                args[1] = text;
                show.Invoke(hud, args);
            }
            catch (Exception)
            {
                // Messages are a nicety; never let them break steering.
            }
        }

        // The ship's heading in degrees, ignoring roll and pitch.
        public static float Heading(object ship)
        {
            var forward = ((UnityEngine.Component)ship).transform.forward;
            return UnityEngine.Mathf.Atan2(forward.x, forward.z) * UnityEngine.Mathf.Rad2Deg;
        }
    }
}
