# Release verification

## Unreleased — Stable output motor labels

The output bar now uses the same motor slots as the control tabs. Disconnecting earlier devices no longer renumbers remaining outputs. Its tooltip and accessible name identify each device and actuator. A synthetic disconnect regression reproduced the old mismatch; playback routing is unchanged. Passed 350 Studio, 236 Screen sync, 21 audio and 17 legacy-import assertions without visible windows or hardware.

## 0.8.23 — Calibration guidance — 2026-11-24

Unreadable HUD image files now give a clear PNG, JPEG or BMP error instead of a raw decoder exception.

Newly learned death-text templates retain their source image dimensions in local and portable setups. A different preview size shows a relearning notice. Clear/reset removes the size metadata, and Undo restores it. Existing setups without dimensions remain valid. This is advisory and does not change recognition thresholds or detect HUD-scale changes at the same resolution. Passed 236 offline Screen sync assertions, including import, export, mismatch guidance, clear and undo. No live game or physical output was used.

## 0.8.22 — HUD image calibration — 2026-11-24

Calibration can open a saved PNG, JPEG or BMP. It stops capture and output, freezes the image for learning, and cannot enable gameplay feedback from that image. A newer Stop cancels an import waiting on an earlier output stop. Files are released after decoding; saved setups retain templates and regions, not the screenshot.

Selected shield, spectator and death-text regions now show their outlines before learning. An invisible rendered-pixel regression failed before this fix and passed afterward. Tests also cover image pixels, invalid files, stopped capture, no output, learning readiness and cancellation. Passed: 228 Screen sync, 343 Studio, 21 audio and 17 legacy-import assertions using synthetic images and transports. File-dialog interaction and live gameplay remain unverified; no installed app was replaced.

## Additional recorded death check — 2026-11-24

A separate 1920×1080 recording was replayed through the production image reader and health/death-text filters at 10 samples per second, reusing the earlier health calibration and death-text template unchanged. One 35-second interval (350 frames) produced exactly one death event after health disappeared and three text matches exceeded 0.97. A separate 50-second respawn/buy-phase interval (500 frames) produced no death events, including 235 frames where positive health and a strong match to the old report were both present.

Mean image-read/filter time was 1.85 ms and 1.71 ms respectively, excluding decoding and capture. These are limited recorded examples, not a general accuracy guarantee or a live capture/device test. No thresholds changed; recordings and extracted frames remain private.

## Death-text resolution follow-up — 2026-11-24

A 1920-wide recorded frame was resampled to widths 1280, 1600, 2560 and 3840. Reusing its original death-text template scored 0.39–0.65, below the 0.90 confirmation threshold; relearning on each resized frame matched that frame at 1.00. This tests image resampling, not actual game rendering at those resolutions. It demonstrates that the small text template should be relearned after changing resolution or HUD scale. No recognition thresholds changed.

## 0.8.21 — Calibrated death text — 2026-11-24

The production image reader and temporal filter were replayed together on three private recording segments at 10 fps, using one learned KILLED YOU template. The 350-frame calibration segment produced one death at 56.6 seconds, and a separate 100-frame segment produced one at 117.0 seconds without retraining. A 450-frame negative segment with a lingering report produced zero deaths. These are limited recorded examples, not a general accuracy rate.

Mean image-reading and event-filter time was 1.7–2.5 ms per frame on the test PC, excluding video decoding and capture. The reader searches vertically for the calibrated text. A new marker must coincide with recent loss of established positive health; report presence alone is insufficient. The existing one-second HUD-loss cutoff remains unchanged.

Passed: 18 core suites (272 screen assertions), 343 native Studio assertions and 211 Screen sync assertions. Synthetic transport checks cover bounded death output, no automatic rearming, old-report suppression, later zero-health deduplication, calibration edits and undo. Portable setups use version 4; older setups leave death-text detection disabled. No live game capture, physical output or installed-app replacement was performed. Recordings and learned templates are not distributed.

