# Screen sync

Screen sync reads a selected window through Windows Graphics Capture. It estimates damage from health decreases and recognizes a kill confirmation badge you teach it. It does not read game memory, inject code, automate input, or use Overwolf. This is experimental and is not approved or certified by Riot.

## Setup

1. Run the game in a window or borderless mode. Use SDR first. Connect your device in HapticBridge.
2. Open **Screen sync**, choose the game window, then **Start preview**. This does not enable vibrations.
3. Open **Calibration**. Select **Health**, press **Freeze**, and drag a rectangle around just the health digits. Leave enough width for all three digits. Enter the number visible in that frame and press **Learn digits** if recognition is incorrect. The match readout should show the correct health value.
4. To recognize kills, freeze a frame containing your own kill confirmation badge. Choose **Kill badge**, select its region, then press **Learn kill badge**. Do not select the shared kill feed. Resume preview and check that the event history recognizes a new badge once.
5. **Save calibration**, then **Resume preview**. Choose the motors and event strengths. Select **Enable output** and return to the selected game window within eight seconds.

You can also set the selected region using **Region coordinates**, measured as percentages of the captured window. Calibration stays on this computer. Changing resolution, window borders, UI scale or badge cosmetics may require recalibration. Learn more digits from other health values when needed; built-in font templates cover digits you have not taught it.

## Feedback

- **Damage:** a brief pulse, stronger at lower remaining health. The slider sets its maximum.
- **Kill:** two short pulses, ending at the selected strength.
- **Death:** a brief pulse when zero health is confirmed, then output disarms.

Each event and motor supports native levels 0–20. Zero disables that event or motor. Motor maximums apply to every event. Events closer than 450 ms may be skipped; rapid badges that never disappear may count as one kill. Output is bounded to at most 450 ms per event, even if a device command is delayed.

Leaving the selected window, losing a usable HUD signal, resizing it, changing the source or losing a selected device disarms output. Returning to the window does not re-enable it. **Stop capture** releases capture; **Stop all** also stops the other output sources. Escape invokes Stop all while HapticBridge has focus, not while the game has focus.

## Recognition limits

The event preview reports matches, not authoritative game events. Use it before enabling output. Three consistent health readings confirm a change; two badge matches and a cleared interval confirm a new kill. Unreadable frames are ignored and sustained signal loss disarms output.

Health loss from abilities or decay can resemble damage. Damage absorbed without changing health cannot be detected by this reader. Spectating and replay HUDs may resemble your own HUD: keep output off in those modes. A disappearing death HUD may produce signal loss instead of a death cue. Background effects, overlays, compressed video, HDR, font changes and animated badges can cause missed or incorrect detections.

Native Windows capture, calibration, damage and kill previews were tested against a synthetic HUD. Automated tests cover event filtering, multiple simulated motors, delayed commands, source cancellation and stop retries. Real VALORANT match accuracy, latency under game load, and ARM64 capture remain unverified.

## Implementation references

- [Microsoft: Windows screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) and [window capture interop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow).
- [Riot: VALORANT developer policies](https://developer.riotgames.com/docs/valorant). Avoiding game hooks is an implementation choice, not a guarantee of approval or exemption from product-registration rules.
- [Microsoft: Windows OCR](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr). That API's desktop support requires package identity. This unpackaged single-executable app instead uses local digit templates and a user-calibrated badge, with no cloud recognition service.
