# Helmsman

A client-side Valheim mod that steers ships with a force-feedback racing wheel, with the wheel physically held to the ship's rudder.

- **Turn the wheel to steer.** The wheel sets a target rudder angle and the rudder moves toward it at the game's normal rate. Keeping up with the rudder costs no effort; turning faster than it can move is resisted.
- **The wheel follows the rudder.** Steering with A/D turns the wheel too, and when you leave the helm the wheel recenters after a short delay.
- **Buttons:** change sail speed with the paddles or O/X, and swing the rudder back to straight with L2/R2/L3/R3.
- **Client-side only.** The mod feeds ordinary steering input to the game, so it works on any server and other players don't need it.

**Status:** tested on native Linux with a Logitech G29. Windows, macOS and other wheels aren't supported yet (see [Roadmap](#roadmap)).

## Requirements

- Valheim, **native Linux build**.
- A wheel with constant-force feedback under Linux. The Logitech G29 needs its mode switch set to **PS3**: in PS4 mode the kernel has no force-feedback driver for it.
- Read/write access to the wheel's `/dev/input/event*` device. Most desktop distros grant this to the logged-in user; otherwise add yourself to the `input` group.
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) for Linux x64.

## Install

### 1. BepInEx

Pick one:

- **Plain BepInEx 5 (keeps achievements).** Download `BepInEx_linux_x64_5.4.x.zip` from the [BepInEx releases](https://github.com/BepInEx/BepInEx/releases) and extract it into the Valheim folder (`~/.local/share/Steam/steamapps/common/Valheim`). Then edit `run_bepinex.sh` there, set `executable_name="valheim.x86_64"`, and make it executable (`chmod +x run_bepinex.sh`).
- **[BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)**, which most Valheim mods and mod managers use. Note that it marks the game as modded (`Game.isModded`), which **disables achievements** while it is installed.

### 2. Helmsman

Download `Helmsman-<version>.zip` from the [latest release](https://github.com/nelseric/helmsman/releases/latest) and extract it into the Valheim folder. It contains `BepInEx/plugins/Helmsman/Helmsman.dll`.

### 3. Steam launch options

In Steam: Valheim → Properties → Launch Options:

```
./run_bepinex.sh %command%
```

If you already wrap the game (for example with gamescope), put the script after the wrapper's `--`:

```
gamescope -w 3840 -h 2160 -f -- ./run_bepinex.sh %command%
```

### 4. Check it works

Launch the game. At the main menu the wheel should snap to center. `BepInEx/LogOutput.log` should contain a line like:

```
[Info   :  Helmsman] Using Logitech G29 Driving Force Racing Wheel (046d:c24f) at /dev/input/event24, range 900°
```

Take a ship's helm and turn the wheel.

## Controls (G29 defaults)

| Control | Action |
| --- | --- |
| Wheel | Steer |
| Right paddle, O | Raise sail speed |
| Left paddle, X | Lower sail speed |
| L2, R2, L3, R3 | Swing the rudder (and wheel) back to straight, once |
| A / D | Still steer; the wheel follows |

When you take the helm, the wheel is first pulled to the rudder's position and takes over once it is within `SyncToleranceDegrees`. After steering with A/D, it takes over again once it has caught up with the rudder.

## Configuration

Settings live in `BepInEx/config/helmsman.wheel.cfg`, created on first launch. BepInEx reads the file at startup, so restart the game after editing it. With [BepInEx.ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) installed, settings can be changed in-game (F1) and take effect immediately.

| Section | Setting | Default | Meaning |
| --- | --- | --- | --- |
| General | `Enabled` | `true` | Turn the mod on or off |
| Device | `DevicePath` | *(empty)* | Wheel device such as `/dev/input/event24`; empty picks the first force-feedback wheel |
| Device | `FallbackRangeDegrees` | `900` | Wheel rotation range if the driver doesn't report one |
| Steering | `DegreesAtFullRudder` | `450` | Wheel angle from center that equals full rudder. Lower for a quicker wheel |
| Steering | `InvertSteering` | `false` | Swap left and right |
| Steering | `SyncToleranceDegrees` | `15` | How close the wheel must get to the rudder before it takes over at the helm |
| ForceFeedback | `MaxForce` | `0.7` | Force cap, as a fraction of the wheel's peak torque |
| ForceFeedback | `Stiffness` | `0.03` | Force per degree the wheel is away from the rudder |
| ForceFeedback | `Damping` | `0.0015` | Resistance per °/s of turning faster than the rudder |
| ForceFeedback | `DeadbandDegrees` | `0.5` | Position error ignored, to avoid buzzing |
| ForceFeedback | `MinForce` | `0.12` | Extra push to get a stationary wheel past gearbox friction |
| ForceFeedback | `InvertForce` | `false` | Flip force direction if the wheel is pushed away from the rudder |
| ForceFeedback | `IdleCenterForce` | `0.5` | Force holding the wheel centered away from the helm; `0` uses `IdleAutocenter` instead |
| ForceFeedback | `IdleCenterDelaySeconds` | `3` | How long after leaving the helm before the wheel recenters |
| ForceFeedback | `IdleAutocenter` | `0.25` | The driver's own centering spring, used when `IdleCenterForce` is `0` |
| Buttons | `SpeedUpButtons` | `292,290` | Key codes that raise sail speed |
| Buttons | `SpeedDownButtons` | `293,288` | Key codes that lower sail speed |
| Buttons | `CenterButtons` | `294,295,298,299` | Key codes that straighten the rudder |
| Debug | `LogPath` | *(empty)* | Write a CSV of wheel, rudder target and force (~500 lines/s) to this file |

**Feel:** if the wheel feels heavy, lower `Stiffness` or `MaxForce`. If it doesn't settle on the rudder, raise `MinForce` slightly. If it buzzes at rest, raise `DeadbandDegrees` or lower `MinForce`.

**Button codes** are Linux evdev key codes. To find your wheel's codes, run the test tool's `info` mode (see [Development](#development)) and press the buttons.

## Troubleshooting

- **Log says `Skipping [Helmsman …] because of process filters`:** you're on an old Helmsman build; update it.
- **The wheel isn't found, or has no force:** check `/proc/bus/input/devices` lists the wheel with `FF=` in its `B:` lines. For the G29, it must show product `c24f` (PS3 mode) and use the `logitech` driver. If you've just updated your kernel, reboot so its driver modules match.
- **The wheel pushes the wrong way:** set `InvertForce = true`.
- **Steering is reversed:** set `InvertSteering = true`.

## Development

Requires the .NET 10 SDK. The plugin builds without Valheim installed; it looks up the game's types at runtime.

```sh
dotnet build -c Release   # builds everything; copies the plugin into a local Valheim BepInEx install if present
dotnet test               # unit tests and closed-loop simulation tests
```

`tools/WheelTest` talks to a real wheel without the game:

```sh
dotnet run --project tools/WheelTest -- info           # live angle; prints button codes as you press them
dotnet run --project tools/WheelTest -- force 0.3      # short push to verify force direction
dotnet run --project tools/WheelTest -- hold 90        # hands-off: hold the wheel at an angle
dotnet run --project tools/WheelTest -- track 200 90   # hands-off: follow a rudder sweeping at 200°/s
dotnet run --project tools/WheelTest -- kbd 1          # hands-off: hold "D" for 1s, check the rudder stops
dotnet run --project tools/WheelTest -- sim            # steer a simulated rudder by hand
```

Set `HELMSMAN_LOG=<file.csv>` to log the force loop from the test tool.

**Layout:**
- `src/Helmsman/Core`: steering decisions (`HelmController`) and a model of Valheim's rudder. No Unity dependencies.
- `src/Helmsman/Wheel`: the force law (`ForceModel`) and Linux evdev I/O.
- `src/Helmsman/Plugin.cs`, `GameApi.cs`: the BepInEx plugin and its hooks into Valheim.

**Releasing:** push a tag like `v0.2.0`. GitHub Actions tests, builds with that version, and publishes a release containing `Helmsman-0.2.0.zip`.

## Roadmap

- One wheel backend for Windows, macOS and Linux via SDL3.
- Per-wheel profiles and a calibration step: force direction, friction, rotation range, buttons.
- Force limits in Nm, with safe defaults for direct-drive wheels.
- A Thunderstore package.

## License

[MIT](LICENSE)
