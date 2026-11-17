# HapticBridge help

## Connect a device

Enable Bluetooth, turn the device on, and close other apps that might hold its Bluetooth connection. Select **Find devices**, then connect a discovered device. The app exposes controls supported by its current device implementation; a catalog entry is not a guarantee that every function has been tested on that hardware.

## Manual controls and patterns

Use **Controls** to select motors and set individual levels. **Start selected** plays their levels together. **Linked intensity** changes the selected sliders. Choose a pattern and use **Play pattern** for synchronized output; **Ripple** offsets their timing. Duration `0` holds playback until stopped. Saved mixes are local files and can include device identifiers.

**STOP ALL** ends output and pauses external integrations. Escape does the same when the window has focus. If a Bluetooth connection is lost, the app cannot guarantee that a stop reaches the hardware; use the device's physical control when needed.

## Sound sync

Select motors under **Controls**. In **Sound**, choose **Bass rumble** or **Full sound**, then **Start listening**. Adjust sensitivity to suit the sound source. Audio comes from the current Windows output device and is not recorded. If you switch output devices, stop and start sound sync again.

## Local programs and mods

In **Integrations**, enable **Allow app and game controls**, then use **Copy connection** to configure a native client. Keep the copied token private. Only one integration session owns output at a time, and its heartbeat must remain active. A manual stop or expired lease ends its output. Re-enable integrations when you want a program to take control again. See **API** for the complete contract.

## FeelConnect

Open **Video sync**, paste the player's pairing link or QR image, or choose **Open QR image**. Select **Connect**, choose the local motors, then **Enable output**. Pairing credentials are specific to the session; request a fresh code if the service rejects an expired one. Player support depends on which protocol messages that player sends. A connected session with no control messages is different from an active cue.

## Troubleshooting

- **No devices:** check Bluetooth Low Energy support, device power and whether another app is connected.
- **Connected but no output:** select a motor, confirm its level is above zero and check the active source. Stop all pauses integrations until enabled again.
- **API unavailable:** close any second copy of the app using port `17896`. Check that the client uses the local connection file from this installation.
- **External session stops:** check network access, the player's playback state and pairing expiry, then reconnect deliberately.

Help, API documentation and dependency notices are bundled in the executable. Local settings and logs live under `%LOCALAPPDATA%\HapticBridge`; they may contain private device details.
