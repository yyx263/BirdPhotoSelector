"""Regenerate the original feather app icon (developer tool; Pillow required)."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parent.parent
image = Image.new("RGBA", (512, 512))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((0, 0, 511, 511), radius=112, fill="#182922")
draw.ellipse((88, 62, 419, 394), fill="#9DDFC4")
draw.polygon([(86, 427), (154, 282), (305, 209), (227, 365)], fill="#9DDFC4")
draw.line([(106, 408), (334, 165)], fill="#183A2D", width=23)
draw.line([(199, 306), (191, 202)], fill="#183A2D", width=16)
draw.line([(257, 245), (356, 247)], fill="#183A2D", width=16)
draw.line([(296, 203), (292, 129)], fill="#183A2D", width=14)
assets = root / "Assets"
assets.mkdir(exist_ok=True)
image.save(assets / "App.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
