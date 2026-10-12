using System;
using Helmsman.Core;
using Xunit;

namespace Helmsman.Tests
{
    public class HelmControllerTests
    {
        private const float Dt = 0.02f;
        private const float RudderSpeed = 0.5f;
        private const float FullDeg = 450f;

        private static HelmController NewHelm(bool invert = false) =>
            new HelmController(new HelmSettings { DegreesAtFullRudder = FullDeg, InvertSteering = invert, SyncToleranceDegrees = 15f });

        private static HelmInput Input(float wheelDeg, float rudder, float manualX = 0f, float manualZ = 0f, bool center = false, int speedSteps = 0, float wheelVel = 0f) =>
            new HelmInput
            {
                WheelDegrees = wheelDeg, WheelVelocity = wheelVel, Rudder = rudder, RudderSpeed = RudderSpeed, DeltaTime = Dt,
                ManualX = manualX, ManualZ = manualZ, CenterPressed = center, SpeedSteps = speedSteps,
            };

        [Fact]
        public void Rudder_ReachesWheelTarget_WithoutOvershoot()
        {
            var helm = NewHelm();
            helm.Step(Input(0f, 0f), out _, out _);
            float rudder = 0f, previous = 0f;
            for (int i = 0; i < 200; i++)
            {
                helm.Step(Input(90f, rudder), out float x, out _);
                rudder = ShipRudder.Apply(rudder, x, RudderSpeed, Dt);
                Assert.True(rudder >= previous, "rudder should approach the target monotonically");
                Assert.True(rudder <= 0.2f + 1e-4f, "rudder should not overshoot");
                previous = rudder;
            }
            Assert.Equal(0.2f, rudder, 3);
        }

        [Fact]
        public void FarFromTarget_SteersAtFullGameRate()
        {
            var helm = NewHelm();
            helm.Step(Input(0.3f * FullDeg, 0.3f), out _, out _);
            helm.Step(Input(0.3f * FullDeg + 50f, 0.3f), out float x, out _);
            Assert.Equal(1f, x);
        }

        [Fact]
        public void TakingHelm_WaitsUntilWheelIsNearRudder()
        {
            var helm = NewHelm();
            helm.Step(Input(90f, -0.5f), out float x, out _);
            Assert.Equal(0f, x);
            Assert.False(helm.Synced);

            helm.Step(Input(-0.5f * FullDeg + 10f, -0.5f), out x, out _);
            Assert.True(helm.Synced);
            Assert.True(x > 0f);
        }

        [Fact]
        public void Keyboard_OverridesWheel_ThenWaitsForWheelWithinTwoDegrees()
        {
            var helm = NewHelm();
            helm.Step(Input(0f, 0f), out _, out _);
            Assert.True(helm.Synced);

            helm.Step(Input(0f, 0f, manualX: 1f), out float x, out _);
            Assert.Equal(1f, x);
            Assert.False(helm.Synced);

            // Released with the wheel still catching up 5° behind: rudder holds rather than stepping back.
            helm.Step(Input(0.2f * FullDeg - 5f, 0.2f, wheelVel: 150f), out x, out _);
            Assert.Equal(0f, x);
            Assert.False(helm.Synced);

            helm.Step(Input(0.2f * FullDeg - 1f, 0.2f, wheelVel: 150f), out x, out _);
            Assert.True(helm.Synced);
        }

        [Fact]
        public void AfterKeyboard_WheelRestingFewDegreesOff_TakesOver()
        {
            // Gearbox friction can stop the wheel a few degrees short of the rudder; it must still take over.
            var helm = NewHelm();
            helm.Step(Input(0f, 0.2f, manualX: 1f), out _, out _);
            helm.Step(Input(0.2f * FullDeg - 4f, 0.2f, wheelVel: 150f), out _, out _);
            Assert.False(helm.Synced);
            helm.Step(Input(0.2f * FullDeg - 3f, 0.2f, wheelVel: 0f), out _, out _);
            Assert.True(helm.Synced);
        }

        [Fact]
        public void AfterKeyboard_WheelTurnedQuicklyThroughRudder_TakesOver()
        {
            // Turning fast can skip over the tolerance window between ticks; passing through the rudder counts.
            var helm = NewHelm();
            helm.Step(Input(0f, 0.2f, manualX: 1f), out _, out _);
            helm.Step(Input(0.2f * FullDeg - 6f, 0.2f, wheelVel: 300f), out _, out _);
            Assert.False(helm.Synced);
            helm.Step(Input(0.2f * FullDeg + 6f, 0.2f, wheelVel: 300f), out _, out _);
            Assert.True(helm.Synced);
        }

