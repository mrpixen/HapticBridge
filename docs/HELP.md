# HapticBridge help

## Connect a device

Enable Bluetooth, turn the device on, and close other apps that might hold its Bluetooth connection. **Find & connect** scans for ten seconds and connects devices found in that scan. **Devices → Find devices** scans without connecting so you can choose devices individually. The app exposes controls supported by its current device implementation; a catalog entry is not a guarantee that every function has been tested on that hardware.

## Manual controls and patterns

In **Controls → Live**, choose motors and set their individual 0–20 levels. **Start selected** plays them together. **Linked level** adjusts every selected motor. Live timing sets duration and transition; duration `0` runs until stopped.

In **Pattern**, choose a motor tab and draw its curve. Double-click to add a point, drag to edit it, or use **Edit point** for exact time and level. **More** contains pattern length, looping, presets, linking and mix files. Link selected motors to share one curve, or leave their curves independent. All selected motors use the same playback clock. Undo and Redo work on curve edits. Editing a playing curve stops it before applying changes.

Without a device, choose **Create a pattern**. **Preview** animates the curve and shows its sampled level without sending any motor commands. Editing, changing modes, or Stop all ends the preview. **More → Add unassigned curve** adds another draft; **Duplicate curve** makes an independent copy of the active curve. **Assign curve to motor** copies an unassigned curve to a connected motor. Starting device output still requires **Start selected**.

**More → Invert levels** flips the active curve's strengths (`0 ↔ 20`, `6 ↔ 14`) while preserving its timing. Linked curves change together; duplicate first for an independent opposite pattern. One Undo restores the original. Changing a playing pattern stops output until **Start selected** is pressed again.

Pattern edits automatically save one local draft after a short pause. After reopening, choose **More → Restore previous draft** to recover it. Recovery stops output before loading, uses the same motor mapping as opening a mix, and never starts playback. Invalid timing or an unfinished point drag leaves the last valid draft intact. Use **Save mix** to keep separate patterns.

New mix files contain numbered motor lanes rather than device names or identifiers. Saved lanes list connected motors first, then disconnected motor slots and unassigned curves. Opening uses the same motor order; extra lanes remain as unassigned drafts and survive saving. Older mix files can contain device identifiers, but conversion preserves their saved curves and saving in the new format removes those identifiers. Live timing is captured when playback starts: changing timing fields applies to the next start, including when another motor joins the current session.

**STOP ALL** ends output and pauses external integrations. Escape does the same when the window has focus. If a Bluetooth connection is lost, the app cannot guarantee that a stop reaches the hardware; use the device's physical control when needed.

For a shortcut while another app has focus, open **Settings → Global Stop all**, choose modifiers and a key, then **Enable shortcut**. It starts disabled. **Active** confirms Windows registered it; choose another combination if it is in use. **Apply shortcut** changes an active binding; a conflict keeps the previous one. **Disable** releases it. Choose a combination your game and other apps do not use.

## Sound sync

Select motors under **Controls** or **Sound**. In **Sound**, choose the **Audio output** to follow, choose **Bass rumble** or **Full sound**, then **Start sound sync**. Adjust sensitivity and each motor's maximum level. Audio is analyzed in memory without being recorded. Changing the selected output or pressing **Refresh** stops sound sync; start it again when ready. **Windows default** resolves the current default each time you start.

## Local programs and mods

In **Integrations**, enable **Allow app and game controls**, then use **Copy connection** to configure a native client. Keep the copied token private. Only one integration session owns output at a time, and its heartbeat must remain active. A manual stop or expired lease ends its output. Re-enable integrations when you want a program to take control again. See **API** for the complete contract.

## FeelConnect

Copy the QR image from the player, open **Video sync**, and press **Ctrl+V** or **Paste QR or code**. You can also paste a pairing code, drop a local QR image, or choose **Open image**. Select **Connect**, choose the local motors, then **Enable output**. Pairing credentials are specific to the session; request a fresh code if the service rejects an expired one. Player support depends on which protocol messages that player sends. A dash means no current signal; a received zero is shown as `0%`. Pairing alone does not mean the player is sending cues.

For an older player, expand **Playback settings**, choose **Live levels (legacy)**, and select **Apply mode**. This reconnects and pauses output; select **Enable output** to resume. Live levels stops if updates pause for five seconds. Use **Timed cues** for scheduled playback when supported.

## Window and appearance

Drag the title bar to move the app; double-click it to maximize or restore. The custom window buttons minimize, maximize/restore, and close. **Settings** offers Glass or Solid surfaces and reduced motion. Closing cancels playback and attempts to stop connected motors before exiting; a disconnected device cannot acknowledge that stop.

## Troubleshooting

**Settings → Copy diagnostics** includes Screen sync capture, output, frame freshness, learned-template presence and selected motor count without window names, captured images or recognized values.

- **No devices:** check Bluetooth Low Energy support, device power and whether another app is connected.
- **Connected but no output:** select a motor, confirm its level is above zero and check the active source. Stop all pauses integrations until enabled again.
- **API unavailable:** close any second copy of the app using port `17896`. Check that the client uses the local connection file from this installation.
- **External session stops:** check network access, the player's playback state and pairing expiry, then reconnect deliberately.

Help, API documentation and dependency notices are bundled in the executable. Local settings and logs live under `%LOCALAPPDATA%\HapticBridge`; they may contain private device details.