## Recorded spectator follow-up — 2026-11-24

Replayed 956 frames from a separate recorded match using a spectator marker learned from another recording. There were no suspected or confirmed spectator matches in 715 sampled own-player frames before death and after respawn. Within a 111-frame spectator window, 85 frames matched the marker threshold and 75 met consecutive confirmation. This is limited cross-recording evidence, not a general accuracy rate.

The first spectator confirmation came about four seconds after health became unreadable. The existing one-second unreadable-health cutoff would already have disabled output, so spectator detection cannot supply an immediate death cue in this case. The death report also remained visible after respawn; its presence alone is insufficient. No thresholds, output cutoff or death inference were changed. Recordings and calibration remain private.

## 0.8.20 — Stroke settings display — 2026-11-24

An integration could set the stroke range to 20–75% while the device controls continued showing 0–100%. The offline native-app regression reproduced the mismatch. Stroke fields now follow the accepted profile configuration through one-way bindings; editing a field still requires Set stroke to apply it.

Passed: 335 native Studio assertions, 198 Screen sync, 21 audio and 17 legacy-import assertions; 18 core suites with 200 screen-detection assertions. Tests used invisible controls and simulated transports. No physical motion or normal app startup was used.

## 0.8.19 — Continuous healing baseline — 2026-11-24

A recorded healing sequence rose from 33 to 66 before falling to 28, but the filter reported only 5 health lost because none of the rising values persisted for three frames. The filter now accepts a trend of three increasing observations with small steps after an established positive baseline. Initial health and losses still require three identical readings; unknown frames break the trend. Isolated spikes, descending or alternating readings and large jumps do not establish a rising trend.

The regression failed before correction. Replaying recorded recognition results changed the affected loss to 38; events in three earlier clips remained identical, including zero events in a buy-phase clip. This is limited offline evidence, not universal match accuracy or game-authoritative damage. Private recordings and calibration are not included in the repository.

Passed: 18 core suites with 200 screen-detection assertions; 198 native Screen sync and 334 Studio assertions; Windows build with no warnings or errors; source privacy scan with no findings. No GUI, live capture or physical output was used.

Additional recorded badge check: an end-of-round kill remained below the match threshold with three previously learned appearances. Adding its stable appearance through the existing badge bank recovered that kill; a 556-frame replay produced two kill cues and three damage cues. A separate 241-frame buy-phase replay with the expanded bank produced zero events. The added template was learned from the positive clip itself, so this is calibration validation, not held-out accuracy. No threshold or matching algorithm changed, and no private badge images are shipped.

## 0.8.18 — Motion control fixes — 2026-11-24

Device summaries, tooltips and accessible controls now name the discovered actuator functions rather than assuming vibration.

### Stroke configuration discovery

Device discovery now includes the configured stroke endpoints so integrations can display the range used by subsequent thrusting commands. Both endpoints come from one immutable snapshot. Disconnected devices and devices without stroke control report null; the values are app configuration, not physical-position feedback.

Passed: 334 native Studio assertions, including default range, accepted changes, disconnection and replacement by a model without stroke control. Windows build completed without warnings or errors; source privacy scan found no configured markers. Tests used offline simulated devices.

### Position move status — 2026-11-24

Reproduced a position command reporting inactive output at level zero while still in flight. Position moves now participate in playback and channel activity, with an unknown speed rather than a fabricated zero. Confirmed cleanup restores level zero; failed cleanup keeps output unconfirmed while clearing the completed task.

The active-status regression failed before the fix. All 18 core suites passed afterward, including successful and rejected stop cleanup. Windows build: zero warnings and errors. Source privacy: no findings. Testing used simulated transport only, with no GUI or hardware access.

### Stop pending motion settings — 2026-11-24

A channel stop could leave a previously queued position move eligible to start. Reproduced this with a blocked simulated transport: the position command was sent after the stop request. Channel stops now invalidate pending device-wide settings, checked both after the playback queue and before transport submission. This does not undo commands already transmitted.

