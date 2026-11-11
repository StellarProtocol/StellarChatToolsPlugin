"""ChatTools Settings header glyph: the launcher icon's two speech bubbles (Resources/chattools-icon.png, 128 px), REDRAWN
at the size the Settings ToolHeader shows it (24 px on a 40 px accent tile) — pre-drawn + pixel-hinted, never the 128
master scaled down (its ~6 px strokes become ~1.1 px soft grey lines at 24). Every straight stroke is 2 whole pixels on
the pixel grid; only the rounded corners and the tail diagonal are anti-aliased (4x supersample + box filter).

Usage (conda python): python tools/make-settings-icon.py Resources
"""
import sys
from PIL import Image, ImageDraw

SIZE, SS, STROKE = 24, 4, 2

def px(v):
    return v * SS

def outline_round_rect(d, box, r, stroke):
    """A rounded-rect outline: outer filled, inner punched out (both radius-matched)."""
    x0, y0, x1, y1 = box
    d.rounded_rectangle([px(x0), px(y0), px(x1) - 1, px(y1) - 1], radius=px(r), fill=255)
    d.rounded_rectangle([px(x0 + stroke), px(y0 + stroke), px(x1 - stroke) - 1, px(y1 - stroke) - 1],
                        radius=px(max(0, r - stroke)), fill=0)

def draw(out):
    img = Image.new("L", (SIZE * SS, SIZE * SS), 0)
    d = ImageDraw.Draw(img)
    # Back bubble (top-right): only its top edge, right edge and a short left stub show; the front bubble covers the
    # rest, separated by a 2 px gap (like the 128 px art).
    outline_round_rect(d, (8, 3, 22, 15), 3, STROKE)
    d.rectangle([px(6), px(6), px(20) - 1, px(17) - 1], fill=0)          # clear what sits behind the front bubble + gap
    # Front bubble.
    outline_round_rect(d, (2, 8, 18, 18), 3, STROKE)
    # Tail: a solid wedge hanging off the bottom-left edge.
    d.polygon([(px(4), px(17)), (px(4), px(21.5)), (px(8.5), px(17))], fill=255)
    # Three 2x2 dots on whole pixels, centred in the bubble's 12 px interior.
    for x in (6, 9, 12):
        d.rectangle([px(x), px(12), px(x + 2) - 1, px(14) - 1], fill=255)
    alpha = img.resize((SIZE, SIZE), Image.BOX)
    rgba = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 0))
    rgba.putalpha(alpha)
    rgba.save(out)

if __name__ == "__main__":
    draw(f"{sys.argv[1]}/chattools-settings-24.png")
    print("ok")
