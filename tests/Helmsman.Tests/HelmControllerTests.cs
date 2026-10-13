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

        [Theory]
        [InlineData(1f)]
        [InlineData(-1f)]
        public void CenterButton_StraightensOnce_ThenReturnsToWheel(float side)
        {
            var helm = NewHelm();
            float rudder = 0.4f * side;
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
                helm.Step(Input(45f * side, rudder), out float x, out _);
                rudder = ShipRudder.Apply(rudder, x, RudderSpeed, Dt);
            }
            Assert.Equal(0.1f * side, rudder, 3);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(-1f)]
        public void CenterButton_KeepsGoingWhileWheelTrailsRudder(float side)
        {
            // On ships with a fast rudder the wheel trails well over SyncToleranceDegrees on its way back.
            var helm = NewHelm();
            helm.Step(Input(0.4f * FullDeg * side, 0.4f * side), out _, out _);
            helm.Step(Input(0.4f * FullDeg * side, 0.4f * side, center: true), out _, out _);
            helm.Step(Input((0.3f * FullDeg + 30f) * side, 0.3f * side, wheelVel: -200f * side), out float x, out _);
            Assert.True(helm.Centering);
            Assert.True(x * side < 0f);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(-1f)]
        public void CenterButton_KeepsGoingWhileWheelStartsHome(float side)
        {
            // The wheel starts slowly at full lock while the rudder is already moving.
            var helm = NewHelm();
            helm.Step(Input(FullDeg * side, side), out _, out _);
            helm.Step(Input(FullDeg * side, side, center: true), out _, out _);
            helm.Step(Input((FullDeg - 2f) * side, 0.97f * side, wheelVel: -50f * side), out _, out _);
            Assert.True(helm.Centering);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(-1f)]
        public void CenterButton_CancelsWhenWheelTurnedAwayFromCenter(float side)
        {
            var helm = NewHelm();
            helm.Step(Input(0.4f * FullDeg * side, 0.4f * side), out _, out _);
            helm.Step(Input(0.4f * FullDeg * side, 0.4f * side, center: true), out _, out _);
            Assert.True(helm.Centering);

            helm.Step(Input((0.4f * FullDeg + 5f) * side, 0.39f * side, wheelVel: 100f * side), out _, out _);
            Assert.False(helm.Centering);
        }

        [Theory]
        [InlineData(1f, 0f)]
        [InlineData(-1f, 0f)]
        [InlineData(1f, -40f)]
        [InlineData(-1f, -40f)]
        public void CenterButton_CancelsWhenGripped(float side, float creepSpeed)
        {
            // A firm grip stops the wheel, or lets it creep far slower than the rudder.
            var helm = NewHelm();
            float rudder = 0.6f * side, wheel = rudder * FullDeg;
            helm.Step(Input(wheel, rudder), out _, out _);
            helm.Step(Input(wheel, rudder, center: true), out float x, out _);
            for (int i = 0; i < 15 && helm.Centering; i++)
            {
                rudder = ShipRudder.Apply(rudder, x, RudderSpeed, Dt);
                wheel += creepSpeed * side * Dt;
                helm.Step(Input(wheel, rudder, wheelVel: creepSpeed * side), out x, out _);
            }
            Assert.False(helm.Centering);
            Assert.True(Math.Abs(wheel - rudder * FullDeg) < 25f, $"cancelled only {Math.Abs(wheel - rudder * FullDeg):0}° behind");
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(-1f)]
        public void CenterButton_BriefSlowWheelDoesNotCancel(float side)
        {
            // Starting from rest, the wheel trails the rudder while it speeds up.
            var helm = NewHelm();
            float rudder = 0.6f * side, wheel = rudder * FullDeg, vel = 0f;
            helm.Step(Input(wheel, rudder), out _, out _);
            helm.Step(Input(wheel, rudder, center: true), out float x, out _);
            for (int i = 0; i < 10; i++)
            {
                rudder = ShipRudder.Apply(rudder, x, RudderSpeed, Dt);
                vel -= 3000f * side * Dt;
                wheel += vel * Dt;
                helm.Step(Input(wheel, rudder, wheelVel: vel), out x, out _);
            }
            Assert.True(helm.Centering);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(-1f)]
        public void CenterButton_GripKicksTowardTheHand(float side)
        {
            var helm = NewHelm();
            float rudder = 0.6f * side, wheel = rudder * FullDeg;
            helm.Step(Input(wheel, rudder), out _, out _);
            helm.Step(Input(wheel, rudder, center: true), out float x, out _);
            for (int i = 0; i < 15 && helm.Centering; i++)
            {
                rudder = ShipRudder.Apply(rudder, x, RudderSpeed, Dt);
                helm.Step(Input(wheel, rudder), out x, out _);
            }
            Assert.False(helm.Centering);
            Assert.Equal((int)side, helm.TakeReleaseKick());
            Assert.Equal(0, helm.TakeReleaseKick());
        }

        [Theory]
        [InlineData(1f, 0f)]
        [InlineData(-1f, 0f)]
        [InlineData(1f, 60f)]
        [InlineData(-1f, 60f)]
        public void CenterButton_GripNearHomeTakesOver(float side, float awaySpeed)
        {
            // The wheel trails the rudder home 22° behind, so centering arrives first; then the hand holds it there or turns it away.
            var helm = NewHelm();
            float rudder = 0.3f * side, wheel = rudder * FullDeg + 22f * side;
            helm.Step(Input(wheel, rudder, center: true), out float x, out _);
            while (helm.Centering)
            {
                rudder = ShipRudder.Apply(rudder, x, RudderSpeed, Dt);
                float next = rudder * FullDeg + 22f * side;
                float vel = (next - wheel) / Dt;
                wheel = next;
                helm.Step(Input(wheel, rudder, wheelVel: vel), out x, out _);
            }
            Assert.Equal(0f, rudder, 3);
            Assert.False(helm.Synced);
            int ticks = 0;
            for (; ticks < 25 && !helm.Synced; ticks++)
            {
                wheel += awaySpeed * side * Dt;
                helm.Step(Input(wheel, rudder, wheelVel: awaySpeed * side), out x, out _);
                rudder = ShipRudder.Apply(rudder, x, RudderSpeed, Dt);
            }
            Assert.True(helm.Synced, "the held wheel never took over");
            Assert.True(ticks <= 4, $"took {ticks} ticks");
            Assert.Equal((int)side, helm.TakeReleaseKick());
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(-1f)]
        public void CenterButton_WheelAtTopSpeedDoesNotCancel(float side)
        {
            // A fast rudder outruns the real G29, which tops out around 450°/s hands-off.
            const float fastRudder = 3f, topSpeed = 440f;
            var helm = NewHelm();
            float rudder = side, wheel = rudder * FullDeg;
            HelmInput Fast(float w, float r, bool center = false, float vel = 0f) { var input = Input(w, r, center: center, wheelVel: vel); input.RudderSpeed = fastRudder; return input; }
            helm.Step(Fast(wheel, rudder), out _, out _);
            helm.Step(Fast(wheel, rudder, center: true), out float x, out _);
            for (int i = 0; i < 25 && Math.Abs(rudder) > 0.001f; i++)
            {
                rudder = ShipRudder.Apply(rudder, x, fastRudder, Dt);
                wheel -= topSpeed * side * Dt;
                helm.Step(Fast(wheel, rudder, vel: -topSpeed * side), out x, out _);
                Assert.True(helm.Centering || Math.Abs(rudder) < 0.001f, $"cancelled with rudder at {rudder * FullDeg:0}°");
            }
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
