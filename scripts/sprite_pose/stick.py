"""Render MTile biped poses as OpenPose-style colored stick figures.

Side view, faces +X (right), Y-down. FK: each bone starts at its parent's tip,
world angle = sum of local Rotations down the chain, tip = start + Length*dir.
Near limbs (_r) warm colors, far limbs (_l) cool colors; far drawn first.
"""
import json, math, os, sys, tarfile
from PIL import Image, ImageDraw

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
RIG = json.load(open(os.path.join(REPO, 'Skeletons', 'biped.json')))
BONES = {b['Name']: b for b in RIG['Bones']}
ORDER = [b['Name'] for b in RIG['Bones']]

COLORS = {
    'chest': (40, 40, 40), 'head': (40, 40, 40),
    'arm_r_upper': (230, 30, 30), 'arm_r_lower': (255, 150, 0),
    'leg_r_upper': (220, 0, 200), 'leg_r_lower': (245, 215, 0),
    'arm_l_upper': (30, 70, 230), 'arm_l_lower': (0, 200, 230),
    'leg_l_upper': (0, 170, 60), 'leg_l_lower': (120, 230, 40),
}
DRAW_ORDER = ['arm_l_upper', 'arm_l_lower', 'leg_l_upper', 'leg_l_lower',
              'chest', 'head',
              'leg_r_upper', 'leg_r_lower', 'arm_r_upper', 'arm_r_lower']


def fk(rot):
    """rot: bone -> local rotation. Returns bone -> (start, tip) in rig units."""
    ang, seg = {}, {}
    for n in ORDER:
        b = BONES[n]
        p = b['Parent']
        a = rot.get(n, b['Rotation']) + (ang[p] if p else 0.0)
        s = seg[p][1] if p else (0.0, 0.0)
        t = (s[0] + b['Length'] * math.cos(a), s[1] + b['Length'] * math.sin(a))
        ang[n], seg[n] = a, (s, t)
    return seg


BACKUP = os.path.join(REPO, 'Backups', 'clips-20260918-111934.tar.gz')


def load_clip(name):
    """A biped clip from the pre-wipe backup (the live library is rest-pose stubs)."""
    with tarfile.open(BACKUP) as tf:
        return json.load(tf.extractfile(f'SkeletonStates/biped/{name}.json'))


def pose_from_clip(name, t):
    """Rotations of the keyframe nearest normalized time t (no interpolation)."""
    d = load_clip(name)
    kf = min(d['Keyframes'], key=lambda k: abs(k['Time'] - t))
    return {b['Bone']: b['Rotation'] for b in kf['Bones'] if b['Bone'] in BONES}


def draw_pose(dr, seg, ox, oy, s, width):
    """Draw at cell origin: hip at (ox, oy) in pixels, s px per rig unit."""
    P = lambda q: (ox + q[0] * s, oy + q[1] * s)
    for n in DRAW_ORDER:
        a, b = seg[n]
        if n == 'head':
            c = P(((a[0] + b[0]) / 2, (a[1] + b[1]) / 2))
            r = BONES['head']['Length'] * s * 0.55
            dr.ellipse([c[0] - r, c[1] - r, c[0] + r, c[1] + r], fill=COLORS[n])
        else:
            dr.line([P(a), P(b)], fill=COLORS[n], width=width)
            for q in (P(a), P(b)):
                w = width / 2
                dr.ellipse([q[0] - w, q[1] - w, q[0] + w, q[1] + w], fill=COLORS[n])


def sole_y(seg):
    return max(seg['leg_l_lower'][1][1], seg['leg_r_lower'][1][1])


def render_sheet(poses, cols, cell=256, out='sheet.png', labels=True):
    rows = (len(poses) + cols - 1) // cols
    img = Image.new('RGB', (cols * cell, rows * cell), 'white')
    dr = ImageDraw.Draw(img)
    s = cell / 64.0  # ~45 rig units tall -> ~180 px in a 256 cell
    for i, rot in enumerate(poses):
        cx, cy = (i % cols) * cell, (i // cols) * cell
        seg = fk(rot)
        ground = cy + cell * 0.86
        oy = ground - sole_y(seg) * s
        ox = cx + cell / 2
        dr.line([(cx + 8, ground), (cx + cell - 8, ground)], fill=(150, 150, 150), width=3)
        draw_pose(dr, seg, ox, oy, s, width=max(3, int(cell / 32)))
        dr.rectangle([cx, cy, cx + cell - 1, cy + cell - 1], outline=(200, 200, 200), width=2)
        if labels:
            dr.text((cx + 8, cy + 6), str(i + 1), fill=(120, 120, 120))
    img.save(out)
    return out


if __name__ == '__main__':
    # Reference renders used for the 2026-10-19 GPT experiment: idle start pose,
    # groundslash3 @0.5 (lunge) end pose, and a 4-key walk strip.
    out = sys.argv[1] if len(sys.argv) > 1 else '.'
    idle = pose_from_clip('idle', 0)
    end = pose_from_clip('groundslash3', 0.5)
    render_sheet([idle], 1, cell=512, out=os.path.join(out, 'ref_start.png'), labels=False)
    render_sheet([idle, end], 2, cell=512, out=os.path.join(out, 'ref_pair.png'), labels=False)
    walk = [pose_from_clip('walk', t) for t in (0, 0.25, 0.5, 0.75)]
    render_sheet(walk, 4, cell=256, out=os.path.join(out, 'walk_strip.png'))