The regression failed before the change and passed afterward. All 18 core suites passed; the Windows build completed with no warnings or errors. Source privacy checks reported no findings. No physical devices or GUI were used.

### Integration session cleanup — 2026-11-24

Reproduced an old integration session sending stop commands to a replacement device connection at the same address. Session ownership now retains the original playback instance and channel; cleanup skips connections that have been replaced. A session that explicitly commands the replacement still stops it normally.

The new regression failed before the fix. All 18 core suites and 330 native Studio assertions passed afterward, using simulated connections with no visible windows or physical output. Source privacy checks passed with no findings. Hardware reconnection behavior remains unverified.

### Binary command diagnostics — 2026-11-24

A valid Solace Pro stroke packet with speed 13 and range 10–59 produced multiple activity-log lines because its payload contains raw control bytes. Command traces and transport errors now render numeric stroke fields; other non-printing command characters are escaped. Bluetooth payload bytes and reply matching are unchanged. Passed: 18 core suites, including the binary-payload regression, and a Windows x64 build with zero warnings. Source privacy checks found no configured private markers. No hardware or normal app startup was used.

## 0.8.17 — Lovense actuator functions — 2026-11-24

Adds capability-based output lanes for rotation, pumping, flexing, thrusting, suction and oscillation; motion settings cover reverse, depth, stroke range and position. Continuous outputs share the existing curve, source-ownership and stop machinery. Local integrations discover function names and native resolution and can use the new `/function` endpoint.

Offline checks cover all 43 catalog entries, model-specific command formats, firmware-gated Flexer channels, independent stops, attempting remaining stops after a failure, position zero versus stop, and withholding a revoked position move. Core tests passed: 18 suites including 164 screen-detection assertions. Native app tests passed: 325 Studio, 198 Screen sync, 21 audio and 17 legacy-import assertions. They use invisible controls and simulated transports; no live game, device output or normal app startup is used.

New actuator compatibility, motion-setting UI appearance and ARM64 execution remain physically unverified. The vendor protocol and SDK are interoperability references, not a guarantee for every firmware or future product. No vendor DLL, device identifiers, recordings or private calibration data are bundled.

## 0.8.16 — Digit boundary fallback — 2026-11-24

Bright pixels outside HUD digits can stretch their shared recognition bounds. Rejected readings now retry with separate vertical bounds for each digit. Existing successful health and shield readings, calibration masks, confidence thresholds and ambiguity checks are preserved.

A synthetic regression failed on the old reader and passed after the change. Tests cover recovery, blank/noisy crops, ambiguous shapes and unchanged learning. Passed locally: 17 core suites including 164 screen-detection assertions; 198 Screen sync, 315 Studio, 21 audio and 17 legacy-import assertions. Source privacy checks found no configured private markers.

The private prototype recovered 13 additional health readings across three recorded segments without changing their events. The final source recovered the same 11 additional readings in one combat segment; spectator suppression still withheld teammate events. A separate 400-frame segment kept exactly the prior health, shield and event results. This limited evidence does not establish general accuracy. Recorded media and calibration are not distributed. Live game performance and ARM64 execution remain unverified.

## 0.8.15 — Calibrated spectator suppression — 2026-11-24

Optional spectator-marker calibration adds a separate image region and template. Three fresh matches at 85% or higher stop active Screen sync output; suspected frames do not start new cues. Output never resumes automatically. Enabling checks the marker both before and after waiting for other output to stop. Clear, region changes, reset, Undo and portable calibration handle the new template. Portable exports now use version 3; versions 1 and 2 remain readable without this feature.

