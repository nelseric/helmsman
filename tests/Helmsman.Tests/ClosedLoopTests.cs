using System;
using Helmsman.Core;
using Helmsman.Wheel;
using Xunit;

namespace Helmsman.Tests
{
    // Simulates the whole loop with a crude geared wheel (inertia, viscous drag, static friction) and Valheim's rudder.
    public class ClosedLoopTests
    {
        private const float WheelDt = 0.002f;
        private const float GameDt = 0.02f;
        private const float RudderSpeed = 0.5f;
        private const float FullDeg = 450f;

        private sealed class Sim
        {
            public readonly HelmController Helm = new HelmController(new HelmSettings { DegreesAtFullRudder = FullDeg });
            public readonly ForceModel Force = new ForceModel(new WheelSettings());
            public float Rudder, WheelAngle, WheelVelocity;
            private float _target, _targetVelocity, _gameClock;
            private bool _newSample;

            public Sim(float rudder)
            {
                Rudder = rudder;
                WheelAngle = rudder * FullDeg;
                Helm.WheelTargetFor(Rudder, 0f, RudderSpeed, out _target, out _targetVelocity);
                Force.Reset(_target, _targetVelocity);
            }

            // hand: extra torque from the player's hands, in the same units as force feedback.
            public void Run(float seconds, float keyboard = 0f, float hand = 0f)
            {
                for (float t = 0; t < seconds; t += WheelDt)
                {
                    _gameClock += WheelDt;
                    if (_gameClock >= GameDt)
                    {
                        _gameClock -= GameDt;
                        Helm.Step(new HelmInput { WheelDegrees = WheelAngle, Rudder = Rudder, RudderSpeed = RudderSpeed, DeltaTime = GameDt, ManualX = keyboard }, out float x, out _);
                        float input = ShipRudder.Input(Rudder, x);
                        Rudder = ShipRudder.Apply(Rudder, x, RudderSpeed, GameDt);
                        Helm.WheelTargetFor(Rudder, input, RudderSpeed, out _target, out _targetVelocity);
                        _newSample = true;
                    }
                    float force = Force.Step(WheelDt, WheelAngle, WheelVelocity, _target, _targetVelocity, _newSample, 0.7f);
                    _newSample = false;
                    StepWheel(force + hand);
                }
            }

            // Lively enough that the old chase-the-lead force law runs away here, as it did on a real G29.
            private void StepWheel(float force)
            {
                const float degPerSecSqPerForce = 5000f, drag = 2f, staticFriction = 0.15f, kineticFriction = 0.02f;
                if (Math.Abs(WheelVelocity) < 1f && Math.Abs(force) < staticFriction)
                {
                    WheelVelocity = 0f;
                    return;
                }
                float friction = Math.Sign(WheelVelocity != 0f ? WheelVelocity : force) * kineticFriction;
                WheelVelocity += ((force - friction) * degPerSecSqPerForce - drag * WheelVelocity) * WheelDt;
                WheelAngle += WheelVelocity * WheelDt;
            }
        }

        [Theory]
        [InlineData(0.3f)]
        [InlineData(1f)]
        [InlineData(2.5f)]
        public void AfterKeyboardRelease_RudderStops(float pressSeconds)
        {
            var sim = new Sim(0f);
            sim.Run(pressSeconds, keyboard: 1f);
            sim.Run(1f);
            float settled = sim.Rudder;
            sim.Run(2f);
            Assert.True(Math.Abs(sim.Rudder - settled) * FullDeg < 1f, $"rudder drifted {(sim.Rudder - settled) * FullDeg:0.0}° after release");
            Assert.True(Math.Abs(sim.WheelAngle - sim.Rudder * FullDeg) < 3f, "wheel should rest at the rudder");
        }

        [Theory]
        [InlineData(0.3f, 0.4f)]
        [InlineData(1f, 0.4f)]
        [InlineData(0.5f, 0.9f)]
        public void AfterLettingGoOfTurningWheel_RudderStops(float handSeconds, float handForce)
        {
            var sim = new Sim(0f);
            sim.Run(handSeconds, hand: handForce);
            sim.Run(1f);
            float settled = sim.Rudder;
            sim.Run(2f);
            Assert.True(Math.Abs(sim.Rudder - settled) * FullDeg < 1f, $"rudder drifted {(sim.Rudder - settled) * FullDeg:0.0}° after letting go");
            Assert.True(Math.Abs(sim.Rudder) < 1f, "rudder ran to the limit");
        }

        [Fact]
        public void HandsOff_EverythingStaysPut()
        {
            var sim = new Sim(0.3f);
            sim.Run(3f);
            Assert.Equal(0.3f, sim.Rudder, 3);
            Assert.Equal(0.3f * FullDeg, sim.WheelAngle, 0);
        }
    }
}
