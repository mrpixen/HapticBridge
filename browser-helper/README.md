# HapticBridge Clock Helper

Optional compatibility helper for a legacy page that reads a stale video clock. Click its toolbar icon, then **Enable for this tab**. Pairing and device output remain separate actions in HapticBridge.

The helper requires the exact supported duplicate-player layout. It changes only the old element's `currentTime` getter, preserves its seek setter, and restores the property when disabled. Navigation or player replacement turns it off. It does not fetch tracks, change playback, send commands, or fix missing pairing messages.

## Install locally

- **Edge:** extract the ZIP, open `edge://extensions`, enable Developer mode, choose **Load unpacked**, and select the `extension` folder (or the ZIP's extracted folder).
- **Firefox 128+:** open `about:debugging#/runtime/this-firefox`, choose **Load Temporary Add-on**, and select `extension/manifest.json`. This lasts until Firefox restarts. The supplied ZIP is unsigned; permanent release/beta installation requires Mozilla signing.

Before removing or reloading the extension, disable it on the page or reload that page afterward. Removing an extension does not reliably clean up code already running in the page.

## Build and test

No dependencies or bundler are needed. From this directory:

```powershell
npm run check
powershell -NoProfile -File package.ps1
```

The package contains six explicit files and is written to `dist/HapticBridge-Browser-Helper.zip`. `extension/` is also the unpacked build. Public fixtures use only original code and generic video elements, with no vendor script, captured page or media.

Serve this directory locally and open `tests/browser-fixture.html` to exercise real browser media properties without installing the extension. It reports each check on the page. On 2026-11-22, all nine browser cases (45 assertions), 14 independent adversarial cases and the six-file package check passed. Browser extension installation and live end-to-end behavior remain unverified.

## Permissions and limits

Only `activeTab` and `scripting` are requested. There are no host permissions, background scripts, automatic content scripts, network calls, storage, browsing logs or accounts. The page's MAIN world can detect or interfere with the helper; it receives no extension privileges or private data. Restricted browser pages and unsupported layouts are rejected.

The popup performs a read-only compatibility check when explicitly opened. Enabling requires its button. Nothing is automatically enabled on a new page or player. This is a narrow compatibility adapter, not a claim of complete website or hardware support.

Implementation references: [MDN execution worlds](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/scripting/ExecutionWorld), [Chromium scripting](https://developer.chrome.com/docs/extensions/reference/api/scripting), [Edge local installation](https://learn.microsoft.com/microsoft-edge/extensions-chromium/getting-started/extension-sideloading), [Firefox temporary installation](https://extensionworkshop.com/documentation/develop/temporary-installation-in-firefox/), and [Firefox signing](https://extensionworkshop.com/documentation/publish/signing-and-distribution-overview/).
