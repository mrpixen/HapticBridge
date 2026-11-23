# HapticBridge

A Windows app for controlling compatible Lovense devices, making vibration patterns, following system audio, and receiving feedback from local programs or FeelConnect sessions. No HapticBridge account is required.

## Run

1. Download the executable for your Windows architecture from [Releases](https://github.com/mrpixen/HapticBridge/releases) and open it.
2. Enable Bluetooth, turn on a compatible device, and disconnect it from other control apps.
3. Select **Find & connect**, then choose the motors you want to control. The Devices page also lets you scan and connect individually.

Use **Controls** for motor levels and patterns, **Sound** for audio sync, and **Video sync** for FeelConnect sessions. **Stop all** stops connected devices; Escape does the same while the window has focus.

Patterns can be edited, previewed and saved without a device. **Preview** animates the curves without sending output. **More → Duplicate curve** creates an independent, unassigned copy; **Assign curve to motor** copies it to a connected motor. Edits save as a local draft; use **More → Restore previous draft** after reopening. In **Sound**, choose the audio output to follow, then select **Start sound sync**.

**Screen sync** provides experimental VALORANT feedback from visible health drops and a calibrated kill badge. Choose a window, check recognition in preview, then enable output. It uses Windows window capture without game hooks or Overwolf. See [screen setup](docs/SCREEN-SYNC.md); actual gameplay accuracy and Riot approval are not established.

FeelConnect website playback is experimental. Pairing can succeed before a player sends any vibration cues; see [compatibility notes](docs/FEELCONNECT.md).

Requires Windows 10 version 2004 or newer, or Windows 11, with Bluetooth Low Energy support. Internet is needed for FeelConnect sessions. A single executable contains the app and its .NET runtime; settings and runtime caches are created locally when it runs. Device capabilities vary by model and firmware.

Mods and native programs can use the [local HTTP API](docs/API.md). The [Python example](examples/gateway_client.py) uses only Python's standard library.

## Build

Install the .NET 10 SDK, then run `./test.ps1` and `./build.ps1 -Locked` in PowerShell. The executable is written to `dist/win-x64/HapticBridge.exe`. See [build details](docs/BUILD.md) for other architectures and verification.

[Privacy and connection behavior](docs/PRIVACY.md) · [Third-party notices](THIRD-PARTY-NOTICES.md) · [MIT license](LICENSE)
