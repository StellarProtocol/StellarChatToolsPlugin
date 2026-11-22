"""ChatTools share-link glyphs (approved prototype chattools-links.html): the map PIN (position links, the composer's
share button, the attachment strip) and the MIC (voice messages). Drawn WHITE on transparent and tinted live by
ImageElement.Tint (info blue #9cc8ff, a darker blue on the Light theme, recessed grey while disabled) — and PRE-DRAWN at
every size they are shown (12 px inline, 14 px in the 28 px composer button), never one master scaled down.
8x supersample + box filter; the pin's hole is punched out (transparent), so the tint never paints a fake background.

Usage (conda python): python tools/make-link-icons.py Assets
"""
import sys
from PIL import Image, ImageDraw

SS = 8


def finish(img, size, out):
    alpha = img.resize((size, size), Image.BOX)
    rgba = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    rgba.putalpha(alpha)
    rgba.save(out)


def pin(size, out):
    """Teardrop map pin: a round head over a point at the bottom centre, a round hole in the head."""
    s = size * SS
    img = Image.new("L", (s, s), 0)
    d = ImageDraw.Draw(img)
    cx = s / 2
    r = s * 0.34                      # head radius
    cy = r + s * 0.04                 # head centre, just under the top edge
    tip = s - s * 0.03                # the point, just above the bottom edge
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=255)
    # The two tangents from the tip to the head circle → a filled wedge that blends into the head.
    import math
    dist = tip - cy
    a = math.asin(min(1.0, r / dist))
    tx = r * math.cos(a)
    ty = r * math.sin(a)
    d.polygon([(cx - tx, cy + ty), (cx + tx, cy + ty), (cx, tip)], fill=255)
    h = r * 0.42                      # the hole
    d.ellipse([cx - h, cy - h, cx + h, cy + h], fill=0)
    finish(img, size, out)


def mic(size, out):
    """Microphone: a rounded capsule, a U-shaped holder under it and a short stem."""
    s = size * SS
    img = Image.new("L", (s, s), 0)
    d = ImageDraw.Draw(img)
    u = s / 12.0                      # 1 unit of the prototype's 12-unit viewBox (= 1 px at 12)
    d.rounded_rectangle([4 * u, 0, 8 * u - 1, 7 * u - 1], radius=2 * u, fill=255)    # capsule, 4 px wide
    # Holder: the lower half of a 1 px ring round the capsule (centre 6,5), drawn as outer disc minus inner disc and
    # kept below the centre line — symmetric on the pixel grid (PIL's arc() isn't).
    ring = Image.new("L", (s, s), 0)
    rd = ImageDraw.Draw(ring)
    cx, cy, ro, ri = 6 * u, 5 * u, 4 * u, 3 * u
    rd.ellipse([cx - ro, cy - ro, cx + ro - 1, cy + ro - 1], fill=255)
    rd.ellipse([cx - ri, cy - ri, cx + ri - 1, cy + ri - 1], fill=0)
    rd.rectangle([0, 0, s, cy], fill=0)
    img.paste(255, (0, 0), ring)
    d.rectangle([5 * u, cy + ri, 7 * u - 1, 11 * u - 1], fill=255)                   # stem, 2 px (centred on the grid)
    d.rectangle([4 * u, 11 * u, 8 * u - 1, 12 * u - 1], fill=255)                    # foot, 1 px
    finish(img, size, out)


if __name__ == "__main__":
    folder = sys.argv[1]
    for px in (12, 14):
        pin(px, f"{folder}/link_pin_{px}.png")
    mic(12, f"{folder}/link_mic_12.png")
    print("ok")
