# Setzt die README-Screenshots der Mac-Version zusammen: Seitenleiste und Seite kommen getrennt aus dem Debug-Build
# (FG_SHOT_DIR), weil macOS 26 sie beim Rendern außerhalb des Bildschirms im Gesamtbild auslässt.
# Aufruf: python3 mac-screenshots.py <Ordner mit Bausteinen> <Zielordner>
import sys
from PIL import Image

W, H, SIDE = 2360, 1560, 476
parts, target = sys.argv[1], sys.argv[2]
for name, out, dark in [("chat", "mac-chat", False), ("settings", "mac-einstellungen", False), ("plugins", "mac-plugins", False),
                        ("automation", "mac-automatik", False), ("compare", "mac-vergleich", False), ("dark-chat", "mac-chat-dunkel", True)]:
    canvas = Image.new("RGBA", (W, H), (30, 30, 30, 255) if dark else (255, 255, 255, 255))
    canvas.paste((44, 44, 46, 255) if dark else (240, 240, 242, 255), (0, 0, SIDE, H))
    canvas.paste((60, 60, 62, 255) if dark else (214, 214, 216, 255), (SIDE - 2, 0, SIDE, H))
    side = Image.open(f"{parts}/{name}-sidebar.png").convert("RGBA")
    page = Image.open(f"{parts}/{name}-page.png").convert("RGBA").crop((0, 0, W - SIDE, H))
    canvas.alpha_composite(side.crop((0, 0, SIDE, H)), (0, 0))
    canvas.alpha_composite(page, (SIDE, 0))
    canvas.convert("RGB").resize((W // 2, H // 2), Image.LANCZOS).save(f"{target}/{out}.png", optimize=True)
