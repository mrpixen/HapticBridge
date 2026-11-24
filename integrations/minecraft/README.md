# Minecraft Immersion 0.2.1

Client-only Fabric mod for Minecraft **26.3**, Fabric Loader **0.20.1+**, matching Fabric API, and Java **25+**. Sends game events to HapticBridge on the same computer. No server mod is required.

## Play

1. Open HapticBridge, connect your devices, and enable integrations.
2. Install this mod and the matching Fabric API in your Minecraft instance.
3. Put the gateway connection JSON in the location named by `gatewayConnectionFile` in `config/minecraft-immersion.json`.
4. Enter a world. **F8** toggles feedback; **F9** shows status. Stop all in HapticBridge pauses integrations until you enable them again.

## Default feedback

| Action | Output |
|---|---|
| Walk or sneak | Off |
| Sprint | Strong pulses on all selected outputs |
| Jump, fall, land | Strong feedback, with landing decay |
| Mine a block | All selected outputs rise from level 2 toward 18 as breaking progresses |
| Break a block | Level 20 completion burst; ores and rare ores have longer rewards |
| Cancel mining | Mining output stops without a completion reward |
| Take damage | Stronger as remaining health falls; maximum at 10% health |
| Eat, drink, swim, sleep, draw a bow, gain experience, change dimension | Separate configurable effects |

There are 41 event profiles. Walking, sneaking, airborne background feedback, and rain are disabled by default. Completion rewards have a short rest between peaks; urgent damage can interrupt that rest. Block-break feedback uses client prediction, so a multiplayer server can still reject a predicted break.

Menus, loss of focus, death, leaving a world, and disabling feedback pause output. The gateway session expires after three seconds without renewal. Device latency limits how closely short pulses can follow game events.

## Customize

Edit `config/minecraft-immersion.json`, then use `/immersion reload`.

- `/immersion status` — connection and output state.
- `/immersion toggle` or **F8** — enable or disable feedback.
- `/immersion stop` — disable feedback.
- `/immersion event block_break` — preview an event after closing chat.
- `/immersion level damage 20` — set an event maximum from 0 to 20.

Profiles contain `level`, `durationMs`, `shape`, `pulses`, `priority`, `cooldownMs`, and `route`. Shapes are `steady`, `pulse`, `decay`, `wave`, and `ramp`. The default mining profile reaches level 18 before completion; changing its level scales that ramp. Config revision 2 migrates selected older defaults once and preserves subsequent edits.

### Output routing

The mod discovers channels from HapticBridge; it does not require particular toy models. Use `deviceIds` to restrict which devices participate; an empty list includes all connected devices.

| Route | Channels |
|---|---|
| `motor1` | Channel 1 on selected devices |
| `motor2` | Channel 2 on selected devices |
| `gush` | Legacy name for all other discovered channels, including non-vibration functions |
| `both` | All discovered channels |
| `left` / `right` | Channel 1 / 2 plus the generic channels |
| `alternate` | Alternates channels 1 and 2; generic channels participate in each pulse |

Rotation, pumping, thrusting, suction and other continuous functions use the same normalized 0–20 levels through HapticBridge. Actual capability and native resolution depend on the device. The mod does not change stroke range, depth, position or rotation direction; configure those in HapticBridge. Use `deviceChannels` to include only specific channel IDs for a device:

```json
"deviceChannels": {
  "<device ID from HapticBridge>": [1, 7, 9]
}
```

An omitted device keeps all its discovered channels. An empty list disables that device's outputs. IDs must match device discovery; channels are integers 0–9. Use `/immersion reload` after editing. The `deviceIds` allowlist still applies. Channel 7 is thrusting, 8 is suction, and 9 is oscillation; only channels actually exposed by the device are selected.

## Build

Use PowerShell 7 and JDK 25. Install Minecraft 26.3 and Fabric Loader 0.20.1 first so their client/library files are available. This build uses Fabric API 0.166.0+26.3 and Mixin 0.17.5+mixin.0.8.7. Extract Fabric API's `META-INF/jars` into a dependency folder and include the main Fabric API JAR there too.

```powershell
./source/test.ps1 -JdkPath "C:/Java/jdk-25"
./source/build.ps1 -JdkPath "C:/Java/jdk-25" -FabricLibrariesPath "C:/Dependencies/fabric26"
```

Both scripts accept `-MinecraftRoot` for a nonstandard Minecraft installation and `-BuildDirectory` for compiler output. Tests also accept `-GsonPath` for the Gson JAR; otherwise they use the installed Minecraft library. `JAVA_HOME` can supply the JDK when `-JdkPath` is omitted. Each run uses a fresh classes folder so stale compiled files cannot enter a new JAR.

The output is `minecraft-immersion-0.2.1+26.3.jar` beside this README. Dependencies are not bundled with this source. The scripts require `-JdkPath` or `JAVA_HOME`; the build also requires `-FabricLibrariesPath`.

Close Minecraft before copying the JAR into an instance. Remove the older Immersion JAR so only one version is installed. Do not share gateway connection JSON: it contains your local API token.

## Verification

Engine/config tests exercise event envelopes, priorities, mining progress, rewards and migration. Offline loopback tests verify discovery and routing of channels 0–9, selected-only routing, reload during connection, and session release after a failed frame or gameplay pause. This does not prove physical behavior for every actuator or game event.
