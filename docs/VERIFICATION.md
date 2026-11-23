# Release verification

## 0.8.4 — Advanced device controls — 2026-11-23

Two conflicts were reproduced before correction. An advanced device slider could raise a Screen sync damage pulse above its configured strength; edits now prepare the next manual value without changing screen-owned output. Explicit manual Start still takes control normally.

An all-motors pattern request could start a later motor after Stop all cancelled its first pending start. The request now checks its operation, device generation and player before each motor and after awaited playback admission. Tests exercise a blocked preset stop, global cancellation, replacement connections, explicit restarts and independent manual levels. An old request cannot start a reconnected player.

Native suites and focused reconnection checks passed with invisible controls, synthetic HUD frames and fake command links. The new screen checks also confirm that the bounded cue completes while the prepared slider value is retained. No GUI control, actual capture, real shortcut registration or physical output was used. Real-match recognition, live shortcut delivery and ARM64 execution remain unverified.

## 0.8.3 — Global Stop all and screen diagnostics — 2026-11-23

**Settings → Global Stop all** provides an optional Windows-registered shortcut. It is disabled by default. Users choose two or three modifiers and F6–F11, then explicitly enable it. A conflicting replacement preserves the working shortcut; disable and close unregister it. Only the current registration can invoke Stop all, and each press cancels pending starts immediately even while an earlier stop awaits a slow transport. No keyboard hook or input injection is used.

Passed: 26 shortcut-controller assertions and 214 native Studio assertions, including settings persistence, registration conflicts, startup failure, stale messages, cleanup and repeated-press ownership races. Tests use a fake registrar and simulated motors without displaying a window or registering a real shortcut. Actual delivery while backgrounded, minimized or playing a game, visual layout and ARM64 execution remain unverified.

**Copy diagnostics** now includes capture, output, frozen/fresh/stale frame state, shield mode, learned-template presence and selected connected motor count. It does not include window identity, pixels, calibration coordinates, recognized values or event history, and does not sample foreground ownership. No recognition-accuracy claim is inferred from template presence.

The native suite also passed 74 screen, 21 audio and 17 legacy-import assertions. Synthetic checks verify anonymous diagnostics, each frame state and target count, and no capture, foreground, ownership or device-command side effects. Both the shortcut and diagnostics additions are included in 0.8.3. Live-game recognition and automatic third-party video compatibility are unchanged.

Two screen transition bugs were reproduced before correction. Motor selection and strength changes stopped output but left the status saying it was enabled; the status now distinguishes stopping, confirmed off and unconfirmed stops, and clears a failure after a successful retry. A slow handoff could also finish arming after health changed to zero. Arming now checks the current HUD after all stop waits and rejects zero or unreadable health. Regression tests cover motor selection, ceilings, event strengths, explicit disable, failed stops and changing HUD readings during a blocked transport. Logs and API status now derive the app version from the built assembly instead of separate version literals.

## 0.8.2 — API request deadlines — 2026-11-23

A regression reproduced a queued motor command starting after its HTTP request had timed out. Closing the socket had not cancelled dispatch. The five-second deadline and server shutdown now cancel pending dispatch, source handoffs and playback starts. Stops already admitted still finish, and accepted output retains its own duration and heartbeat lease. A client disconnecting earlier still cannot infer whether its request was accepted.

Passed: all 16 core suites, including 22 API assertions; 191 native Studio, 47 screen, 21 audio, 17 legacy-import, 41 QR and 80 video-pipeline assertions; six Python tests. The timeout regression failed before correction. Tests also cover cancellation between motors, during session acquisition, while waiting for the playback gate or a firmware-preset stop, and accepted HTTP pattern output continuing after its response. All devices, clipboard inputs and capture frames were simulated; network tests used private loopback listeners. No GUI, actual capture or physical output was used. Live-game recognition and ARM64 execution remain unverified.

## 0.8.1 — Pattern ordering and inversion — 2026-11-23

