using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Helmsman.Core;
using Helmsman.Wheel;

// Standalone wheel test: `info`, `force <-1..1> [seconds]`, `hold <degrees> [seconds]`, `track [speed] [amplitude] [seconds] [fps]`, `sim [rudderSpeed] [degreesAtFullRudder] [log.csv]`, `kbd [pressSeconds]`, `center [rudderSpeed] [startDegrees]`, or `coast [force] [seconds]`.
var settings = new WheelSettings { LogPath = Environment.GetEnvironmentVariable("HELMSMAN_LOG") ?? "", Backend = Environment.GetEnvironmentVariable("HELMSMAN_BACKEND") ?? WheelDevices.Auto };
string mode = args.Length > 0 ? args[0] : "info";
float Arg(int i, float fallback) => args.Length > i ? float.Parse(args[i], CultureInfo.InvariantCulture) : fallback;
var stop = false;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };

if (mode == "force")
{
    float level = Arg(1, 0.3f), seconds = Arg(2, 0.5f);
    using var wheel = new WheelController(settings, Console.WriteLine);
    for (int i = 0; i < 40 && !wheel.Connected; i++) Thread.Sleep(50);
    if (!wheel.Connected) { Console.WriteLine("No force-feedback wheel found."); return 1; }
    float start = wheel.AngleDegrees;
    // A far-away target with saturating stiffness gives a constant push of `level`.
    settings.Stiffness = 1f;
    settings.Damping = 0f;
    settings.DeadbandDegrees = 0f;
    settings.MaxForce = Math.Abs(level);
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < end && !stop)
    {
        wheel.Follow(wheel.AngleDegrees + Math.Sign(level) * 1000f, 0f);
        Thread.Sleep(5);
    }
    wheel.StopFollowing();
    float moved = wheel.AngleDegrees - start;
    Console.WriteLine($"Pushed with {level:+0.00;-0.00} from {start:0.0}°: wheel moved {moved:+0.0;-0.0}° ({(moved * level > 0 ? "force direction OK" : "set InvertForce = true")})");
    return 0;
}

using var controller = new WheelController(settings, Console.WriteLine);
Console.WriteLine("Waiting for wheel... (Ctrl+C to quit)");
while (!controller.Connected && !stop) Thread.Sleep(100);

if (mode == "info")
{
    while (!stop)
    {
        for (int button = 0; button < 256; button++)
            if (controller.TakePresses(button) > 0) Console.WriteLine($"\nButton pressed: {button}");
        Console.Write($"\rangle {controller.AngleDegrees,8:0.0}°  velocity {controller.VelocityDegreesPerSecond,8:0}°/s   ");
        Thread.Sleep(20);
    }
    return 0;
}

if (mode == "coast")
{
    // Push with a fixed force, then release with no force or centering spring, to measure the wheel's inertia and friction.
    float level = Arg(1, 0.4f), pushSeconds = Arg(2, 0.15f);
    settings.IdleAutocenter = 0f;
    settings.Stiffness = 1f;
    settings.Damping = 0f;
    settings.MinForce = 0f;
    settings.DeadbandDegrees = 0f;
    controller.StopFollowing();
    Thread.Sleep(300);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < pushSeconds) { controller.Follow(controller.AngleDegrees + Math.Sign(level) * 1000f, 0f, Math.Abs(level)); Thread.Sleep(1); }
    controller.StopFollowing();
    float releaseAngle = controller.AngleDegrees, releaseVelocity = controller.VelocityDegreesPerSecond;
    Thread.Sleep(1500);
    Console.WriteLine($"push {level:+0.00;-0.00} for {pushSeconds:0.000}s: released at {releaseVelocity:0}°/s, coasted {controller.AngleDegrees - releaseAngle:+0.0;-0.0}°");
    return 0;
}

