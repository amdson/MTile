#!/usr/bin/env python3
"""Turn a loose row of drawings into an Aseprite-format sprite sheet.

    scripts/import-strip.py <in.png> <name> --frames N [options]

Writes Assets/AnimationEffects/<name>.png + <name>.json. The JSON is Aseprite's
"array" export format (frames + meta.frameTags + meta.slices), which is what
Drawing/SpriteSheet.cs loads — so a sheet exported straight from Aseprite,
LibreSprite, TexturePacker or free-tex-packer drops into the same slot.

Why this exists: generated or hand-painted effect strips arrive as N drawings in a
row at irregular spacing, often overlapping a naive equal-width grid (the
knife_energy_* sources do). This finds each drawing as a 2D connected component,
trims it, packs the trimmed frames, and records where each sits on a common canvas
so their anchors line up — the "pivot" slice marks that anchor.

Options:
  --frames N        how many drawings the strip holds (required). Specks beyond N
                    components merge into their nearest neighbor; fewer is an error.
  --anchor MODE     center (bbox center, radial bursts) | right | left (bbox edge,
                    y = the strip's alpha centroid — impacts whose contact point is
                    on that side) | top | bottom | feet (bottom edge, x = the ink
                    centroid of the lowest 15% — a standing character, steady even
                    when an arm or weapon reaches far out) | tip-up (a prop drawn
                    pointing up — x = bbox center, y = the MEDIAN frame top for all
                    frames, so partial frames (breaking, regrowing) keep their place
                    on the strip and the pivot sits on the full prop's tip line).
                    Default center.
  --downsample K    integer box-filter reduction. Records meta.scale = 1/K so effect
                    definitions keep sizing in ORIGINAL art pixels.
  --ms MS           per-frame duration in milliseconds (default 40).
  --tag NAME        one frameTag spanning every frame (default: <name>).
  --tags SPEC       several tags instead, e.g. idle:0-3,windup:4-5,attack:6 (frame
                    indices, inclusive). Enemy sheets name their tags this way —
                    see Assets/Sprites/entities.json for the vocabulary.
  --gap PX          dilation radius joining wisps to their drawing (default 6).
  --cuts X,X,...    explicit column boundaries (source px) between drawings; each
                    component joins the frame its center falls in. The fallback when
                    the automatic merge picks wrong — check the printed frame spans.
  --out DIR         output directory (default Assets/AnimationEffects).
  --key HEX         chroma-key a flat background color out first (e.g. ff00ff) —
                    for generated art, which rarely comes with real transparency.

Pure stdlib (zlib + struct) — no Pillow needed.
"""
import argparse, json, os, struct, sys, zlib
from collections import deque

ALPHA_MIN = 8    # alpha at or below this is haze, not drawing
CELL = 2         # segmentation grid (px); ownership is decided per cell
MAX_SHEET_W = 4096


def read_png(path):
    d = open(path, 'rb').read()
    if d[:8] != b'\x89PNG\r\n\x1a\n':
        sys.exit(f'{path}: not a PNG')
    i, idat = 8, b''
    while i < len(d):
        n, = struct.unpack('>I', d[i:i + 4]); t = d[i + 4:i + 8]; c = d[i + 8:i + 8 + n]; i += 12 + n
        if t == b'IHDR':
            w, h, bd, ct, _, _, il = struct.unpack('>IIBBBBB', c)
            if bd != 8 or ct not in (2, 6) or il != 0:
                sys.exit(f'{path}: need 8-bit RGB/RGBA, non-interlaced (got depth {bd}, color type {ct})')
            bpp = 4 if ct == 6 else 3
        elif t == b'IDAT':
            idat += c
    raw = zlib.decompress(idat)
    stride, p, prev, out = w * bpp, 0, bytearray(w * bpp), bytearray()
    for _ in range(h):
        f = raw[p]; line = bytearray(raw[p + 1:p + 1 + stride]); p += 1 + stride
        if f == 1:
            for x in range(bpp, stride): line[x] = (line[x] + line[x - bpp]) & 255
        elif f == 2:
            for x in range(stride): line[x] = (line[x] + prev[x]) & 255
        elif f == 3:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 255
        elif f == 4:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0; b = prev[x]; c = prev[x - bpp] if x >= bpp else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        out += line if bpp == 4 else b''.join(bytes(line[i:i + 3]) + b'\xff' for i in range(0, stride, 3))
        prev = line
    return w, h, out


