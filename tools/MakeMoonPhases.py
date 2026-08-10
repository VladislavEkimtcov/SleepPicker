"""
Renders docs/moon-phases.png: every phase the dynamic tray icon can draw, 0% to 100%.

This is a *reference* sheet, not an embedded asset. SleepPicker draws its tray icon at
run time (src/MoonIcon.cs) from the geometry below, so it stays crisp at whatever size
the notification area asks for and the executable stays one small file. This script
exists so that geometry is reviewable as a picture rather than only as arithmetic: the
constants here and the ones in MoonIcon.cs are the same numbers, and the sheet is what
they are supposed to look like.

Standard library only -- the machines this project targets (Windows IoT Enterprise LTSC)
have no package manager, so there is no Pillow to import. The PNG is written by hand.

    python tools\\MakeMoonPhases.py
"""

import math
import os
import struct
import zlib

# ---------------------------------------------------------------------------
# Geometry and colour. Keep in step with src/MoonIcon.cs.
# ---------------------------------------------------------------------------

# Measured off assets/SleepPicker.png, the artwork the executable icon is drawn from,
# as fractions of the icon's edge so every size is the same picture.
DISK_RADIUS = 0.414          # 26.5 / 64
RIM_WIDTH = 0.047            # 3 / 64

# The artwork's crescent is tilted: its unlit side faces the upper right. Rotating the
# phase series by the same angle keeps the tray icon recognisably the same moon as the
# one on the executable.
TILT_DEGREES = 38.0

GOLD = (0xF7, 0xC9, 0x48)
RIM = (0x5B, 0x49, 0x18)

# Near new moon the lit crescent has almost no area left, so the unlit limb is drawn as
# a ring instead -- earthshine, the "old moon in the new moon's arms". Without it the
# tray slot would simply go blank at 0% and read as a crashed program.
EARTHSHINE_FROM = 20.0       # percent at which the ring starts to appear
SUPERSAMPLE = 4              # sub-samples per pixel per axis


def lit(x, y, radius, terminator):
    """
    True if (x, y), measured from the disk centre in an upright moon, is lit.

    The terminator -- the day/night line -- is a great circle seen edge on, so it
    projects to an ellipse sharing the disk's vertical radius. `terminator` is its signed
    horizontal semi-axis: negative bulges right (gibbous), positive cuts left (crescent),
    zero is a straight edge at half moon. The moon is lit on its left, as a waning moon
    is, which is the way the artwork's crescent opens.
    """
    if (x * x) + (y * y) > radius * radius:
        return False
    if abs(terminator) < 1e-9:
        return x <= 0.0

    ellipse = ((x / terminator) ** 2) + ((y / radius) ** 2)
    if terminator < 0.0:
        return x <= 0.0 or ellipse <= 1.0
    return x <= 0.0 and ellipse >= 1.0