if (mode == "hold")
{
    // Step response: follow a fixed angle and log position every 50 ms.
    float target = Arg(1, 90f), seconds = Arg(2, 2f);
    var start = DateTime.UtcNow;
    while (!stop && (DateTime.UtcNow - start).TotalSeconds < seconds)
    {
        controller.Follow(target, 0f);
        Console.WriteLine($"{(DateTime.UtcNow - start).TotalMilliseconds,6:0} ms  angle {controller.AngleDegrees,7:0.0}°  force {controller.LastForce,6:+0.00;-0.00}");
        Thread.Sleep(50);
    }
    controller.StopFollowing();
    return 0;
}

if (mode == "track")
{
    // Hands-off: follow a rudder sweeping at constant speed, published from 50 Hz physics ticks run inside frames at `fps`
    // (0 = evenly spaced ticks), like Unity. Reports tracking error and high-frequency force jitter.
    float speed = Arg(1, 200f), amplitude = Arg(2, 90f), seconds = Arg(3, 6f), fps = Arg(4, 60f);
    const double fixedDt = 0.02;
    double Rudder(double t) { double period = 4 * amplitude / speed, ph = (t % period) / period; return amplitude * (ph < 0.25 ? 4 * ph : ph < 0.75 ? 2 - 4 * ph : 4 * ph - 4); }
    double RudderRate(double t) { double period = 4 * amplitude / speed, ph = (t % period) / period; return ph < 0.25 || ph >= 0.75 ? speed : -speed; }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    double fixedTime = 0, nextFrame = 0, sqErr = 0, sqJitter = 0, avgForce = 0;
    int samples = 0;
    while (!stop && sw.Elapsed.TotalSeconds < seconds)
    {
        double now = sw.Elapsed.TotalSeconds;
        if (now >= nextFrame)
        {
            // Unity runs every fixed tick that is due at the start of each frame.
            while (fixedTime + fixedDt <= now)
            {
                fixedTime += fixedDt;
                controller.Follow((float)Rudder(fixedTime), (float)RudderRate(fixedTime));
            }
            nextFrame = fps > 0 ? nextFrame + 1.0 / fps : now;
        }
        Thread.Sleep(1);
        if (now < 1) { avgForce = controller.LastForce; continue; }
        float f = controller.LastForce;
        avgForce += (f - avgForce) * 0.05;
        sqJitter += (f - avgForce) * (f - avgForce);
        sqErr += Math.Pow(controller.AngleDegrees - Rudder(now), 2);
        samples++;
    }
    controller.StopFollowing();
    Console.WriteLine($"rms error {Math.Sqrt(sqErr / Math.Max(1, samples)):0.0}°, force jitter {Math.Sqrt(sqJitter / Math.Max(1, samples)):0.000}");
    return 0;
}

