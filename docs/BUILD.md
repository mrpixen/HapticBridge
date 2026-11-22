# Building and checking a release

Use Windows and the .NET 10 SDK. Package restore requires internet access; the published app includes its runtime.

```powershell
./test.ps1
./build.ps1 -Locked
```

The Python example and privacy checker have offline tests: `python -m unittest discover -s tests -p "test_*.py"`.

The default output is `dist/win-x64/HapticBridge.exe`. For Windows on ARM, run `./build.ps1 -Runtime win-arm64 -Locked`. The project pins its NuGet dependencies through `source/packages.lock.json`; omit `-Locked` only when intentionally updating that lock file. `-DotNet` accepts an alternate SDK executable path.

The publish script checks that its output directory contains exactly one executable and prints its SHA-256 hash. XAML, the device catalog, help, notices and the six distributable browser-helper files are embedded. The helper's tests and research files are excluded. Debug symbols are omitted and source paths are mapped to a generic build path. Native runtime files may extract into the operating system's runtime cache during launch.

**Settings → Browser helper → Save helper…** exports the bundled helper ZIP. The app remains one executable; using the optional browser workaround requires loading that extension separately. The export includes setup instructions and does not install anything in a browser.

Before publishing, run the privacy checker against the source and each release file:

```powershell
python tools/privacy_check.py --artifact dist/win-x64/HapticBridge.exe
```

For a private local audit, `--private-terms-file` accepts a UTF-8 file containing one sensitive value per line. Keep that file outside this repository. The checker reports only categories and counts. It does not print matches, file contents or private values. A raw binary scan cannot inspect compressed bundle contents; also review the project's embedded-resource list, generated managed assemblies and the files selected for the release.

Run the executable from a fresh directory before releasing. Verify scanning, manual stop, local API lease expiry, audio sync and any available live external session. Automated protocol tests do not establish compatibility with every device or live service. Hardware output tests require a connected device; ordinary unit tests do not actuate hardware.

Publish source through Git and attach the single executable to a GitHub release. Do not copy the application's local data directory, runtime logs, diagnostics, pairing data, screenshots or research downloads into either artifact.