A regression reproduced curves and selection settings moving to the wrong motor after saving and reopening with an earlier-discovered device disconnected. Export used document insertion order; import used connected-motor order. Export now uses the same motor slots as import, followed by unassigned curves. Tests cover disconnections, drafts created before device discovery, link groups, levels, selections and identifier-free files.

**More → Invert levels** complements the active curve's levels within 0–20, preserving timing, interpolation and linked groups. One Undo restores it. The command is unavailable without an active curve or during a point drag. A changed playing pattern stops and needs an explicit restart.

Passed: 73 curve-document checks, 163 native Studio assertions, 47 screen, 21 audio-routing and 17 legacy-import assertions. The ordering regression failed before its fix. Inversion tests exercise all interpolation modes, Undo/Redo and real playback scheduling with simulated motors, including no positive output during Undo/Redo before explicit restart. No GUI access, real capture or physical output was used. The 0.8.1 preview includes these changes; live-game and new-window validation remain outstanding.

## 0.8.0 release scope

This release includes optional shield damage detection, independent curve duplication and local draft recovery, with the checks described below. Validation used synthetic frames and simulated devices without accessing a running game or showing a window. No new live-game, physical-device or ARM64 execution result is claimed. Automatic third-party video playback remains unresolved.

## Shield damage — 2026-11-23 (source update)

The health-only detector missed damage absorbed entirely by shields. Optional shield calibration now reads a separate 0–50 number, requires three consistent readings, and emits damage while health is unchanged. Simultaneously confirmed health/shield decreases form one event. Regeneration is silent; unreadable shield digits discard that baseline instead of implying zero. Older profiles leave shield reading off. Calibration undo, reset and save include the shield region and enabled state; changing the mode disarms output.

All 16 core suites passed, including 75 screen-filter assertions covering combined damage, regeneration, invalid/low-confidence readings, reacquisition, death and older profiles. The native screen suite passed 47 assertions, including independent pixel recognition, shield learning/undo, persisted calibration and bounded feedback to both simulated motors. Studio (143), audio routing (21) and legacy import (17) also passed. No visible UI, game capture or physical device was accessed. Actual gameplay, visual layout and ARM64 execution remain unverified. This addition is not in the published 0.7.1 executable.

## Draft recovery — 2026-11-23 (source update)

Pattern edits save a local draft after a 750 ms pause. Startup offers **More → Restore previous draft** without changing the editor or starting output. Recovery uses normal mix import, preserving curves, links, levels, selection and timing. Timing changes alone before a pattern is edited do not replace a previous draft.

Offline tests covered debounce, close-time flushing, recovery with and without simulated motors, invalid timing, unfinished drags, malformed files and a locked destination. Failed replacement retained the previous valid file and removed the temporary file. The Studio suite passed 143 assertions; native screen (34), audio routing (21) and legacy import (17) also passed. No GUI, real screen capture or hardware was accessed. These source additions are not included in the published 0.7.1 executable.

## Renewal test timing — 2026-11-23

A CI timeout in `BoundedRetriesAndHardExpiry` was reproduced by delaying its synthetic failed response. The test advanced its fake clock when the request started, before the receiver scheduled its next retry. It now waits for that renewal attempt to finish before advancing time; the delayed response remains in the test. All 16 core suites passed afterward. No receiver runtime behavior or test timeout was changed.

## Curve duplication — 2026-11-23 (source update)

**More → Duplicate curve** copies the active curve to an independent, unassigned lane. It preserves interpolation, saved level and selection metadata, including when the source is linked to another curve. Editing the copy leaves the source group unchanged. Copies can be previewed, saved and assigned to a motor later.

Offline tests exercised copying both connected and unassigned lanes, independent edits, complete save/load round trips, the 64-lane limit and absence of output commands. The Studio suite passed 123 assertions; screen, audio and legacy suites also passed. No visible window, game capture, GUI automation or physical device was used. This menu addition has not received a new visual review.

## Calibration and playback fixes — 2026-11-23

Three failures were reproduced with regression tests before correction:

- Cancelling screen output during a slow handoff from another source could still let the pending enable complete. The handoff now checks for screen cancellation before proceeding.
- Dragging a HUD region left already-open coordinate fields stale. The fields now follow the selection, and editing a saved region marks it unsaved.
- Importing an older Ripple mix discarded the saved phase offsets. Each imported curve now retains its original phase, including when current motors are missing, reordered or newly connected.

Undo calibration restores up to 20 region and learning edits without reverting feedback strengths. Native UI checks with a synthetic HUD confirmed reset/undo restored health recognition and the learned badge, empty Undo stayed disabled, and dragging refreshed open coordinate fields. No physical motors or game window were used.

The full run passed all 16 core suites (including 58 detector assertions), 41 QR assertions, 80 video-pipeline assertions, 34 screen-sync assertions, 21 audio-routing assertions, 17 legacy-import assertions, 107 Studio assertions and six Python tests. Real-match recognition and ARM64 execution remain unverified; live video compatibility is unchanged.

## Screen sync preview — 2026-11-22

Screen detection was subsequently requested as an experimental feature. This supersedes the earlier deferral below; it does not establish real-game accuracy.

- Windows Graphics Capture successfully captured a separate synthetic HUD window. Freeze, drag selection, digit learning, badge learning, calibration save and resume were exercised through the native UI. A 100-to-80 health change produced one damage event; a cleared and reappearing badge produced one kill event. Preview sent no motor output. Escape stopped capture. Compact controls were checked at 850 × 620.
- The pure detector passed 58 assertions covering confirmation, healing, missing and out-of-order frames, stale badges, deduplication, malformed profiles and empty images.
- The native screen pipeline passed 26 assertions with synthetic pixels and actuator transports. Coverage includes multi-motor feedback, automatic pulse completion, focus loss, no automatic rearm, stale capture, death, cancellation during blocked writes, stop failure/retry, newer-owner protection, pending-arm cancellation and late capture callbacks.
- All 16 core suites, 41 QR assertions, 80 video-pipeline assertions, 107 existing Studio assertions, 21 audio-routing assertions, 14 legacy-import assertions and six Python tests passed. No physical output or game capture was used by these tests.
- No new package, external recognition service or game asset is required. The Windows capture indicator is retained. Captured frames stay in memory; explicit calibration saves only small binary templates, regions and levels to a private local file.

Real VALORANT match recognition, game-load performance, spectator discrimination and ARM64 execution remain unverified. Avoiding process injection does not guarantee Riot approval. The separate Overwolf integration's approval gate remains unchanged. Live third-party video support is unchanged.

## Controls and sound update — 2026-11-22

Two reviewers scored offline authoring/preview, lossless mix imports, captured live-session timing and truthful output status 9/10. Selecting an audio endpoint scored 8/10 and 9/10. These changes were selected because they solve observed gaps and can be tested without hardware. A proposed gameplay HUD recognizer scored 6/10 and was deferred pending recorded gameplay and measured detection accuracy. Duplicate status and Undo/Redo controls were rejected at 2/10.

- Pattern preview samples the editable curves and animates a playhead without a Playback or Bluetooth call. Drafts can be created, linked, imported and saved with no connected device. Unassigned curves and older saved mixes retain every lane.
- A motor joining an active live session inherits its original deadline and transition. Editing duration to zero or leaving invalid text in the next-run fields cannot extend that session.
- Sound follows a selected Windows audio output. Selection, refresh and endpoint failure stop that source without automatically restarting. Inactive audio settings do not interrupt a newer manual source.
- Output stays marked unconfirmed when a transport does not acknowledge a stop; an unknown level is never presented as an acknowledged zero.
- The explicit sound Stop retries a failed stop only while that exact playback generation still owns the target. A new manual session, reconnect or hardware preset invalidates the retry, including while a write is queued.
- All 15 core suites, 41 Windows QR assertions, 80 video-pipeline assertions, 107 Studio assertions, 21 audio-routing assertions, 14 legacy-import assertions and six Python release-tool tests passed. Audio endpoints and actuator links were synthetic.
- The real native window was checked with zero and three simulated motors at 850 × 620. Offline point editing, visual preview, Stop all, readable audio endpoint names and refresh-to-stop passed. Preview keeps the graph and time labels visible without moving the footer.
- The neutral browser-helper fixture passed nine real-browser cases (45 assertions); 14 independent adversarial cases and six-file ZIP/source comparison also passed. This verifies the conditional clock adapter, not a third-party player or browser extension installation.