        [Fact]
        public void CenterButton_StraightensOnce_ThenReturnsToWheel()
        {
            var helm = NewHelm();
            float rudder = 0.4f;
            helm.Step(Input(rudder * FullDeg, rudder), out _, out _);

            bool pressed = true;
            for (int i = 0; i < 500 && (pressed || helm.Centering); i++)
            {
                // Wheel follows the rudder, as force feedback would.
                helm.Step(Input(rudder * FullDeg, rudder, center: pressed), out float x, out _);
                pressed = false;
                rudder = ShipRudder.Apply(rudder, x, RudderSpeed, Dt);
            }
            Assert.False(helm.Centering);
            Assert.True(Math.Abs(rudder) < 0.001f, $"rudder {rudder} should be straight");

            // Afterwards the wheel steers again; nothing pulls back to center.
            for (int i = 0; i < 50; i++)
            {
                helm.Step(Input(45f, rudder), out float x, out _);
                rudder = ShipRudder.Apply(rudder, x, RudderSpeed, Dt);
            }
            Assert.Equal(0.1f, rudder, 3);
        }

        [Fact]
        public void CenterButton_KeepsGoingWhileWheelTrailsRudder()
        {
            // On ships with a fast rudder the wheel trails well over SyncToleranceDegrees on its way back.
            var helm = NewHelm();
            helm.Step(Input(0.4f * FullDeg, 0.4f), out _, out _);
            helm.Step(Input(0.4f * FullDeg, 0.4f, center: true), out _, out _);
            helm.Step(Input(0.3f * FullDeg + 30f, 0.3f, wheelVel: -200f), out float x, out _);
            Assert.True(helm.Centering);
            Assert.True(x < 0f);
        }

        [Fact]
        public void CenterButton_CancelsWhenWheelTurnedAwayFromCenter()
        {
            var helm = NewHelm();
            helm.Step(Input(0.4f * FullDeg, 0.4f), out _, out _);
            helm.Step(Input(0.4f * FullDeg, 0.4f, center: true), out _, out _);
            Assert.True(helm.Centering);

            helm.Step(Input(0.4f * FullDeg + 5f, 0.39f, wheelVel: 100f), out _, out _);
            Assert.False(helm.Centering);
        }

        [Fact]
        public void CenterButton_CancelsWhenWheelHeldFarBack()
        {
            var helm = NewHelm();
            helm.Step(Input(0.4f * FullDeg, 0.4f), out _, out _);
            helm.Step(Input(0.4f * FullDeg, 0.4f, center: true), out _, out _);
            helm.Step(Input(0.4f * FullDeg, 0.2f), out _, out _);
            Assert.False(helm.Centering);
        }

        [Fact]
        public void SpeedButtons_InjectOnePressPerStep_WithReleasedTickBetween()
        {
            var helm = NewHelm();
            var z = new float[5];
            helm.Step(Input(0f, 0f, speedSteps: 2), out _, out z[0]);
            for (int i = 1; i < z.Length; i++) helm.Step(Input(0f, 0f), out _, out z[i]);
            Assert.Equal(new[] { 1f, 0f, 1f, 0f, 0f }, z);

            helm.Step(Input(0f, 0f, speedSteps: -1), out _, out float down);
            Assert.Equal(-1f, down);
        }

        [Fact]
        public void SpeedButtons_DoNotOverrideHeldKey()
        {
            var helm = NewHelm();
            helm.Step(Input(0f, 0f, manualZ: 1f, speedSteps: -1), out _, out float z);
            Assert.Equal(1f, z);
            helm.Step(Input(0f, 0f), out _, out z);
            Assert.Equal(-1f, z);
        }

        [Fact]
        public void InvertSteering_FlipsDirection()
        {
            var helm = NewHelm(invert: true);
            helm.Step(Input(0f, 0f), out _, out _);
            helm.Step(Input(10f, 0f), out float x, out _);
            Assert.True(x < 0f);
        }

        [Fact]
        public void WheelTarget_StopsAtRudderLimit()
        {
            var helm = NewHelm();
            helm.WheelTargetFor(0.5f, 0.75f, RudderSpeed, out float angle, out float velocity);
            Assert.Equal(225f, angle, 3);
            Assert.Equal(0.75f * RudderSpeed * FullDeg, velocity, 3);

            helm.WheelTargetFor(1f, 1f, RudderSpeed, out angle, out velocity);
            Assert.Equal(450f, angle, 3);
            Assert.Equal(0f, velocity);
        }

        [Fact]
        public void ButtonCodes_ParsesListAndSkipsJunk()
        {
            Assert.Equal(new[] { 292, 290 }, ButtonCodes.Parse(" 292, 290,,abc,-1"));
            Assert.Empty(ButtonCodes.Parse(null));
        }
    }
}
