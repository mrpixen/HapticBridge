# Outfit fonts

The original variable font is retained in `original/Outfit-Variable.ttf`, downloaded unchanged from [Google Fonts](https://github.com/google/fonts/blob/main/ofl/outfit/Outfit%5Bwght%5D.ttf). Its SHA-256 is `fc7287273e66929776e2ba54f144fe699080bec29f61bf649d70d871468aeade`. Copyright 2021 The Outfit Project Authors. The SIL Open Font License is retained in `../vendor-notices/Outfit-OFL.txt` and embedded in the application.

Regular (400), Medium (500), SemiBold (600), and Bold (700) static instances were generated with FontTools 4.68.0 so WPF can resolve their weights consistently. To regenerate after installing that development dependency, run `python tools/build_fonts.py` from the repository root. Normal app builds use the checked-in static fonts and do not need Python or FontTools.

Only the four static fonts are embedded into the executable; the original remains in the source distribution. `UiFonts.Body` uses an explicit WPF assembly pack URI. A WPF glyph-typeface probe confirmed all four requested weights resolve to these embedded resources instead of a system font.