A private recorded-frame probe using an English Switch player marker confirmed spectating before the tested teammate feedback events. A separate normal-play interval of 1,680 frames had no matches at the candidate threshold. Later matches were visually checked as genuine spectator frames after death. This is limited calibration evidence, not universal language, layout, or spectator recognition. Templates and recordings are not distributed. Live game behavior and visual layout remain unverified. A subsequent 350-frame replay using the production matcher, default spectator region, and a serialized/restored calibration confirmed spectating at 13.8 seconds. Both prior player-damage events remained eligible, while three teammate kills and one teammate-damage event were ineligible after the latched stop. This replay checks recognition and eligibility; the separate simulated-device tests check actual stop routing.

Passed: 17 core suites, including 158 screen-detection assertions; 198 Screen sync, 315 Studio, 21 audio and 17 legacy-import assertions. New integration checks cover stopping active simulated output, withholding a suspected spectator damage cue, blocking enable both immediately and during an asynchronous handoff, no automatic resume, template copying, clearing, Undo, and preserving a failed-stop warning until retry succeeds. No visible UI, game capture or hardware was used.

## 0.8.14 — Recorded HUD recognition — 2026-11-24

Calibration retains up to eight learned kill badge appearances for different skins and animations. Duplicate learning does not replace a template or consume Undo. Clear, Undo, region changes, local saves and portable setup files handle the complete set. New portable files use version 2; version 1 imports remain supported.

Rapid kill animations can restart without the previous 300 ms clear interval. The filter now accepts a deep visibility drop followed by two rebuilding frames and three strong matches within 1.25 seconds. It retains the minimum interval between events and rejects a direct flicker back to the held badge. Reset and unreadable health cancel the pending restart.

A calibrated recorded sequence improved from two to three visible kill detections. A separate 1,958-frame scan retained its single detection without additional kills. These checks include spectated HUDs and prove only limited visual matching, not identification of the player's own kills. Skin coverage, live timing, spectator handling and ARM64 execution remain unverified. No recordings, extracted images or personal calibration files are distributed.

Passed locally: 17 core suites, including 147 screen-detection assertions; 184 Screen sync, 315 Studio, 21 audio and 17 legacy-import assertions. Tests use synthetic frames and simulated transports. The following two recognition corrections are also included in this release.

### Dim shield digits

Recorded depleted-armor digits fell below the bright-text threshold. A synthetic reproduction failed before correction. Shield reading now tries local luminance contrast when normal matching fails; it keeps the existing digit-confidence and ambiguity thresholds and rejects uniform or weak-contrast crops. Shield learning can use the same fallback when the normal mask cannot separate the supplied number. Health and badge reading are unchanged.

With two calibration samples and a tighter shield region, the reviewed 359-frame segment produced 143 zero-shield readings instead of leaving those frames unknown. In visually checked stable intervals, 145/145 full-shield samples and 118/125 zero-shield samples were correct; the remaining seven were unknown. The production replay matched the experiment frame for frame. A separate 400-frame segment read the visible 34 shield and added no events. These limited checks do not establish general precision/recall. Brief unreadable shield transitions still reset the event baseline, so improved readability does not recover every shield hit. Death and kill recognition remain separate unfinished validation work.

The core suite passed 17 suites, including 84 screen-detection assertions. Native tests passed 174 Screen sync, 315 Studio, 21 audio and 17 legacy-import assertions. New checks cover dim zero, flat crops, low contrast, learning, and keeping the fallback out of health recognition. Recordings and calibration remain private.

### Recorded HUD color interference

Private 1080p SDR recordings exposed teal armor outlines and ability effects entering the number mask. Digit learning and health/shield reading now exclude the saturated teal category; badge matching retains it. Confidence thresholds are unchanged.

In one calibrated 35.9-second combat segment, readable health samples increased from 281/359 to 311/359 at 10 Hz, with the same four damage events and no events during the visible healing ramp. A separate 40-second clip segment had identical health readings with both masks (259/400 readable), using the same calibration and templates. These are readability comparisons, not measured precision/recall. Shield recognition remained poor and a disappearing death HUD did not produce a death event. Kill-badge recognition was not calibrated in this comparison.