def chroma_key(px, key, near=60, far=140):
    """Generated art rarely has real alpha; it comes on a flat key color. Pixels within
    `near` (RGB distance) of the key go transparent, beyond `far` stay opaque, between
    ramp — and the key's tint is pulled out of those soft edge pixels (despill)."""
    kr, kg, kb = key
    for i in range(0, len(px), 4):
        r, g, b = px[i], px[i + 1], px[i + 2]
        d = ((r - kr) ** 2 + (g - kg) ** 2 + (b - kb) ** 2) ** 0.5
        if d >= far: continue
        a = 0.0 if d <= near else (d - near) / (far - near)
        if a == 0.0:
            px[i:i + 4] = b'\0\0\0\0'; continue
        # Despill: remove the key color's share from the edge pixel, then un-mix.
        r2 = (r - kr * (1 - a)) / a; g2 = (g - kg * (1 - a)) / a; b2 = (b - kb * (1 - a)) / a
        px[i] = max(0, min(255, int(r2))); px[i + 1] = max(0, min(255, int(g2)))
        px[i + 2] = max(0, min(255, int(b2))); px[i + 3] = int(px[i + 3] * a)
    # Then strip any remaining key cast everywhere: anti-aliased ink and translucent
    # paint (wings, smoke) were blended over the key by the generator, so they carry
    # its tint well beyond the edge ramp. Spill = how far the key's strong channels
    # stand above its weak ones; pull the strong channels down by it. The art was
    # asked to avoid the key color, so a genuine key-hued detail is not expected.
    hi = [c for c in range(3) if key[c] > 127]; lo = [c for c in range(3) if key[c] <= 127]
    if hi and lo:
        for i in range(0, len(px), 4):
            if not px[i + 3]: continue
            spill = min(px[i + c] for c in hi) - max(px[i + c] for c in lo)
            if spill > 0:
                for c in hi: px[i + c] -= spill


def write_png(path, w, h, px):
    raw = bytearray()
    for y in range(h):
        raw.append(0); raw += px[y * w * 4:(y + 1) * w * 4]
    def chunk(t, c): return struct.pack('>I', len(c)) + t + c + struct.pack('>I', zlib.crc32(t + c) & 0xffffffff)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
                + chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))


