# Helmsman

Client-side Valheim mod (BepInEx) that steers ships with a force-feedback racing wheel (Logitech G29) on native Linux.

- Turning the wheel sets a target rudder angle. The mod feeds ordinary steering input to `Ship.ApplyControlls`, so the rudder moves at the game's own rate and syncs to other players as normal. No server-side install needed.
- Force feedback pulls the wheel toward the actual rudder position. Turning faster than the rudder can follow is resisted, and keyboard steering moves the wheel.
- The paddle shifters and the circle/cross buttons change sail speed.
- R2, L2, R3 or L3 swings the rudder (and the wheel with it) back to straight.

## Requirements

- Native Linux Valheim with [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).
- G29 in **PS3 mode** (mode switch on the wheel), driven by the kernel's `hid-logitech` (lg4ff) or `new-lg4ff`. In PS4 mode (`046d:c260`) the kernel has no force-feedback driver for it.
- Read/write access to the wheel's `/dev/input/event*` node (e.g. membership in the `input` group).

## Build

```sh
dotnet build src/Helmsman -c Release
```

Builds against `~/.local/share/Steam/steamapps/common/Valheim` (override with `-p:ValheimDir=...`). If BepInEx is installed there, the DLL is copied to `BepInEx/plugins/Helmsman/`.

## Testing without the game

```sh
dotnet run --project tools/WheelTest -- info           # live angle, prints button codes when pressed
dotnet run --project tools/WheelTest -- force 0.3      # short push to verify force direction
dotnet run --project tools/WheelTest -- hold 90         # hands-off: hold the wheel at an angle
dotnet run --project tools/WheelTest -- track 200 90    # hands-off: follow a rudder sweeping at 200°/s, reports force jitter
dotnet run --project tools/WheelTest -- kbd 1           # hands-off: hold "D" for 1s, check the rudder stops on release
dotnet run --project tools/WheelTest -- sim 0.5 450    # simulated rudder (rudderSpeed, degrees at full rudder [, log.csv])
```

Set `HELMSMAN_LOG=<file.csv>` to log the force loop (~500 lines/s); the in-game equivalent is the `Debug.LogPath` setting.

## Configuration

`BepInEx/config/helmsman.wheel.cfg` is created on first launch. Main settings:

| Setting | Default | Meaning |
| --- | --- | --- |
| `DegreesAtFullRudder` | 450 | Wheel angle that equals full rudder |
| `MaxForce` | 0.7 | Force cap (fraction of peak torque) |
| `Stiffness` / `Damping` | 0.03 / 0.0015 | Spring/damper pulling the wheel to the rudder |
| `MinForce` | 0.12 | Extra push to overcome the wheel's gearbox friction |
| `InvertForce` / `InvertSteering` | false | Direction fixes |
| `IdleCenterForce` | 0.5 | Force snapping the wheel to center when not at the helm (0 = use `IdleAutocenter`) |
| `IdleCenterDelaySeconds` | 3 | After leaving the helm, the wheel stays free this long before centering |
| `IdleAutocenter` | 0.25 | Driver centering spring when `IdleCenterForce` is 0 |
| `SpeedUpButtons` / `SpeedDownButtons` | 292,290 / 293,288 | Comma-separated evdev key codes: right paddle + circle / left paddle + cross (use `WheelTest info` to find codes) |
| `CenterButtons` | 294,295,298,299 | Buttons that swing the rudder and wheel back to straight (R2, L2, R3, L3) |
