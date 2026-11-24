<p align="center">
  <img src="assets/readme-banner.svg" alt="HapticBridge" width="100%">
</p>

<p align="center">
  <a href="https://github.com/mrpixen/HapticBridge/releases"><strong>Download for Windows</strong></a> ·
  <a href="docs/SCREEN-SYNC.md">Screen sync setup</a> ·
  <a href="docs/API.md">Developer API</a> ·
  <a href="docs/VERIFICATION.md">Release checks</a>
</p>

Control compatible Lovense devices from one Windows app. Shape vibration patterns, follow system audio, or let local games and programs control individual motors. **One executable. No HapticBridge account.**

## What you can do

| Feature | Controls |
| :--- | :--- |
| **Motor controls** | Set each motor independently or control several together. |
| **Curve editor** | Draw, preview, save and assign vibration patterns. Preview works without a connected device. |
| **Sound sync** | Follow a selected audio output, with a bass-focused option. |
| **Screen sync** | Experimental VALORANT feedback from visible health, shield and kill badges. Learn multiple badge appearances for different skins. |
| **Video sync** | Pair FeelConnect sessions by code or QR image, including an image pasted from the clipboard. |
| **Game & app integrations** | Send commands through the local HTTP API. Discover motors and their capabilities at runtime. |

## Supported toys

The current control system supports **vibration** on the following Lovense model families, based on the bundled [device catalog](source/models.json). Catalog support is not a claim that every model or firmware has been physically tested.

| Vibration motors | Models |
| :--- | :--- |
| **1** | Ambi, Calor, Diamo, Domi, Exomoon, Ferri, Fizz, Flexer, Gravity, Gush, Hush, Hyphy |
| **1** | Lush, Lush 2, Lush 3, Lush 4, Lush Mini, Lush Anal; Max, Max 2; Mission, Mission 2 |
| **1** | Mini XMachine, Nora, Osci, Osci 2, Osci 3, Ridge, Spinel, Tenera, Tenera 2, Velvo, Vulse, XMachine |
| **2, independently controlled** | Dolce, Edge family (including Edge 2), Gemini |
| **3, independently controlled** | Lapis |

The catalog also contains the identifiers **C20** (one vibration motor) and **QA01** (two).

**Recognized only:** Gush 2, Solace and Solace Pro have no vibration channel in this build. Rotation, pumping, suction, flexing, thrusting, position/depth and oscillation controls are not implemented, even where the catalog identifies those capabilities. Generations with shared Bluetooth identifiers may appear as a family until the app can identify the model.

## Get started

**Requirements:** Windows 10 version 2004 or newer, or Windows 11; Bluetooth Low Energy for device connections. The .NET runtime is included. FeelConnect sessions require internet access.

1. Download `HapticBridge-win-x64.exe` from [Releases](https://github.com/mrpixen/HapticBridge/releases). Choose `win-arm64` for Windows on ARM.
2. Turn on Bluetooth and your device. Disconnect it from other control apps.
3. Open HapticBridge and select **Find & connect**. Use **Devices** to scan or connect individually.
4. Choose **Controls**, **Sound**, **Screen sync** or **Video sync** and select the motors to use.

**Stop all** stops connected output. **Escape** does the same while the app has focus. Previewing a curve does not send output; Screen sync and Video sync have separate output-enable controls.

### Games and other programs

In **Integrations**, enable **Allow app and game controls**, then use **Copy connection**. The API listens only on this computer and requires the generated local token. Keep that connection information private.

[API reference](docs/API.md) · [Python example](examples/gateway_client.py)

### Experimental features

- **Screen sync:** calibrate the visible HUD and check the event preview before enabling output. It uses Windows window capture, without game hooks or Overwolf. Recognition can miss or misread events; Riot approval is not established. [Setup and limits](docs/SCREEN-SYNC.md)
- **FeelConnect:** pairing alone does not mean a video is sending cues. Website playback remains experimental. [Compatibility and troubleshooting](docs/FEELCONNECT.md)

Device features vary by model and firmware. Settings and patterns are stored locally; a single-file download does not mean the app creates no local files. [Privacy and connections](docs/PRIVACY.md)

## Build from source

On Windows, install the **.NET 10 SDK**, clone this repository, and run these commands from its root in PowerShell:

```powershell
./test.ps1
./build.ps1 -Locked
```

Output: `dist/win-x64/HapticBridge.exe`.

For Windows on ARM:

```powershell
./build.ps1 -Runtime win-arm64 -Locked
```

Package restore needs internet access. [Build details](docs/BUILD.md) cover dependency locking, verification and packaging.

---

[MIT license](LICENSE) · [Third-party notices](THIRD-PARTY-NOTICES.md) · [Report an issue](https://github.com/mrpixen/HapticBridge/issues)