def components(w, h, px, gap):
    """Label grid cells: a cell is ink if any sampled pixel clears ALPHA_MIN; ink is
    dilated by `gap` so a drawing's detached droplets stay with it."""
    gw, gh = (w + CELL - 1) // CELL, (h + CELL - 1) // CELL
    ink = bytearray(gw * gh)
    for gy in range(gh):
        for gx in range(gw):
            for y in range(gy * CELL, min(h, gy * CELL + CELL)):
                row = y * w * 4
                if any(px[row + x * 4 + 3] > ALPHA_MIN for x in range(gx * CELL, min(w, gx * CELL + CELL))):
                    ink[gy * gw + gx] = 1; break
    r = max(0, gap // CELL)   # 0: only literally touching ink joins (crowded strips)
    dil = bytearray(gw * gh)   # separable box dilation
    tmp = bytearray(gw * gh)
    for gy in range(gh):
        for gx in range(gw):
            if ink[gy * gw + gx]:
                for x in range(max(0, gx - r), min(gw, gx + r + 1)): tmp[gy * gw + x] = 1
    for gy in range(gh):
        for gx in range(gw):
            if tmp[gy * gw + gx]:
                for y in range(max(0, gy - r), min(gh, gy + r + 1)): dil[y * gw + gx] = 1
    label = [0] * (gw * gh); comps = []
    for start in range(gw * gh):
        if not dil[start] or label[start]: continue
        lid = len(comps) + 1; q = deque([start]); label[start] = lid
        x0 = y0 = 10 ** 9; x1 = y1 = -1; area = 0
        while q:
            c = q.popleft(); cx, cy = c % gw, c // gw
            area += ink[c]
            x0, x1, y0, y1 = min(x0, cx), max(x1, cx), min(y0, cy), max(y1, cy)
            for nx, ny in ((cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1)):
                if 0 <= nx < gw and 0 <= ny < gh:
                    n = ny * gw + nx
                    if dil[n] and not label[n]: label[n] = lid; q.append(n)
        comps.append({'ids': {lid}, 'area': area, 'box': [x0, y0, x1, y1]})
    return gw, label, comps


def merge_to(comps, n):
    """Fold the smallest component into its nearest neighbor until n remain. Nearest is
    HORIZONTAL first — strips run left to right, and a droplet flung far above or below
    its drawing still sits inside that drawing's column span."""
    def gap(a, b):
        cx, cy = (a[0] + a[2]) / 2, (a[1] + a[3]) / 2
        dx = max(0, b[0] - cx, cx - b[2]); dy = max(0, b[1] - cy, cy - b[3])
        return (dx, dy)
    def inside(a, b):   # a's center column falls within b's span, and a is the lesser
        cx = (a['box'][0] + a['box'][2]) / 2
        return a is not b and a['area'] <= b['area'] and b['box'][0] <= cx <= b['box'][2]
    while len(comps) > n:
        # Specks inside another drawing's column span go first: area alone can't tell a
        # stray droplet cluster from a genuinely tiny frame (a burst's last puff).
        nested = [c for c in comps if any(inside(c, o) for o in comps)]
        small = min(nested or comps, key=lambda c: c['area'])
        comps.remove(small)
        t = min(range(len(comps)), key=lambda i: gap(small['box'], comps[i]['box']))
        big = comps[t]; big['ids'] |= small['ids']; big['area'] += small['area']
        b, o = big['box'], small['box']
        big['box'] = [min(b[0], o[0]), min(b[1], o[1]), max(b[2], o[2]), max(b[3], o[3])]
    return comps


def downsample(w, h, px, k):
    """Box filter in premultiplied space, written back as straight alpha."""
    ow, oh = w // k, h // k; out = bytearray(ow * oh * 4); n = k * k
    for oy in range(oh):
        for ox in range(ow):
            r = g = b = a = 0
            for y in range(oy * k, oy * k + k):
                row = (y * w + ox * k) * 4
                for i in range(row, row + k * 4, 4):
                    al = px[i + 3]; a += al; r += px[i] * al; g += px[i + 1] * al; b += px[i + 2] * al
            o = (oy * ow + ox) * 4
            if a:
                out[o], out[o + 1], out[o + 2] = r // a, g // a, b // a
                out[o + 3] = (a + n // 2) // n
    return ow, oh, out


def parse_tags(spec, n):
    tags = []
    for part in spec.split(','):
        name, _, span = part.strip().partition(':')
        lo, _, hi = span.partition('-')
        lo, hi = int(lo), int(hi or lo)
        if not name or not 0 <= lo <= hi < n:
            sys.exit(f'--tags: bad entry {part!r} for {n} frames')
        tags.append({'name': name, 'from': lo, 'to': hi, 'direction': 'forward'})
    return tags


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('src'); ap.add_argument('name')
    ap.add_argument('--frames', type=int, required=True)
    ap.add_argument('--anchor', default='center', choices=['center', 'right', 'left', 'top', 'bottom', 'feet', 'tip-up'])
    ap.add_argument('--downsample', type=int, default=1)
    ap.add_argument('--ms', type=int, default=40)
    ap.add_argument('--tag')
    ap.add_argument('--tags')
    ap.add_argument('--gap', type=int, default=6)
    ap.add_argument('--cuts')
    ap.add_argument('--key')
    ap.add_argument('--out', default=os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'Assets', 'AnimationEffects'))
    a = ap.parse_args()

    w, h, px = read_png(a.src)
    if a.key:
        k = a.key.lstrip('#')
        chroma_key(px, (int(k[0:2], 16), int(k[2:4], 16), int(k[4:6], 16)))
    gw, label, comps = components(w, h, px, a.gap)
    if a.cuts:
        cuts = sorted(float(c) / CELL for c in a.cuts.split(','))
        if len(cuts) + 1 != a.frames:
            sys.exit(f'--cuts gives {len(cuts) + 1} frames, --frames says {a.frames}')
        groups = [[] for _ in range(a.frames)]
        for c in comps:
            cx = (c['box'][0] + c['box'][2]) / 2
            groups[sum(cx > x for x in cuts)].append(c)
        if any(not g for g in groups):
            sys.exit('a --cuts interval holds no drawing')
        comps = []
        for g in groups:
            comps.append({'ids': set().union(*(c['ids'] for c in g)), 'area': sum(c['area'] for c in g),
                          'box': [min(c['box'][0] for c in g), min(c['box'][1] for c in g),
                                  max(c['box'][2] for c in g), max(c['box'][3] for c in g)]})
    elif len(comps) < a.frames:
        boxes = ', '.join(f'x{c["box"][0] * CELL}-{c["box"][2] * CELL}' for c in sorted(comps, key=lambda c: c['box'][0]))
        sys.exit(f'found only {len(comps)} separate drawings, expected {a.frames} ({boxes}). '
                 f'Drawings touch — try a smaller --gap, or separate them in a paint tool.')
    comps = sorted(merge_to(comps, a.frames), key=lambda c: (c['box'][0] + c['box'][2]))
    print('frame spans (source px):', ' '.join(f'{c["box"][0] * CELL}-{(c["box"][2] + 1) * CELL}' for c in comps))

    # Per frame: own the pixels whose cell carries one of its labels; trim to them.
    owner = {}
    for fi, c in enumerate(comps):
        for lid in c['ids']: owner[lid] = fi
    frames = []
    for fi, c in enumerate(comps):
        bx0, by0 = c['box'][0] * CELL, c['box'][1] * CELL
        bx1, by1 = min(w, (c['box'][2] + 1) * CELL), min(h, (c['box'][3] + 1) * CELL)
        fw, fh = bx1 - bx0, by1 - by0
        img = bytearray(fw * fh * 4); sa = sy = 0
        for y in range(by0, by1):
            grow = (y // CELL) * gw
            for x in range(bx0, bx1):
                if owner.get(label[grow + x // CELL]) != fi: continue
                s = (y * w + x) * 4; al = px[s + 3]
                if al == 0: continue
                d = ((y - by0) * fw + (x - bx0)) * 4
                img[d:d + 4] = px[s:s + 4]; sa += al; sy += al * y
        frames.append({'img': img, 'x': bx0, 'y': by0, 'w': fw, 'h': fh, 'cy': sy / sa if sa else (by0 + by1) / 2,
                       'sa': sa})

    k = max(1, a.downsample)
    if k > 1:
        for f in frames:
            # Pad to a multiple of k so the box filter never drops an edge row/column.
            pw, ph = -(-f['w'] // k) * k, -(-f['h'] // k) * k
            img = bytearray(pw * ph * 4)
            for y in range(f['h']): img[y * pw * 4:(y * pw + f['w']) * 4] = f['img'][y * f['w'] * 4:(y + 1) * f['w'] * 4]
            f['w'], f['h'], f['img'] = downsample(pw, ph, img, k)
            f['x'] /= k; f['y'] /= k; f['cy'] /= k

    # Anchor per frame, in strip coordinates (post-downsample).
    total = sum(f['sa'] for f in frames) or 1
    strip_cy = sum(f['cy'] * f['sa'] for f in frames) / total
    tops = sorted(f['y'] for f in frames)
    median_top = tops[len(tops) // 2]
    for f in frames:
        x0, y0, x1, y1 = f['x'], f['y'], f['x'] + f['w'], f['y'] + f['h']
        if a.anchor == 'tip-up':
            f['ax'], f['ay'] = (x0 + x1) / 2, median_top
            continue
        if a.anchor == 'feet':
            band, sx = 0, 0
            for y in range(int(f['h'] * 0.85), f['h']):
                for x in range(f['w']):
                    al = f['img'][(y * f['w'] + x) * 4 + 3]; band += al; sx += al * x
            f['ax'], f['ay'] = (x0 + sx / band if band else (x0 + x1) / 2), y1
            continue
        f['ax'], f['ay'] = {
            'center': ((x0 + x1) / 2, (y0 + y1) / 2),
            'right':  (x1, strip_cy), 'left': (x0, strip_cy),
            'top':    ((x0 + x1) / 2, y0), 'bottom': ((x0 + x1) / 2, y1),
        }[a.anchor]

    # Common canvas: every frame placed so its anchor lands on the canvas pivot.
    import math
    px_ = math.ceil(max(f['ax'] - f['x'] for f in frames))
    py_ = math.ceil(max(f['ay'] - f['y'] for f in frames))
    cw = px_ + math.ceil(max(f['x'] + f['w'] - f['ax'] for f in frames))
    ch = py_ + math.ceil(max(f['y'] + f['h'] - f['ay'] for f in frames))

    # Shelf-pack the trimmed frames with a 1px transparent border against filter bleed.
    sx = sy = shelf_h = sheet_w = 0
    for f in frames:
        if sx + f['w'] + 2 > MAX_SHEET_W: sx, sy, shelf_h = 0, sy + shelf_h, 0
        f['px'], f['py'] = sx + 1, sy + 1
        sx += f['w'] + 2; shelf_h = max(shelf_h, f['h'] + 2); sheet_w = max(sheet_w, sx)
    sheet_h = sy + shelf_h
    sheet = bytearray(sheet_w * sheet_h * 4)
    for f in frames:
        for y in range(f['h']):
            d = ((f['py'] + y) * sheet_w + f['px']) * 4
            sheet[d:d + f['w'] * 4] = f['img'][y * f['w'] * 4:(y + 1) * f['w'] * 4]

    os.makedirs(a.out, exist_ok=True)
    png_name = a.name + '.png'
    write_png(os.path.join(a.out, png_name), sheet_w, sheet_h, sheet)
    doc = {
        'frames': [{
            'filename': f'{a.name} {i}',
            'frame': {'x': f['px'], 'y': f['py'], 'w': f['w'], 'h': f['h']},
            'rotated': False, 'trimmed': True,
            'spriteSourceSize': {'x': round(px_ - (f['ax'] - f['x'])), 'y': round(py_ - (f['ay'] - f['y'])),
                                 'w': f['w'], 'h': f['h']},
            'sourceSize': {'w': cw, 'h': ch},
            'duration': a.ms,
        } for i, f in enumerate(frames)],
        'meta': {
            'app': 'scripts/import-strip.py', 'image': png_name, 'format': 'RGBA8888',
            'size': {'w': sheet_w, 'h': sheet_h}, 'scale': f'{1 / k:g}',
            'frameTags': parse_tags(a.tags, len(frames)) if a.tags else
                         [{'name': a.tag or a.name, 'from': 0, 'to': len(frames) - 1, 'direction': 'forward'}],
            'slices': [{'name': 'pivot', 'color': '#0000ffff',
                        'keys': [{'frame': 0, 'bounds': {'x': px_, 'y': py_, 'w': 1, 'h': 1}, 'pivot': {'x': 0, 'y': 0}}]}],
        },
    }
    with open(os.path.join(a.out, a.name + '.json'), 'w') as fp:
        json.dump(doc, fp, indent=1); fp.write('\n')
    print(f'{a.name}: {len(frames)} frames, sheet {sheet_w}x{sheet_h}, canvas {cw}x{ch}, pivot ({px_},{py_}), scale {1 / k:g}')


if __name__ == '__main__':
    main()