Passed: 17 core suites, including 78 screen-detection checks; 174 Screen sync, 315 Studio, 21 audio and 17 legacy-import assertions. Synthetic regression checks cover learning/reading white digits beside a teal outline and preserving teal badge extraction. No recordings, extracted frames or personal calibration data are included in the repository. Recorded-file processing does not validate live capture timing or physical output.

## 0.8.13 — Pending capture updates — 2026-11-23

A recognition update waiting for a focus-loss motor stop could restore its old HUD frame and reading after **Stop capture**, overwriting the newer status. This was reproduced before correction. Pending recognition updates now check capture and output revisions after device stops; outdated updates leave the newer state alone.

Passed locally: 174 Screen sync, 315 Studio, 21 audio and 17 legacy-import assertions using synthetic frames, invisible controls and simulated transports. The regression verifies that the frame stays cleared, the reading stays empty and the stopped status survives the late update. No live game, physical device or ARM64 execution was tested.

## 0.8.12 — Enable Screen sync shortcut — 2026-11-23

Settings includes an optional shortcut to enable Screen sync from the captured window. It starts disabled and suggests Ctrl + Shift + F9. A live preview and readable positive health are required. It does not start capture or send a test pulse. Repeated presses while enabled or enabling do not queue another start. Stop all remains a separate action; conflicting bindings preserve the working shortcut.

The new handoff tests reproduced an existing bug: a pending enable could adopt a reconnected device while another device was stopping. Both the button and shortcut now retain the original playback instances and connection generations across source and motor stops. A reconnect requires a fresh explicit enable.

Passed locally: 17 core suites, including 35 shortcut-controller checks; 315 Studio, 171 screen, 21 audio and 17 legacy-import assertions. Coverage includes saved preferences, registration conflicts, independent action IDs, stale messages, focus loss, shortcut changes, Stop all cancellation, death/re-enable and reconnects during both handoff stages. Tests used fake registrars, invisible controls, synthetic frames and simulated devices. Actual shortcut delivery during gameplay, visual layout, recognition accuracy and ARM64 execution remain unverified.

## 0.8.11 — Capture stop status — 2026-11-23

A failed device stop could leave the output level unknown while **Stop capture** displayed **output off**. Capture-error and focus-loss messages could also replace the stop warning. Screen status now preserves pending or unconfirmed stops across capture and calibration messages. A successful retry clears the warning; the change does not alter motor commands or detection thresholds.

The failure was reproduced before correction. Passed: 300 Studio, 142 screen, 21 audio and 17 legacy-import assertions. New checks cover immediate and delayed stops, capture errors, focus loss and retry recovery using invisible controls, synthetic frames and fake transport. No game window, real capture or physical device was used. Live-game recognition and ARM64 execution remain unverified.

## 0.8.10 — Portable HUD setups — 2026-11-23

**Calibration → More** can save and open `.hud.json` setups containing recognition regions, learned digit/badge templates and the shield-reading choice. Import preserves motor assignments and event strengths, stops output, and creates one calibration Undo action. It never starts capture or output; **Save calibration** keeps the loaded setup across launches.

Passed: 300 Studio, 129 screen, 21 audio and 17 legacy-import assertions, plus eight Python release-tool tests. New checks cover file round trips, invalid or oversized input, locked-file replacement and cleanup, deep copies, Undo, cancellation during a delayed stop and preservation of an unconfirmed-stop warning. The release scan rejects exported setups and their temporary files.

These checks used invisible controls, synthetic frames and fake transport. File-picker interaction and live layout were not exercised under the no-GUI restriction. Real-game recognition, Bluetooth timing and ARM64 execution remain unverified.

## 0.8.9 — Overlapping combat cues — 2026-11-23

A synthetic HUD confirmed damage and a kill in the same frame, but the shared cooldown suppressed the kill even when it used a different motor. Cooldowns now belong to each motor. Events assigned to separate motors can play together; on shared motors, death takes priority over kills, and kills over damage. Lower-priority events cannot cut off a kill's second pulse. Repeated cues remain rate-limited and are skipped rather than queued for later playback.

