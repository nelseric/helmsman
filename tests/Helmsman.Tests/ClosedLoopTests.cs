using System;
using System.Collections.Generic;
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
        private const float FullDeg = 450f;
        // USB and driver latency between computing a force and the wheel feeling it (4 × 2 ms).
        private const int OutputDelayTicks = 4;

        private sealed class Sim
        {
            public readonly HelmController Helm = new HelmController(new HelmSettings { DegreesAtFullRudder = FullDeg });
            public readonly ForceModel Force = new ForceModel(new WheelSettings());
            public float Rudder, WheelAngle, WheelVelocity;
            private readonly float _rudderSpeed;
            private float _target, _targetVelocity, _gameClock;
            private bool _newSample;
            private readonly Queue<float> _outputDelay = new Queue<float>(new float[OutputDelayTicks]);

            public Sim(float rudder, float rudderSpeed = 0.5f)
            {
                _rudderSpeed = rudderSpeed;
                Rudder = rudder;
                WheelAngle = rudder * FullDeg;
                Helm.WheelTargetFor(Rudder, 0f, _rudderSpeed, out _target, out _targetVelocity);
                Force.Reset(_target, _targetVelocity);
            }

            // hand: extra torque from the player's hands, in the same units as force feedback.
            public void Run(float seconds, float keyboard = 0f, float hand = 0f, bool center = false)
            {
                for (float t = 0; t < seconds; t += WheelDt)
                {
                    _gameClock += WheelDt;
                    if (_gameClock >= GameDt)
                    {
                        _gameClock -= GameDt;
                        Helm.Step(new HelmInput { WheelDegrees = WheelAngle, WheelVelocity = WheelVelocity, Rudder = Rudder, RudderSpeed = _rudderSpeed, DeltaTime = GameDt, ManualX = keyboard, CenterPressed = center }, out float x, out _);
                        center = false;
                        float input = ShipRudder.Input(Rudder, x);
                        Rudder = ShipRudder.Apply(Rudder, x, _rudderSpeed, GameDt);
                        Helm.WheelTargetFor(Rudder, input, _rudderSpeed, out _target, out _targetVelocity);
                        _newSample = true;
                    }
                    float force = Force.Step(WheelDt, WheelAngle, WheelVelocity, _target, _targetVelocity, _newSample, 0.7f);
                    _newSample = false;
                    _outputDelay.Enqueue(force);
                    StepWheel(_outputDelay.Dequeue() + hand);
                }
            }

            // Fitted to push-and-coast measurements of a G29 (WheelTest coast).
            private void StepWheel(float force)
            {
                const float degPerSecSqPerForce = 12000f, drag = 1f, staticFriction = 0.17f, kineticFriction = 0.08f;
                if (Math.Abs(WheelVelocity) < 1f && Math.Abs(force) < staticFriction)
                {
                    WheelVelocity = 0f;
                    return;
                }
                float friction = Math.Sign(WheelVelocity != 0f ? WheelVelocity : force) * kineticFriction;
                float velocity = WheelVelocity + ((force - friction) * degPerSecSqPerForce - drag * WheelVelocity) * WheelDt;
                // Friction stops the wheel; it can't push it back the other way.
                if (WheelVelocity != 0f && Math.Sign(velocity) != Math.Sign(WheelVelocity) && Math.Abs(force) < staticFriction) velocity = 0f;
                WheelVelocity = velocity;
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

        [Theory]
        [InlineData(0.5f)]
        [InlineData(1f)]
        [InlineData(2f)]
        public void CenterButton_BringsRudderAndWheelHome(float rudderSpeed)
        {
            var sim = new Sim(0.6f, rudderSpeed);
            sim.Run(0.1f, center: true);
            sim.Run(4f);
            // Within 6° of wheel is ~0.4° of actual rudder (Valheim's rudder turns ±30°); friction stops the wheel a little short.
            Assert.True(Math.Abs(sim.Rudder) * FullDeg < 6f, $"rudder stopped at {sim.Rudder * FullDeg:0.0}°");
            Assert.True(Math.Abs(sim.WheelAngle) < 6f, $"wheel stopped at {sim.WheelAngle:0.0}°");
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
