# Release verification

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
