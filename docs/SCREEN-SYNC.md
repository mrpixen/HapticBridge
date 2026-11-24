# Screen sync

Screen sync reads a selected window through Windows Graphics Capture. It estimates damage from health and optional shield decreases, and recognizes a kill confirmation badge you teach it. It does not read game memory, inject code, automate input, or use Overwolf. This is experimental and is not approved or certified by Riot.

## Setup

1. Run the game in a window or borderless mode. Use SDR first. Connect your device in HapticBridge.
2. Open **Screen sync**, choose the game window, then **Start preview**. This does not enable vibrations.
3. Open **Calibration**. Select **Health**, press **Freeze**, and drag a rectangle around just the health digits. Leave enough width for all three digits. Enter the number visible in that frame and press **Learn digits** if recognition is incorrect. The match readout should show the correct health value.
4. To recognize kills, freeze a frame containing your own kill confirmation badge. Choose **Kill badge**, select its region, then press **Learn kill badge**. Do not select the shared kill feed. Resume preview and check that the event history recognizes a new badge once.
5. **Save calibration**, then **Resume preview**. Choose the motors and event strengths. Select **Enable output** and return to the selected game window within eight seconds.

You can also set the selected region using **Region coordinates**, measured as percentages of the captured window. Calibration stays on this computer. Changing resolution, window borders, UI scale or badge cosmetics may require recalibration. Learn more digits from other health values when needed; built-in font templates cover digits you have not taught it.

For shield damage, turn on **Read shield damage** in Calibration, choose **Shield**, then freeze and select only its current 0–50 number. Exclude the icon and regeneration reserve. Enter the visible number and use **Learn digits** if needed. Check the shield readout, then **Save calibration**. Shield reading is off by default; changing it disarms output. An unreadable or absent number is unknown, not zero, so health detection continues but some shield breaks may be missed.

**Undo calibration** restores a changed region, learned digits or badge, including after Reset. It keeps the last 20 edits until the app closes. Press **Save calibration** after undoing to keep the restored setup. Feedback strengths are unchanged.

## Feedback

- **Damage:** a brief pulse on confirmed health or enabled shield loss, stronger at lower remaining health. The slider sets its maximum. Simultaneously confirmed losses become one event; shield regeneration produces no event.
- **Kill:** two short pulses, ending at the selected strength.
- **Death:** a brief pulse when zero health is confirmed, then output disarms.

Each event and motor supports native levels 0–20. Zero disables that event or motor. Motor maximums apply to every event. Damage and kill events closer than 450 ms may be skipped; confirmed death takes priority over that cooldown. Muted or unassigned events do not consume it. Rapid badges that never disappear may count as one kill. Output is bounded to at most 450 ms per event, even if a device command is delayed.

Under each motor, choose **Damage**, **Kill** and **Death** independently. For example, one motor can follow damage while another handles kills. New motors start with all three choices selected. Changing a choice, strength or maximum turns output off; select **Enable output** again when ready.

Event strengths, motor selection, maximums and event choices save automatically on this computer. They follow the same motor when discovery order changes. Calibration still uses **Save calibration**. Restoring preferences never starts capture or output.

Leaving the selected window, losing a usable HUD signal, resizing it, changing the source or losing a selected device disarms output. Returning to the window does not re-enable it. **Stop capture** releases capture; **Stop all** also stops the other output sources. Escape invokes Stop all while HapticBridge has focus, not while the game has focus.

To stop without switching windows, opt into **Settings → Global Stop all** and choose an unused shortcut. The status must say **Active**. This uses a Windows-registered shortcut, with no game hook or input injection, and stops all sources rather than toggling output. The setting is off by default. Registration and stopping logic have offline test coverage; delivery during actual gameplay remains unverified.

## Recognition limits

The event preview reports matches, not authoritative game events. Use it before enabling output. Three consistent health readings confirm a change; two badge matches and a cleared interval confirm a new kill. Unreadable frames are ignored and sustained signal loss disarms output.

Health loss from abilities or decay can resemble damage. Hits absorbed without changing health require shield reading and readable shield digits. Health and shield values must each repeat across three frames; a shield baseline is discarded when that number becomes unreadable, preventing a false hit on reacquisition. Spectating and replay HUDs may resemble your own HUD: keep output off in those modes. A disappearing death HUD may produce signal loss instead of a death cue. Background effects, overlays, compressed video, HDR, font changes and animated badges can cause missed or incorrect detections.

Native Windows capture, calibration, damage and kill previews were tested against a synthetic HUD. Automated tests cover event filtering, multiple simulated motors, delayed commands, source cancellation and stop retries. Real VALORANT match accuracy, latency under game load, and ARM64 capture remain unverified.

## Implementation references

- [Microsoft: Windows screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) and [window capture interop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow).
- [Riot: VALORANT developer policies](https://developer.riotgames.com/docs/valorant). Avoiding game hooks is an implementation choice, not a guarantee of approval or exemption from product-registration rules.
- [Riot: Regen Shield introduction](https://playvalorant.com/en-gb/news/game-updates/valorant-patch-notes-9-10/). Its damage absorption can leave health unchanged, which is why shield reading is available separately.
- [Microsoft: Windows OCR](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr). That API's desktop support requires package identity. This unpackaged single-executable app instead uses local digit templates and a user-calibrated badge, with no cloud recognition service.
