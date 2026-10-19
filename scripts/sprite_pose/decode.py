"""Quick decoder: 4x4 stick sheet -> per-frame bone world angles -> rig rotations,
re-rendered beside the source cell for visual comparison.

Per bone: pixels nearest its legend color; direction = principal axis, oriented
away from the parent joint (torso: hip=mean of thigh pixels' top end).
"""
import sys, os, math, json
import numpy as np
from PIL import Image, ImageDraw
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from stick import COLORS, BONES, fk, draw_pose, sole_y

LIMBS = ['arm_r_upper', 'arm_r_lower', 'leg_r_upper', 'leg_r_lower',
         'arm_l_upper', 'arm_l_lower', 'leg_l_upper', 'leg_l_lower']
PAL = np.array([COLORS[b] for b in LIMBS], float)


def classify(cell):
    a = np.asarray(cell.convert('RGB'), float)
    sat = a.max(2) - a.min(2)
    d = ((a[:, :, None, :] - PAL[None, None]) ** 2).sum(-1)
    lab = d.argmin(2)
    ok = (sat > 90) & (d.min(2) < 70 ** 2)
    return lab, ok


def axis(pts):
    c = pts.mean(0)
    u, s, vt = np.linalg.svd(pts - c, full_matrices=False)
    v = vt[0]
    proj = (pts - c) @ v
    return c, v, c + v * proj.min(), c + v * proj.max(), s


def decode_cell(cell):
    lab, ok = classify(cell)
    segs = {}
    for i, b in enumerate(LIMBS):
        ys, xs = np.nonzero(ok & (lab == i))
        if len(xs) < 30:
            segs[b] = None
            continue
        pts = np.stack([xs, ys], 1).astype(float)
        segs[b] = axis(pts)
    flags = [b for b in LIMBS if segs[b] is None]
    # joints: shoulder = upper-arm end nearest the other arm's; hip = thigh ends nearest each other
    def near_end(b, ref):
        _, _, e0, e1, _ = segs[b]
        return (e0, e1) if np.linalg.norm(e0 - ref) < np.linalg.norm(e1 - ref) else (e1, e0)
    ang = {}
    try:
        tl, tr = segs['leg_l_upper'], segs['leg_r_upper']
        al, ar = segs['arm_l_upper'], segs['arm_r_upper']
        # hip ~ the thigh endpoints that are highest; shoulder ~ upper-arm endpoints highest
        hip = np.mean([min((tl[2], tl[3]), key=lambda p: p[1]), min((tr[2], tr[3]), key=lambda p: p[1])], 0)
        sh = np.mean([min((al[2], al[3]), key=lambda p: p[1]), min((ar[2], ar[3]), key=lambda p: p[1])], 0)
        ang['chest'] = math.atan2(*(sh - hip)[::-1])
        for side in 'lr':
            for limb, root in (('arm', sh), ('leg', hip)):
                up, lo = f'{limb}_{side}_upper', f'{limb}_{side}_lower'
                s0, s1 = near_end(up, root)
                ang[up] = math.atan2(*(s1 - s0)[::-1])
                if segs[lo] is not None:
                    l0, l1 = near_end(lo, s1)
                    ang[lo] = math.atan2(*(l1 - l0)[::-1])
                else:
                    ang[lo] = ang[up]
    except TypeError:
        return None, flags + ['undecodable']
    # world angles -> local rotations along the chain
    rot = {'hip': 0.0, 'chest': ang['chest'], 'head': 0.0}
    for side in 'lr':
        for limb, par in (('arm', ang['chest']), ('leg', 0.0)):
            up, lo = f'{limb}_{side}_upper', f'{limb}_{side}_lower'
            rot[up] = ang[up] - par
            rot[lo] = ang[lo] - ang[up]
    wrap = lambda x: (x + math.pi) % (2 * math.pi) - math.pi
    rot = {k: wrap(v) for k, v in rot.items()}
    # knee/elbow sanity flags (rig knee bends forward = lower rotation in [0, 3.05])
    for side in 'lr':
        if not (-0.15 <= rot[f'leg_{side}_lower'] <= 3.05):
            flags.append(f'knee_{side}_backward({rot[f"leg_{side}_lower"]:.2f})')
    return rot, flags


def run(path, out, grid=4):
    im = Image.open(path).convert('RGB')
    W, H = im.size
    cw, ch = W / grid, H / grid
    T = 220
    sheet = Image.new('RGB', (grid * 2 * T, grid * T), 'white')
    dr = ImageDraw.Draw(sheet)
    results = []
    for i in range(grid * grid):
        cx, cy = (i % grid), (i // grid)
        cell = im.crop((int(cx * cw), int(cy * ch), int((cx + 1) * cw), int((cy + 1) * ch)))
        rot, flags = decode_cell(cell)
        results.append({'frame': i + 1, 'rot': rot, 'flags': flags})
        x0, y0 = cx * 2 * T, cy * T
        sheet.paste(cell.resize((T, T)), (x0, y0))
        if rot:
            seg = fk(rot)
            s = T / 64.0
            ground = y0 + T * 0.86
            draw_pose(dr, seg, x0 + T + T / 2, ground - sole_y(seg) * s, s, 5)
            dr.line([(x0 + T + 8, ground), (x0 + 2 * T - 8, ground)], fill=(150, 150, 150), width=2)
        dr.text((x0 + T + 4, y0 + 4), f'{i+1} ' + ' '.join(flags)[:40], fill=(200, 0, 0))
        dr.rectangle([x0, y0, x0 + 2 * T - 1, y0 + T - 1], outline=(180, 180, 180))
    sheet.save(out)
    json.dump(results, open(out.replace('.png', '.json'), 'w'), indent=1)
    for r in results:
        print(r['frame'], r['flags'])


if __name__ == '__main__':
    run(sys.argv[1], sys.argv[2])
