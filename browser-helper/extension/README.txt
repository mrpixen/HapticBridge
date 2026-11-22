HapticBridge Clock Helper 0.1.0

Fixes one legacy MGP page layout that reads an old video element's clock
instead of the active player's clock. It does not repair pairing, obtain
video tracks, change playback, or control devices.

Edge
1. Extract this ZIP into a folder you will keep.
2. Open edge://extensions and turn on Developer mode.
3. Choose Load unpacked and select the extracted folder.

Firefox 128 or later (temporary local installation)
1. Extract this ZIP.
2. Open about:debugging#/runtime/this-firefox.
3. Choose Load Temporary Add-on and select manifest.json.
Firefox removes temporary add-ons when it restarts. Normal permanent
Firefox distribution requires Mozilla signing; this package is unsigned.

Use
1. Open the player page, then click the helper's toolbar icon.
2. If it says Ready, choose Enable for this tab.
3. Pair normally and enable output separately in HapticBridge.
4. Choose Disable for this tab to restore the original clock.

The helper stays off on other tabs. Reloading, navigation, or replacing
the supported player turns it off; enable again only when you want it.
Turn it off before removing/reloading the extension, or reload the page
afterward to remove its page-side changes.

It asks only for activeTab and scripting. No accounts, network requests,
saved settings, browsing logs, automatic injection or device commands.
It changes only the old element's currentTime getter. Its original seek
setter is preserved, and unrelated videos are untouched. If the layout
is ambiguous or unsupported, it makes no change.

Validation uses neutral offline fixtures. This does not certify that
every site, browser build, pairing service, or physical device works.
