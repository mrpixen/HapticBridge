# HapticBridge help

## Connect a device

Enable Bluetooth, turn the device on, and close other apps that might hold its Bluetooth connection. **Find & connect** scans for ten seconds and connects devices found in that scan. **Devices → Find devices** scans without connecting so you can choose devices individually. The app exposes controls supported by its current device implementation; a catalog entry is not a guarantee that every function has been tested on that hardware.

## Manual controls and patterns

In **Controls → Live**, choose motors and set their individual 0–20 levels. **Start selected** plays them together. **Linked level** adjusts every selected motor. Live timing sets duration and transition; duration `0` runs until stopped.

In **Pattern**, choose a motor tab and draw its curve. Double-click to add a point, drag to edit it, or use **Edit point** for exact time and level. **More** contains pattern length, looping, presets, linking and mix files. Link selected motors to share one curve, or leave their curves independent. All selected motors use the same playback clock. Undo and Redo work on curve edits. Editing a playing curve stops it before applying changes.

New mix files contain numbered motor lanes rather than device names or identifiers. When opened, lanes map to the connected motors in their displayed order. Older mix files can contain device identifiers.

**STOP ALL** ends output and pauses external integrations. Escape does the same when the window has focus. If a Bluetooth connection is lost, the app cannot guarantee that a stop reaches the hardware; use the device's physical control when needed.

## Sound sync

Select motors under **Controls** or **Sound**. In **Sound**, choose **Bass rumble** or **Full sound**, then **Start sound sync**. Adjust sensitivity and each motor's maximum level. Audio comes from the current Windows output device and is not recorded. If you switch output devices, stop and start sound sync again.

## Local programs and mods

In **Integrations**, enable **Allow app and game controls**, then use **Copy connection** to configure a native client. Keep the copied token private. Only one integration session owns output at a time, and its heartbeat must remain active. A manual stop or expired lease ends its output. Re-enable integrations when you want a program to take control again. See **API** for the complete contract.

## FeelConnect

Copy the QR image from the player, open **Video sync**, and press **Ctrl+V** or **Paste QR or code**. You can also paste a pairing code, drop a local QR image, or choose **Open image**. Select **Connect**, choose the local motors, then **Enable output**. Pairing credentials are specific to the session; request a fresh code if the service rejects an expired one. Player support depends on which protocol messages that player sends. A dash means no current signal; a received zero is shown as `0%`. Pairing alone does not mean the player is sending cues.

For an older player, expand **Playback settings**, choose **Live levels (legacy)**, and select **Apply mode**. This reconnects and pauses output; select **Enable output** to resume. Live levels stops if updates pause for five seconds. Use **Timed cues** for scheduled playback when supported.

## Window and appearance

Drag the title bar to move the app; double-click it to maximize or restore. The custom window buttons minimize, maximize/restore, and close. **Settings** offers Glass or Solid surfaces and reduced motion. Closing cancels playback and attempts to stop connected motors before exiting; a disconnected device cannot acknowledge that stop.

## Troubleshooting

- **No devices:** check Bluetooth Low Energy support, device power and whether another app is connected.
- **Connected but no output:** select a motor, confirm its level is above zero and check the active source. Stop all pauses integrations until enabled again.
- **API unavailable:** close any second copy of the app using port `17896`. Check that the client uses the local connection file from this installation.
- **External session stops:** check network access, the player's playback state and pairing expiry, then reconnect deliberately.

Help, API documentation and dependency notices are bundled in the executable. Local settings and logs live under `%LOCALAPPDATA%\HapticBridge`; they may contain private device details.
