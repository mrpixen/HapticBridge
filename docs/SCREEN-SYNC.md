# Screen sync

Screen sync reads a selected window through Windows Graphics Capture. It estimates damage from health and optional shield decreases, and recognizes a kill confirmation badge you teach it. It does not read game memory, inject code, automate input, or use Overwolf. This is experimental and is not approved or certified by Riot.

## Setup

1. Run the game in a window or borderless mode. Use SDR first. Connect your device in HapticBridge.
2. Open **Screen sync**, choose the game window, then **Start preview**. This does not enable vibrations.
3. Open **Calibration**. Select **Health**, press **Freeze**, and drag a rectangle around just the health digits. Leave enough width for all three digits. Enter the number visible in that frame and press **Learn digits** if recognition is incorrect. The match readout should show the correct health value.
4. To recognize kills, freeze a frame containing your own kill confirmation badge. Choose **Kill badge**, select its region, then press **Learn kill badge**. Do not select the shared kill feed. Resume preview and check that the event history recognizes a new badge once.
5. **Save calibration**, then **Resume preview**. Choose the motors and event strengths. Select **Enable output** and return to the selected game window within eight seconds.

You can also set the selected region using **Region coordinates**, measured as percentages of the captured window. **Save calibration** keeps the current setup on this computer. Changing resolution, window borders, UI scale or badge cosmetics may require recalibration. Learn more digits from other health values when needed; built-in font templates cover digits you have not taught it.

Kill badges vary with weapon skins and animations. Learn a stable, clearly visible appearance for each one you use; **Learn kill badge** retains up to eight appearances. Keep the same badge region while adding them. Changing that region clears the learned badges. **More → Clear learned badges** starts a new set, and **Undo** restores the previous set. Newly exported setups use format version 2; this app still opens version 1 setups, but older app versions cannot open the new exports. This does not provide automatic recognition of every skin.

For shield damage, turn on **Read shield damage** in Calibration, choose **Shield**, then freeze and select only its current 0–50 number. Exclude the icon and regeneration reserve. Enter the visible number and use **Learn digits** if needed. Check the shield readout, then **Save calibration**. Shield reading is off by default; changing it disarms output. An unreadable or absent number is unknown, not zero, so health detection continues but some shield breaks may be missed.

Depleted armor can display a dim zero. When normal shield matching fails, the reader tries local contrast within the selected digit region, retaining its usual shape-confidence checks. Keep the region tight around the number. Flat or weak-contrast crops remain unknown. You can also learn a dim shield digit; this fallback does not change health or badge recognition.

**Undo calibration** restores a changed region, learned digits or badge, including after Reset. It keeps the last 20 edits until the app closes. Press **Save calibration** after undoing to keep the restored setup. Feedback strengths are unchanged.

Under **Calibration → More**, **Save setup** exports a `.hud.json` file; **Open setup** loads one. Use separate files for different HUD layouts or move a setup to another computer. Files contain recognition regions, learned templates and the shield-reading choice, without motor assignments or strengths. Opening stops output and replaces calibration as one undoable edit. Check the preview, then **Save calibration** to keep it across launches. Loading never starts capture or output. These files contain your calibration data; keep them out of public bug reports.

## Feedback

- **Damage:** a brief pulse on confirmed health or enabled shield loss, stronger at lower remaining health. The slider sets its maximum. Simultaneously confirmed losses become one event; shield regeneration produces no event.
- **Kill:** two short pulses, ending at the selected strength.
- **Death:** a brief pulse when zero health is confirmed, then output disarms.

Each event and motor supports native levels 0–20. Zero disables that event or motor. Motor maximums apply to every event. Each motor has a 450 ms cooldown: death takes priority over kills, and kills over damage. A higher-priority cue can replace a lower-priority one; repeated or lower-priority cues are skipped during cooldown. Events assigned to different motors can play together. Muted or unassigned events do not consume a cooldown. Rapid badges that never disappear may count as one kill. Output is bounded to at most 450 ms per event, even if a device command is delayed.