Physical output, real endpoint switching during capture, ARM64 execution and live third-party video playback require separate validation. This update does not change the disabled live VALORANT integration.

## Receiver update — 2026-11-17

- Ordered legacy batches are tested through the real decoder, client, native UI handlers and fake motor links. Coverage includes intermediate peaks, independent gains, delayed acknowledgements, queue expiry, overload, disarm and Stop while the UI is blocked.
- Guest renewal has 36 offline assertions for authentication, room acknowledgements, hard expiry, bounded retries, stale socket events and preservation of website presence and legacy subscriptions. A separate 14-assertion suite covers post-pairing room discovery and cancellation.
- The current native video pipeline has 80 assertions, and the ordered motor writer has 41. Tests do not operate Bluetooth hardware or a third-party player.
- Legacy failure tests preserve timeout, rejection, malformed-response and connection-loss reasons through cleanup. Intentional disconnect and replacement clear prior failure state; raw request and response data are excluded from displayed errors.
- These fixes do not establish live website playback compatibility. A recent paired session received no candidate cues; its motor output remained off. Live token renewal and third-party playback remain unverified.

## Native controls update — 2026-11-17

- The native curve editor, linked curves, 0–20 levels, Undo/Redo and compact layout were exercised in a review window with simulated motors. Synchronized output and Stop all were verified through the actual UI.
- Custom minimize, maximize, restore and close controls were exercised on Windows. Edge resizing and title-bar double-click were also verified in the installed app. Maximizing uses the current monitor's work area so the output dock stays above the taskbar. Multi-monitor/DPI combinations and Windows Snap flyout interaction are not yet exhaustively tested.
- The offline suites passed: 11 core suites (including 52 curve-model checks, 40 playback checks and 44 V2 diagnostics checks), 41 QR assertions, 54 video-pipeline assertions and 78 native Studio assertions. Studio coverage includes simultaneous motors, independent selection, shared deadlines, blocked transport, stop/start races, manual takeover, disconnect/reconnect and atomic import rollback.
- The native app builds without warnings or errors. These UI and transport changes still require a fresh end-to-end third-party video playback check.

## Earlier service and hardware checks

- Automated checks cover pattern playback and stopping, audio analysis, catalog routing, JSON compatibility, FeelConnect parsing and both transports, clipboard QR formats, and release tooling. These tests use synthetic fixtures and do not actuate hardware.
- Synthetic timed packets generated by the official SDK pass through the actual legacy receiver, UI routing and playback engine to fake motor links. Checks cover intensity, independent gain, arming, queued cues, pause, muted routes and disconnect; this does not verify a third-party website's playback.
- Windows x64 EXE-only launch, Bluetooth output acknowledgements, heartbeat expiry and explicit global stop were verified; output ended at zero.
- Live FeelConnect pairing completed credential exchange, WebSocket authentication, authorized-room acknowledgements, legacy subscriptions and online/offline presence updates. The official API sandbox delivered a nonzero intensity command through the app to connected hardware; explicit zero and timeout stopped output. Third-party video playback remains unverified. The app distinguishes pairing from receiving a control signal.
- The Windows app decoded a QR image through its file picker. Automated Windows checks cover browser PNG bytes and streams, native bitmaps, text codes, transparency, malformed data and image-size limits.
- Windows x64 and ARM64 executables are built as self-contained single files. ARM64 is cross-compiled; execution on ARM64 hardware remains unverified.
- The capability catalog identifies model features. It is not a claim that every model, firmware version or non-vibration function is implemented or hardware-tested.

Build commands and the privacy review procedure are in **BUILD**. Service behavior and hardware compatibility can change independently of these checks.