Event history now distinguishes preview, muted events, cooldowns, queued motor counts, cancellation and source/command failures. A queued cue is a playback request, not confirmation of physical delivery.

Passed: 300 Studio, 112 screen, 21 audio and 17 legacy-import assertions. The new overlap regression failed before correction. Checks cover separate and shared routing, same-frame recognition, priority during active playback and history outcomes. Existing stop, delayed-command, persistence and death checks also pass. Testing used invisible controls, synthetic frames and fake transport; real game recognition, live layout, Bluetooth timing and ARM64 execution remain unverified.

## 0.8.8 — Per-motor screen events — 2026-11-23

Each Screen sync motor now has independent Damage, Kill and Death choices alongside its existing maximum. Changing feedback still disarms output. Strengths, selections, maximums and event assignments save separately from calibration and reload by motor identity without enabling capture or output.

Passed: 300 Studio, 105 screen, 21 audio and 17 legacy-import assertions, plus seven Python release-tool tests. New checks route each event to a different channel of a simulated dual-motor device, cancel a blocked cue when its assignment changes, reject an empty route, reload settings after discovery order changes, preserve a locked settings file and reject invalid saved data. Release privacy checks reject the new local settings file.

A synthetic rapid-hit sequence reproduced recognized death being suppressed by the previous damage cue's cooldown. Death now takes priority over that cooldown and still stops and disarms afterward. Muted events and events assigned to no motors leave the cooldown available for the next cue. Regression checks cover both cases.

Tests used invisible controls, synthetic pixels and fake transports. No game window, actual capture, real shortcut registration or physical device was accessed. Live layout, match recognition, Bluetooth timing and ARM64 execution remain unverified.

## 0.8.7 — Presets on short cycles — 2026-11-23

All five built-in presets rejected the allowed 200 ms cycle because their points were too close together. Applying a preset now increases the shared cycle only when its spacing requires it, preserving longer cycles. The curve and length change form one Undo/Redo action, and the controls show the applied preset and length.

The failure was reproduced with an invisible editor and no devices. Regression checks cover every preset, valid spacing, unchanged longer cycles, Undo/Redo, matching controls and no motor commands. This changes preset authoring; hardware timing and real-game recognition are unchanged.

## 0.8.6 — Settings and integration requests — 2026-11-23

Two more reconnection bugs were reproduced before correction. Apply settings could send its remaining commands to a replacement connection. A multi-motor API frame could start a reconnected motor while waiting for an earlier motor to stop; a cancelled pattern admission could also report acceptance after its connection changed.

Settings now retain the original connection and chosen values. Motor, frame and pattern API requests check their captured connection and session before and after awaited work, returning 409 if ownership changes. Earlier commands in a partial frame can already have applied; clients must refresh state before preparing another request.

Offline regressions exercise late settings replies, multi-motor API handoffs, rejected pattern admission, cancellation, accepted-output leases and fresh requests after reconnect. An injectable device-link contract also tests power-off acknowledgement and cleanup without Bluetooth. These checks do not establish real reconnect performance, physical power-off, gameplay recognition or ARM64 execution.

## 0.8.5 — Pending device controls — 2026-11-23

Reconnection races were reproduced in the advanced device controls. A pending power-off could release a replacement connection; Start, Test 2s, Play pattern and Play hardware preset could use a new connection after waiting for other output to stop. Each request now retains its original connection before that wait. Power-off also checks ownership before sending its command and before releasing the connection.

Regression checks cover replacement during source handoff and during the device stop, failed power-off cleanup, a concurrent Stop all, and fresh explicit starts after reconnection. Tests use invisible controls and fake motor transports. Power-off failure is simulated with an absent Bluetooth link; this verifies cleanup, not physical power-off or a real Bluetooth reconnect. Live gameplay, background shortcut delivery and ARM64 execution remain unverified.

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
