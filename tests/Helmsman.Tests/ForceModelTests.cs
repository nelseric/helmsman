using System;
using Helmsman.Wheel;
using Xunit;

namespace Helmsman.Tests
{
    public class ForceModelTests
    {
        private const float Dt = 0.002f;
        private const float Cap = 0.7f;

        private static ForceModel NewModel(WheelSettings settings = null) => new ForceModel(settings ?? new WheelSettings());

        // Steps a model whose rudder sits still at `target`.
        private static float StaticForce(ForceModel model, float angle, float vel, float target)
        {
            model.Reset(target, 0f);
            return model.Step(Dt, angle, vel, target, 0f, true, Cap);
        }

        [Fact]
        public void OnTarget_NoForce() => Assert.Equal(0f, StaticForce(NewModel(), 30f, 0f, 30f));

        [Fact]
        public void WithinDeadband_NoForce() => Assert.Equal(0f, StaticForce(NewModel(), 30.4f, 0f, 30f));

        [Fact]
        public void PullsTowardTarget()
        {
            Assert.True(StaticForce(NewModel(), 0f, 0f, 10f) > 0f);
            Assert.True(StaticForce(NewModel(), 0f, 0f, -10f) < 0f);
        }

        [Fact]
        public void ForceIsCapped()
        {
            Assert.Equal(Cap, StaticForce(NewModel(), 0f, 0f, 1000f));
            Assert.Equal(-0.2f, NewModel().Step(Dt, 0f, 0f, -1000f, 0f, true, 0.2f));
        }

        [Fact]
        public void KeepingUpWithMovingRudder_FeelsNoResistance()
        {
            var model = NewModel();
            model.Reset(100f, 200f);
            // A few degrees ahead of a rudder moving at 200°/s, at the same speed: inside the free zone.
            Assert.Equal(0f, model.Step(Dt, 105f, 200f, 100f, 200f, false, Cap), 3);
        }

        [Fact]
        public void NeverPushesWheelAheadOfRudder()
        {
            var model = NewModel();
            model.Reset(100f, 200f);
            // Wheel in the free zone but slower than the rudder: pushing it along is what made wheel and rudder chase each other.
            Assert.Equal(0f, model.Step(Dt, 104f, 100f, 100f, 200f, false, Cap));
        }

        [Fact]
        public void TurningFasterThanRudder_IsResisted()
        {
            var model = NewModel();
            model.Reset(100f, 200f);
            Assert.True(model.Step(Dt, 105f, 400f, 100f, 200f, false, Cap) < 0f);
        }

        [Fact]
        public void MinForce_AppliesToStationaryWheel_AndFadesWhenMoving()
        {
            var noMin = new WheelSettings { MinForce = 0f, Damping = 0f };
            var withMin = new WheelSettings { MinForce = 0.12f, Damping = 0f };
            float Extra(float vel) => StaticForce(NewModel(withMin), 0f, vel, 5f) - StaticForce(NewModel(noMin), 0f, vel, 5f);
            Assert.Equal(0.12f, Extra(0f), 3);
            Assert.Equal(0f, Extra(60f), 3);
        }

        [Fact]
        public void FrameTimedUpdates_TargetStaysSmooth()
        {
            // 50 Hz physics ticks delivered at the start of 60 fps frames, rudder moving at 200°/s.
            const double speed = 200, fixedDt = 0.02, frameDt = 1.0 / 60;
            var model = NewModel();
            model.Reset(0f, (float)speed);
            double fixedTime = 0, nextFrame = 0, published = 0;
            float maxStep = 0f, maxError = 0f, last = 0f;
            for (double t = 0; t < 2; t += Dt)
            {
                bool newSample = false;
                if (t >= nextFrame)
                {
                    while (fixedTime + fixedDt <= t) { fixedTime += fixedDt; published = speed * fixedTime; newSample = true; }
                    nextFrame += frameDt;
                }
                model.Step(Dt, 0f, 0f, (float)published, (float)speed, newSample, Cap);
                if (t > 0.5)
                {
                    maxStep = Math.Max(maxStep, Math.Abs(model.Target - last));
                    maxError = Math.Max(maxError, (float)Math.Abs(model.Target - speed * t));
                }
                last = model.Target;
            }
            // A raw 50 Hz target would jump 4° per tick; the tracked one moves smoothly and stays close.
            Assert.True(maxStep < 1.2f, $"target jumped {maxStep:0.00}° in one step");
            Assert.True(maxError < 6f, $"target lagged the rudder by {maxError:0.0}°");
        }
    }
}
