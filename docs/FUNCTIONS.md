# Device functions

HapticBridge controls supported Lovense devices directly over Bluetooth. No vendor account or mobile app is required. Capabilities depend on the identified model and firmware; recognition alone does not verify physical operation.

## Controls

Every continuous output gets its own lane in Controls and its own route in Sound, Screen sync and Video sync. Use the same output in the local API by discovering `/devices`. Channels are stable identifiers, not array positions:

| Channel | Function | Control range |
|---|---|---|
| 0 | Single or combined vibration | 0–20 |
| 1–3 | Independent vibration motors | 0–20 |
| 4 | Rotation | 0–20 |
| 5 | Air pump | 0–20 mapped to native 0–3 |
| 6 | Flexing | 0–20 |
| 7 | Thrusting | 0–20 |
| 8 | Suction | 0–20 |
| 9 | Oscillation | 0–20 |

Zero stops a continuous output. It does not reset position or depth. Stop all attempts every actuator even if another stop fails; failures remain unconfirmed. Device presets are offered for vibration-only profiles; custom patterns work with all continuous outputs.

Motion settings stop current playback before changing depth, stroke range or rotation direction. Position is a bounded point move, followed by a motion stop. Stroke range is retained for the current connection and used on the next thrusting start. These settings are also available through [`POST /function`](API.md#motion-settings).

## Model differences

- XMachine and Mini XMachine's vibration command controls thrusting speed; there is one output, not two independent motors.
- Tenera, Tenera 2 and Fizz expose one suction output rather than duplicate aliases.
- Flexer firmware 3+ exposes two vibration actuators and flexing. Older or unidentified firmware retains combined vibration only.
- Lapis uses multi-actuator commands that leave unselected channels unchanged.
- Solace and Solace Pro use different stop packets. Position zero is never substituted for a stop.
- Solace Pro stroke packets contain binary speed and range values. No vendor DLL is shipped or required.

## Verification and sources

The command encoders, ranges, stopping, revoked-source handling and local API are tested with simulated transports. New actuator functions have not been physically verified on each model or firmware. Simulated acknowledgements cannot establish real device support. Failed commands are reported; the app does not silently substitute another function.

Protocol implementation was checked against [Lovense's Windows BLE SDK](https://developer.lovense.com/docs/game-engine-plugins/windows_ble), its v1.2 command implementation and v1.6 capability catalog, [Lovense's Standard API](https://developer.lovense.com/docs/standard-solutions/standard-api), and the [Buttplug Lovense implementation](https://github.com/buttplugio/buttplug/tree/master/crates/buttplug_server/src/device/protocol_impl/lovense). LAN API names are not assumed to be Bluetooth commands. The vendor SDK is used as an interoperability reference, not bundled code.
