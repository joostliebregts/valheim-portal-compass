import sys
# Draws package/icon.png (256x256): python make-icon.py package/icon.png  (needs Pillow)
from PIL import Image, ImageDraw

# Same flat look as Aim Stamina's icon: charcoal ground, light grey strokes, one amber accent.
S = 4                      # draw at 4x, downsample for smooth edges
BG, LIGHT, AMBER, DIM = (30, 35, 32), (230, 233, 228), (255, 201, 61), (64, 72, 67)
im = Image.new("RGB", (256 * S, 256 * S), BG)
d = ImageDraw.Draw(im)
s = lambda *v: [x * S for x in v]

# Portal: an arch (rounded top) in light grey, a faint inner glow
x0, x1, top, bottom, w = 30, 120, 44, 208, 11
r = (x1 - x0) / 2
d.rounded_rectangle(s(x0, top, x1, bottom), radius=(r - 1) * S, outline=LIGHT, width=w * S, corners=(True, True, False, False))
d.rounded_rectangle(s(x0 + w + 6, top + w + 6, x1 - w - 6, bottom - w), radius=(r - w - 7) * S, fill=(44, 52, 47), corners=(True, True, False, False))
d.rectangle(s(x0 - 8, bottom - w, x1 + 8, bottom), fill=LIGHT)    # stone sill

# The picker: three destinations, the chosen one in amber
lx = 140
d.rounded_rectangle(s(lx, 84, 222, 110), radius=7 * S, fill=AMBER)
d.rounded_rectangle(s(lx, 126, 210, 138), radius=6 * S, fill=LIGHT)
d.rounded_rectangle(s(lx, 154, 196, 166), radius=6 * S, fill=DIM)
d.rounded_rectangle(s(lx, 182, 204, 194), radius=6 * S, fill=DIM)

# Arrow from the chosen row into the portal
d.polygon(s(127, 97, 137, 89, 137, 105), fill=AMBER)

im.resize((256, 256), Image.LANCZOS).save(sys.argv[1])
print("saved", sys.argv[1])
