# Third-party notices

HapticBridge's original code is provided under the MIT license in `LICENSE`. Dependency licenses apply to their respective components. Open **Help** in the app to read the notices bundled with the executable.

## NAudio

The app uses NAudio.Core and NAudio.Wasapi 2.2.1 for Windows audio capture. Project: https://github.com/naudio/NAudio

Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## .NET and Windows Desktop runtime

Self-contained releases bundle Microsoft's .NET runtime and Windows Desktop components. Their upstream license texts and third-party notices are retained under `source/vendor-notices` and embedded in the app. These cover the runtime's own dependencies and must remain with redistributed releases. Projects: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf

## Windows SDK projections and C#/WinRT

Windows Bluetooth access bundles `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll` from Microsoft.Windows.SDK.NET.Ref 10.0.19041.57. The package's [Microsoft Windows SDK license](https://aka.ms/WinSDKLicenseURL) is retained unchanged as `source/vendor-notices/Windows-SDK-LICENSE.rtf`. The C#/WinRT runtime's upstream MIT license is retained unchanged as `source/vendor-notices/CsWinRT-LICENSE.txt`. Both are embedded and available from Help. C#/WinRT project: https://github.com/microsoft/CsWinRT

## ZXing.Net

QR decoding uses ZXing.Net 0.16.11, a .NET port of ZXing maintained by Michael Jahn. The package declares Apache-2.0 licensing. The unchanged upstream `COPYING` text is retained as `source/vendor-notices/ZXing-Net-Apache-2.0.txt` and embedded in the app. Project: https://github.com/micjahn/ZXing.Net

## Outfit font

The bundled Outfit font is Copyright 2021 The Outfit Project Authors and is licensed under the SIL Open Font License 1.1. Its unchanged license and copyright notice are retained as `source/vendor-notices/Outfit-OFL.txt` and embedded in the app. Project: https://github.com/Outfitio/Outfit-Fonts

## Protocol and product names

Lovense and FeelConnect are names of their respective products and owners. HapticBridge is an independent integration and is not their official client. The source contains a generic device capability catalog and an independently implemented protocol client; it does not redistribute the FeelConnect application, decompiled application code, videos or third-party site assets. Device names in the catalog describe compatibility and do not identify a developer's device inventory.
