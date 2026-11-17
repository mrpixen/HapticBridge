# Release verification

- Automated checks cover pattern playback and stopping, audio analysis, catalog routing, JSON compatibility, FeelConnect parsing/transport and release tooling. These tests use synthetic fixtures and do not actuate hardware.
- Windows x64 EXE-only launch, Bluetooth output acknowledgements, heartbeat expiry and explicit global stop were verified; output ended at zero.
- A live FeelConnect pairing completed credential exchange, WebSocket authentication, authorized-room acknowledgements and online/offline presence updates. No video cues arrived during the 30-second receive window, so end-to-end video-to-device playback remains unverified.
- Windows x64 and ARM64 executables are built as self-contained single files. ARM64 is cross-compiled; execution on ARM64 hardware remains unverified.
- The capability catalog identifies model features. It is not a claim that every model, firmware version or non-vibration function is implemented or hardware-tested.

Build commands and the privacy review procedure are in **BUILD**. Service behavior and hardware compatibility can change independently of these checks.
