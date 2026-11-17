"""Rebuild bundled static Outfit fonts (requires FontTools 4.68.0)."""
from pathlib import Path
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

directory = Path(__file__).resolve().parents[1] / "source" / "fonts"
source = TTFont(directory / "original" / "Outfit-Variable.ttf")
for name, weight in [("Regular", 400), ("Medium", 500), ("SemiBold", 600), ("Bold", 700)]:
    font = instantiateVariableFont(source, {"wght": weight}, inplace=False, updateFontNames=True)
    font.save(directory / f"Outfit-{name}.ttf")
    print(f"Built Outfit {name}")
