# Local data and connections

HapticBridge has no account system or application telemetry service. Device discovery and the native integration API run on this computer. The API binds to IPv4 loopback and authenticates every request with a randomly generated local token. It does not listen on the local network and rejects browser-origin requests.

The app stores its settings, connection file, diagnostic status, activity log and saved patterns below `%LOCALAPPDATA%\HapticBridge`. These files can contain Bluetooth device identifiers, model names, firmware details and application activity. Mixer presets can contain motor identifiers. The API connection file contains a control credential. Treat this directory and copied API connection JSON as private; a local process that can read the token can control the API while integrations are enabled.

Sound sync analyzes the current Windows output in memory. It does not save an audio recording. Changing the Windows output device may require restarting sound sync.

FeelConnect support communicates with `api.feel-app.com` and `fec.feelme.com` over the internet to pair and receive control messages. The service receives pairing credentials, online/offline presence and the display names of devices supplied by the app; its own privacy terms apply. HapticBridge does not provide a private relay or an account for that service. Pairing links and QR codes are credentials: do not post them in bug reports. Pairing tokens and session details stay in memory. The integration follows control messages, rather than downloading or analyzing the video itself. A disconnected or expired session does not automatically reconnect or resume output.

Release builds contain program code, generic capability metadata, documentation and dependency notices. They do not include a developer's connection token, paired-device inventory, saved presets, session logs or browsing material. A new installation generates its own local API token. Downloading the executable does not copy another installation's state.

Before sharing a diagnostic file or preset, inspect it and remove device identifiers and credentials. Do not upload the local data folder to an issue or public repository. To reset local state, close the app and remove its local data folder; this also removes saved patterns and invalidates existing API clients' tokens.
