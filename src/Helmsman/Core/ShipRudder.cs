using System;

namespace Helmsman.Core
{
    // Mirrors the rudder integration in Valheim's Ship.ApplyControlls(dir), one call per physics tick.
    internal static class ShipRudder
    {
        // Rudder change per tick at full input; Ship scales input by Lerp(0.5, 1, |rudder|).
        public static float MaxStep(float rudder, float rudderSpeed, float dt) => InputScale(rudder) * rudderSpeed * dt;

        // Ship.m_rudder for a given dir.x, which Ship.GetRudder() returns.
        public static float Input(float rudder, float x) => x * InputScale(rudder);

        public static float Apply(float rudder, float x, float rudderSpeed, float dt) =>
            Math.Max(-1f, Math.Min(1f, rudder + Input(rudder, x) * rudderSpeed * dt));

        private static float InputScale(float rudder) => 0.5f + 0.5f * Math.Min(1f, Math.Abs(rudder));
    }
}
