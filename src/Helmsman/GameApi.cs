using System;
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

        private GameApi(Type ship, Type player)
        {
            ShipType = ship;
            ApplyControlls = AccessTools.Method(ship, "ApplyControlls");
            _getRudderValue = AccessTools.Method(ship, "GetRudderValue");
            _getRudder = AccessTools.Method(ship, "GetRudder");
            _rudderSpeed = AccessTools.Field(ship, "m_rudderSpeed");
            _localPlayer = AccessTools.Field(player, "m_localPlayer");
            _getControlledShip = AccessTools.Method(player, "GetControlledShip");
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
                         (api._getRudder, "Ship.GetRudder"), (api._rudderSpeed, "Ship.m_rudderSpeed"),
                         (api._localPlayer, "Player.m_localPlayer"), (api._getControlledShip, "Player.GetControlledShip"),
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
    }
}