if (mode == "sim" || mode == "kbd" || mode == "center")
{
    // Replicates Ship.ApplyControlls at 50 Hz with the plugin's steering logic. `kbd` holds "D" for a while, then checks the rudder stops.
    bool kbd = mode == "kbd" || mode == "center", centerTest = mode == "center";
    float rudderSpeed = centerTest ? Arg(1, 0.5f) : kbd ? 0.5f : Arg(1, 0.5f), fullDeg = kbd ? 450f : Arg(2, 450f), pressSeconds = centerTest ? 0f : kbd ? Arg(1, 1f) : 0f;
    using var csv = !kbd && args.Length > 3 ? new System.IO.StreamWriter(args[3]) : null;
    csv?.WriteLine("ms,wheel,rudderDeg,rudderRate,force");
    var simStart = DateTime.UtcNow;
    const float dt = 0.02f;
    // HELMSMAN_RETURN=<force> turns on the tiller's return-to-center pull, as on a ship at full speed.
    float returnForce = float.TryParse(Environment.GetEnvironmentVariable("HELMSMAN_RETURN"), NumberStyles.Float, CultureInfo.InvariantCulture, out float rf) ? rf : 0f;
    var helm = new HelmController(new HelmSettings { DegreesAtFullRudder = fullDeg, ReturnToCenter = returnForce > 0f, ReturnToCenterForce = returnForce }) { Log = msg => Console.WriteLine("\n  " + msg) };
    int[] centerButtons = { 6, 7, 10, 11 };
    if (centerTest && args.Length > 2)
    {
        // Move to the starting angle first.
        float startAngle = Arg(2, 0f);
        for (int i = 0; i < 100 && !stop; i++) { controller.Follow(startAngle, 0f); Thread.Sleep(20); }
    }
    float rudder = Math.Clamp(controller.AngleDegrees / fullDeg, -1f, 1f), rudderAtRelease = float.NaN;
    int tick = 0;
    while (!stop)
    {
        double t = (DateTime.UtcNow - simStart).TotalSeconds;
        if (kbd && t > 0.5 + pressSeconds + (centerTest ? 6 : 3)) break;
        float keyboard = kbd && t > 0.5 && t < 0.5 + pressSeconds ? 1f : 0f;
        if (kbd && keyboard == 0f && t >= 0.5 + pressSeconds + 1 && float.IsNaN(rudderAtRelease)) rudderAtRelease = rudder;

        var input = new HelmInput { WheelDegrees = controller.AngleDegrees, WheelVelocity = controller.VelocityDegreesPerSecond, Rudder = rudder, RudderSpeed = rudderSpeed, DeltaTime = dt, ManualX = keyboard, CenterPressed = centerTest ? tick == 25 : centerButtons.Sum(controller.TakePresses) > 0 };
        helm.Step(input, out float x, out _);
        float rudderInput = ShipRudder.Input(rudder, x);
        rudder = ShipRudder.Apply(rudder, x, rudderSpeed, dt);
        helm.WheelTargetFor(rudder, rudderInput, rudderSpeed, out float wheelTarget, out float wheelTargetVelocity);
        helm.ReturnToCenterFor(8f, out float returnPerDegree, out float returnFriction);
        controller.SetReturnToCenter(returnPerDegree, returnFriction);
        controller.Follow(wheelTarget, wheelTargetVelocity);
        controller.Kick(helm.TakeReleaseKick());
        csv?.WriteLine(FormattableString.Invariant($"{(DateTime.UtcNow - simStart).TotalMilliseconds:0},{controller.AngleDegrees:0.00},{wheelTarget:0.00},{wheelTargetVelocity:0.0},{controller.LastForce:0.000}"));
        if (kbd && tick % 12 == 0)
            Console.WriteLine($"{t,5:0.00}s {(keyboard != 0 ? "D held " : "       ")} wheel {controller.AngleDegrees,7:0.0}°  rudder {rudder * fullDeg,7:0.0}°  force {controller.LastForce,6:+0.00;-0.00}");
        else if (!kbd && tick % 5 == 0)
            Console.Write($"\rwheel {controller.AngleDegrees,7:0.0}°  rudder {rudder,6:+0.00;-0.00} ({rudder * fullDeg,7:0.0}°)  force {controller.LastForce,6:+0.00;-0.00}  {(helm.Synced ? "" : "syncing")}   ");
        tick++;
        Thread.Sleep(20);
    }
    if (kbd)
    {
        float drift = (rudder - rudderAtRelease) * fullDeg;
        if (centerTest) Console.WriteLine($"Centered: rudder {rudder * fullDeg:+0.0;-0.0}°, wheel {controller.AngleDegrees:+0.0;-0.0}°, drift in the last 2s {drift:+0.0;-0.0}°");
        else Console.WriteLine($"Rudder moved {drift:+0.0;-0.0}° in the last 2s after release: {(Math.Abs(drift) < 2f ? "stable" : "RUNAWAY")}");
    }
    else Console.WriteLine();
    return 0;
}

Console.WriteLine("Usage: WheelTest [info | force <-1..1> [seconds] | hold <degrees> [seconds] | track [speed] [amplitude] [seconds] [fps] | sim [rudderSpeed] [degreesAtFullRudder] [log.csv] | kbd [pressSeconds] | center [rudderSpeed] [startDegrees] | coast [force] [seconds]]");
return 1;