Under each motor, choose **Damage**, **Kill** and **Death** independently. For example, one motor can follow damage while another handles kills. New motors start with all three choices selected. Changing a choice, strength or maximum turns output off; select **Enable output** again when ready.

Event strengths, motor selection, maximums and event choices save automatically on this computer. They follow the same motor when discovery order changes. Calibration still uses **Save calibration**. Restoring preferences never starts capture or output.

Event history shows **preview**, **muted**, **cooldown**, or how many motors a cue was queued for. **Queued** means playback was requested, not that Bluetooth delivery was confirmed. Cancellation and source or command failures appear separately.

Leaving the selected window, losing a usable HUD signal, resizing it, changing the source or losing a selected device disarms output. Returning to the window does not re-enable it. **Stop capture** releases capture; **Stop all** also stops the other output sources. Escape invokes Stop all while HapticBridge has focus, not while the game has focus.

**Stopping output** means a device stop is still pending. **Output stop unconfirmed** stays visible if that stop fails, even after capture ends or calibration changes. It clears after a successful retry or when that playback is replaced. Stopping capture alone does not confirm that the device has stopped.

To stop without switching windows, opt into **Settings → Global Stop all** and choose an unused shortcut. The status must say **Active**. This uses a Windows-registered shortcut, with no game hook or input injection, and stops all sources rather than toggling output. The setting is off by default. Registration and stopping logic have offline test coverage; delivery during actual gameplay remains unverified.

To re-enable feedback after death or focus loss, open **Settings → Enable Screen sync shortcut**. It is off by default; its suggested binding is **Ctrl + Shift + F9**. After enabling the shortcut, press it in the captured window with a live preview and readable positive health. It enables feedback without switching apps or sending a test pulse. Pressing it while already enabled does nothing. It never starts capture: after **Stop all** or **Stop capture**, start a preview again first. Focus changes, Stop all, shortcut changes or a reconnect cancel a pending enable. A new connection requires another explicit enable.

## Recognition limits

The event preview reports matches, not authoritative game events. Use it before enabling output. Three consistent health readings confirm a change; two badge matches after a cleared interval confirm a new kill. For rapid kills without a full clear, the detector requires a deep visibility drop, two rebuilding frames and three strong matches within a short window. A brief disappearance that returns directly to the same badge does not count again. Some animations can still be missed or mistaken for a restart. Unreadable frames are ignored and sustained signal loss disarms output.

Health loss from abilities or decay can resemble damage. Hits absorbed without changing health require shield reading and readable shield digits. Health and shield values must each repeat across three frames; a shield baseline is discarded when that number becomes unreadable, preventing a false hit on reacquisition. Spectating and replay HUDs may resemble your own HUD: keep output off in those modes. A disappearing death HUD may produce signal loss instead of a death cue. Background effects, overlays, compressed video, HDR, font changes and animated badges can cause missed or incorrect detections.

Native Windows capture, calibration, damage and kill previews were tested against a synthetic HUD. Automated tests cover event filtering, multiple simulated motors, delayed commands, source cancellation and stop retries. Real VALORANT match accuracy, latency under game load, and ARM64 capture remain unverified.

## Implementation references

- [OpenCV: Otsu thresholding](https://docs.opencv.org/4.5.5/d7/d4d/tutorial_py_thresholding.html). The shield fallback uses a local luminance histogram; it adds no OpenCV runtime dependency.
- [Microsoft: Windows screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) and [window capture interop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow).
- [Microsoft: RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey). Both shortcut actions use separate application ID ranges and suppress keyboard auto-repeat. Conflicting bindings keep the existing working shortcut.
- [Riot: VALORANT developer policies](https://developer.riotgames.com/docs/valorant). Avoiding game hooks is an implementation choice, not a guarantee of approval or exemption from product-registration rules.
- [Riot: Regen Shield introduction](https://playvalorant.com/en-gb/news/game-updates/valorant-patch-notes-9-10/). Its damage absorption can leave health unchanged, which is why shield reading is available separately.
- [Microsoft: Windows OCR](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr). That API's desktop support requires package identity. This unpackaged single-executable app instead uses local digit templates and a user-calibrated badge, with no cloud recognition service.
