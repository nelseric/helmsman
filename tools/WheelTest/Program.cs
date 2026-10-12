using System;
using System.Globalization;
using System.Threading;
using Helmsman.Wheel;

// Standalone wheel test: `info`, `force <-1..1> [seconds]`, `hold <degrees> [seconds]`, `track [speed] [amplitude] [seconds] [fps]`, `sim [rudderSpeed] [degreesAtFullRudder] [log.csv]`, or `kbd [pressSeconds]`.
var settings = new WheelController.Settings { LogPath = Environment.GetEnvironmentVariable("HELMSMAN_LOG") ?? "" };
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
        for (int code = 0x100; code < 0x300; code++)
            if (controller.TakePresses(code) > 0) Console.WriteLine($"\nButton pressed: code {code}");
        Console.Write($"\rangle {controller.AngleDegrees,8:0.0}°  velocity {controller.VelocityDegreesPerSecond,8:0}°/s   ");
        Thread.Sleep(20);
    }
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

if (mode == "sim" || mode == "kbd")
{
    // Replicates Ship.ApplyControlls at 50 Hz with the plugin's steering logic. `kbd` holds "D" for a while, then checks the rudder stops.
    bool kbd = mode == "kbd";
    float rudderSpeed = kbd ? 0.5f : Arg(1, 0.5f), fullDeg = kbd ? 450f : Arg(2, 450f), pressSeconds = kbd ? Arg(1, 1f) : 0f;
    using var csv = !kbd && args.Length > 3 ? new System.IO.StreamWriter(args[3]) : null;
    csv?.WriteLine("ms,wheel,rudderDeg,rudderRate,force");
    var simStart = DateTime.UtcNow;
    const float dt = 0.02f;
    float rudder = Math.Clamp(controller.AngleDegrees / fullDeg, -1f, 1f), rudderAtRelease = float.NaN;
    bool synced = false, keyboardResync = false;
    int tick = 0;
    while (!stop)
    {
        double t = (DateTime.UtcNow - simStart).TotalSeconds;
        if (kbd && t > 0.5 + pressSeconds + 3) break;
        float keyboard = kbd && t > 0.5 && t < 0.5 + pressSeconds ? 1f : 0f;
        if (kbd && keyboard == 0f && t >= 0.5 + pressSeconds + 1 && float.IsNaN(rudderAtRelease)) rudderAtRelease = rudder;

        float target = Math.Clamp(controller.AngleDegrees / fullDeg, -1f, 1f);
        float x = keyboard;
        if (keyboard != 0f)
        {
            synced = false;
            keyboardResync = true;
        }
        else
        {
            if (!synced && Math.Abs(target - rudder) * fullDeg <= (keyboardResync ? 2f : 15f)) { synced = true; keyboardResync = false; }
            if (synced)
            {
                float step = (0.5f + 0.5f * Math.Abs(rudder)) * rudderSpeed * dt;
                float delta = target - rudder;
                x = Math.Abs(delta) < 0.001f ? 0f : Math.Clamp(delta / step, -1f, 1f);
            }
        }
        float rate = x * (0.5f + 0.5f * Math.Abs(rudder)) * rudderSpeed;
        rudder = Math.Clamp(rudder + rate * dt, -1f, 1f);
        if (Math.Abs(rudder) >= 1f && Math.Sign(rate) == Math.Sign(rudder)) rate = 0f;
        controller.Follow(rudder * fullDeg, rate * fullDeg);
        csv?.WriteLine(FormattableString.Invariant($"{(DateTime.UtcNow - simStart).TotalMilliseconds:0},{controller.AngleDegrees:0.00},{rudder * fullDeg:0.00},{rate * fullDeg:0.0},{controller.LastForce:0.000}"));
        if (kbd && tick % 12 == 0)
            Console.WriteLine($"{t,5:0.00}s {(keyboard != 0 ? "D held " : "       ")} wheel {controller.AngleDegrees,7:0.0}°  rudder {rudder * fullDeg,7:0.0}°  force {controller.LastForce,6:+0.00;-0.00}");
        else if (!kbd && tick % 5 == 0)
            Console.Write($"\rwheel {controller.AngleDegrees,7:0.0}°  rudder {rudder,6:+0.00;-0.00} ({rudder * fullDeg,7:0.0}°)  force {controller.LastForce,6:+0.00;-0.00}  {(synced ? "" : "syncing")}   ");
        tick++;
        Thread.Sleep(20);
    }
    if (kbd)
    {
        float drift = (rudder - rudderAtRelease) * fullDeg;
        Console.WriteLine($"Rudder moved {drift:+0.0;-0.0}° in the last 2s after release: {(Math.Abs(drift) < 2f ? "stable" : "RUNAWAY")}");
    }
    else Console.WriteLine();
    return 0;
}

Console.WriteLine("Usage: WheelTest [info | force <-1..1> [seconds] | hold <degrees> [seconds] | track [speed] [amplitude] [seconds] [fps] | sim [rudderSpeed] [degreesAtFullRudder] [log.csv] | kbd [pressSeconds]]");
return 1;