def render(percent, size):
    """One frame as straight-alpha RGBA bytes, `size` x `size`."""
    centre = size / 2.0
    radius = DISK_RADIUS * size
    nominal_rim = max(1.0, RIM_WIDTH * size)

    # Signed horizontal semi-axis of the terminator. At 100% it equals -radius, so the
    # ellipse coincides with the limb and the whole disk is lit; at 0% it equals +radius
    # and nothing is.
    terminator = radius * (1.0 - (2.0 * percent / 100.0))

    # A rim of fixed width eats a thin crescent alive: at 16px, the size the notification
    # area asks for at 100% scaling, a crescent is narrower than one pixel below about 15%
    # charge, and a full-width rim would swallow every last scrap of gold -- leaving 15%
    # and 0% looking alike, which is the one place the reading matters most. So the rim is
    # given at most a quarter of whatever the crescent is wide, and thins away with it,
    # leaving gold the middle half.
    rim = min(nominal_rim, (radius - terminator) / 4.0)

    # The rim is the lit region minus the same region shrunk by the rim width. Shrinking
    # pulls the limb in and pushes the terminator out, which is the one expression
    # `terminator + rim` gives for both a gibbous bulge and a crescent bite.
    inner_radius = radius - rim
    inner_terminator = terminator + rim

    # The unlit limb, drawn as a ring between inner_radius and radius.
    ring_alpha = 0.0
    if percent < EARTHSHINE_FROM:
        ring_alpha = (EARTHSHINE_FROM - percent) / EARTHSHINE_FROM

    angle = math.radians(TILT_DEGREES)
    cos_a = math.cos(angle)
    sin_a = math.sin(angle)

    step = 1.0 / SUPERSAMPLE
    samples = SUPERSAMPLE * SUPERSAMPLE
    out = bytearray(size * size * 4)

    for py in range(size):
        for px in range(size):
            r_sum = g_sum = b_sum = a_sum = 0.0
            for sy in range(SUPERSAMPLE):
                dy = py + ((sy + 0.5) * step) - centre
                for sx in range(SUPERSAMPLE):
                    dx = px + ((sx + 0.5) * step) - centre

                    # Rotate the sample back into the upright moon's frame. Screen y
                    # points down, so this undoes an anticlockwise tilt on screen.
                    x = (dx * cos_a) - (dy * sin_a)
                    y = (dx * sin_a) + (dy * cos_a)

                    if lit(x, y, inner_radius, inner_terminator):
                        colour, alpha = GOLD, 1.0
                    elif lit(x, y, radius, terminator):
                        colour, alpha = RIM, 1.0
                    elif ring_alpha > 0.0:
                        distance = math.hypot(x, y)
                        # Full width, not the thinned rim: by the time the ring matters it
                        # is the only thing left to see, and a hairline would not be seen.
                        if (radius - nominal_rim) <= distance <= radius:
                            colour, alpha = RIM, ring_alpha
                        else:
                            continue
                    else:
                        continue

                    r_sum += colour[0] * alpha
                    g_sum += colour[1] * alpha
                    b_sum += colour[2] * alpha
                    a_sum += alpha

            offset = ((py * size) + px) * 4
            if a_sum <= 0.0:
                continue
            # Sums are premultiplied by coverage, so dividing by the alpha sum rather
            # than the sample count recovers the straight colour.
            out[offset] = min(255, int(round(r_sum / a_sum)))
            out[offset + 1] = min(255, int(round(g_sum / a_sum)))
            out[offset + 2] = min(255, int(round(b_sum / a_sum)))
            out[offset + 3] = int(round(255.0 * a_sum / samples))

    return out


def blend(sheet, sheet_width, frame, size, left, top):
    """Source-over `frame` onto the opaque `sheet`."""
    for y in range(size):
        for x in range(size):
            src = ((y * size) + x) * 4
            alpha = frame[src + 3] / 255.0
            if alpha <= 0.0:
                continue
            dst = (((top + y) * sheet_width) + left + x) * 3
            for c in range(3):
                sheet[dst + c] = int(round((frame[src + c] * alpha) +
                                           (sheet[dst + c] * (1.0 - alpha))))


def write_png(path, width, height, rgb):
    """Minimal 8-bit RGB PNG."""
    stride = width * 3
    raw = bytearray()
    for y in range(height):
        raw.append(0)                                   # filter type: none
        raw += rgb[y * stride:(y + 1) * stride]

    def chunk(tag, payload):
        return (struct.pack('>I', len(payload)) + tag + payload +
                struct.pack('>I', zlib.crc32(tag + payload) & 0xFFFFFFFF))

    header = struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0)
    with open(path, 'wb') as handle:
        handle.write(b'\x89PNG\r\n\x1a\n')
        handle.write(chunk(b'IHDR', header))
        handle.write(chunk(b'IDAT', zlib.compress(bytes(raw), 9)))
        handle.write(chunk(b'IEND', b''))


def main():
    levels = list(range(101))
    columns = 10

    # A large block to judge the shapes by, and the same series at 16px -- the size the
    # notification area actually asks for at 100% scaling -- to judge legibility by.
    large, small = 48, 16
    gap, margin = 4, 12
    rows = (len(levels) + columns - 1) // columns

    width = (columns * (large + gap)) - gap + (2 * margin)
    large_block = (rows * (large + gap)) - gap
    small_block = (rows * (small + gap)) - gap
    height = (2 * margin) + large_block + (2 * gap) + small_block

    # Mid grey: both the gold fill and the dark rim read against it, whichever theme the
    # sheet is being viewed in.
    sheet = bytearray([0x6E]) * (width * height * 3)

    for index, percent in enumerate(levels):
        column = index % columns
        row = index // columns
        # Both blocks share the large block's column pitch, so a frame sits directly
        # under its own big version.
        column_left = margin + (column * (large + gap))

        blend(sheet, width, render(percent, large), large,
              column_left, margin + (row * (large + gap)))

        blend(sheet, width, render(percent, small), small,
              column_left + ((large - small) // 2),
              margin + large_block + (2 * gap) + (row * (small + gap)))

        print('rendered %d%%' % percent, end='\r')

    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_path = os.path.join(repo_root, 'docs', 'moon-phases.png')
    write_png(out_path, width, height, sheet)
    print('Wrote %s (%dx%d, %d phases).' % (out_path, width, height, len(levels)))


if __name__ == '__main__':
    main()
