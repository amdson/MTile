"""Rig loading, sprite profiles, FK, and the stick-sheet renderer — rig-agnostic.

A *sprite profile* says how a rig is drawn as a color-coded stick figure: which bones
are drawn (long enough to read in an image), which are held at bind or derived later,
which side is near/far, and each drawn bone's color. It is derived from the rig file
and cached in profiles/<rig>.json, where it can be hand-edited.

Conventions match the engine (Skeletons/*.json, Animation/PoseState.cs): side view,
faces +X, Y-down; a bone starts at its parent's tip, world angle = sum of local
Rotations down the chain, tip = start + Length*Stretch*dir.
"""
import json, math, os, tarfile
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
PROFILES = os.path.join(HERE, 'profiles')
BACKUP = os.path.join(REPO, 'Backups', 'clips-20260918-111934.tar.gz')

MIN_DRAW = 4.0           # rig units; shorter bones can't be read back from an image
WEAPON_LEN = 9.0         # rig units, drawn from the weapon's parent tip (the hand); the game knife is short
CORE = (40, 40, 40)
WEAPON = (120, 0, 255)
GROUND = (150, 150, 150)
PALETTES = {             # (side, limb kind) -> colors by depth along the limb
    ('near', 'arm'): [(230, 30, 30), (255, 150, 0)],
    ('near', 'leg'): [(220, 0, 200), (245, 215, 0)],
    ('far', 'arm'): [(30, 70, 230), (0, 200, 230)],
    ('far', 'leg'): [(0, 170, 60), (120, 230, 40)],
}
DESCR = {('near', 'arm'): ['near upper arm', 'near forearm'], ('near', 'leg'): ['near thigh', 'near shin'],
         ('far', 'arm'): ['far upper arm', 'far forearm'], ('far', 'leg'): ['far thigh', 'far shin']}


# ---- rig + clips ---------------------------------------------------------------------

def load_rig(name):
    d = json.load(open(os.path.join(REPO, 'Skeletons', name + '.json')))
    return d


def bone_map(rig):
    return {b['Name']: b for b in rig['Bones']}


def load_clip(spec, rig_name):
    """'name' -> live pool SkeletonStates/<rig>/name.json; 'backup:name' -> the pre-wipe tarball."""
    if spec.startswith('backup:'):
        with tarfile.open(BACKUP) as tf:
            return json.load(tf.extractfile(f'SkeletonStates/{rig_name}/{spec[7:]}.json'))
    return json.load(open(os.path.join(REPO, 'SkeletonStates', rig_name, spec + '.json')))


def pose_at(spec_t, rig_name):
    """'clip@t' (t normalized) -> {bone: (rotation, stretch)} of the nearest keyframe."""
    spec, t = spec_t.rsplit('@', 1) if '@' in spec_t else (spec_t, '0')
    d = load_clip(spec, rig_name)
    kf = min(d['Keyframes'], key=lambda k: abs(k['Time'] - float(t)))
    return {b['Bone']: (b['Rotation'], b.get('Stretch')) for b in kf['Bones']}


# ---- profile -------------------------------------------------------------------------

def side_of(name):
    toks = name.split('_')
    return 'near' if 'r' in toks else 'far' if 'l' in toks else None


def kind_of(name):
    return 'arm' if 'arm' in name else 'leg' if 'leg' in name else None


def derive_profile(rig):
    bm = bone_map(rig)
    root = next(b['Name'] for b in rig['Bones'] if not b.get('Parent'))
    drawn, held = [], []
    for b in rig['Bones']:
        (drawn if b['Length'] >= MIN_DRAW else held).append(b['Name'])
    colors, labels, sides = {}, {}, {}
    for n in drawn:
        side, kind = side_of(n), kind_of(n)
        if side and kind:
            # depth = number of drawn ancestors of the same limb kind
            depth, p = 0, bm[n].get('Parent')
            while p:
                if p in drawn and kind_of(p) == kind:
                    depth += 1
                p = bm[p].get('Parent')
            colors[n] = PALETTES[(side, kind)][min(depth, 1)]
            labels[n] = DESCR[(side, kind)][min(depth, 1)]
            sides[n] = side
        else:
            colors[n], labels[n], sides[n] = CORE, ('head' if n == 'head' else 'torso'), 'core'
    hand = next((n for n in drawn if sides.get(n) == 'near' and kind_of(n) == 'arm'
                 and not any(bm[c].get('Parent') == n and c in drawn for c in bm)), None)
    return {
        'rig': rig['Name'], 'root': root, 'head': 'head' if 'head' in bm else None,
        'drawn': drawn, 'colors': colors, 'labels': labels, 'sides': sides,
        # undrawn bones: 'hold' keeps the start pose's value; struts that the clip animates
        # (biped_rabbit shoulder_*/hip_*) are re-derived by Probe `bakeyaw` after decoding
        'undrawn': {n: ('derive:bakeyaw' if n.startswith(('shoulder_', 'hip_')) else 'hold') for n in held},
        'weapon': {'parent': hand, 'color': WEAPON, 'length': WEAPON_LEN, 'bone': 'knife'},
        'min_draw': MIN_DRAW,
    }


def profile(rig_name, regenerate=False):
    os.makedirs(PROFILES, exist_ok=True)
    p = os.path.join(PROFILES, rig_name + '.json')
    if regenerate or not os.path.exists(p):
        prof = derive_profile(load_rig(rig_name))
        json.dump(prof, open(p, 'w'), indent=1)
    prof = json.load(open(p))
    prof['colors'] = {k: tuple(v) for k, v in prof['colors'].items()}
    prof['weapon']['color'] = tuple(prof['weapon']['color'])
    return prof


# ---- FK ------------------------------------------------------------------------------

def fk(rig, pose, weapon=None):
    """pose: {bone: rotation | (rotation, stretch)} (missing -> bind).
    weapon: optional (parent bone, local angle, length).
    Returns {bone: (start, tip, world_angle)} in rig units, root at the origin."""
    out = {}
    for b in rig['Bones']:
        n, p = b['Name'], b.get('Parent')
        v = pose.get(n, b['Rotation'])
        rot, st = (v if isinstance(v, (tuple, list)) else (v, None))
        st = 1.0 if st is None else st
        a = rot + (out[p][2] if p else 0.0)
        s = out[p][1] if p else (0.0, 0.0)
        L = b['Length'] * st
        out[n] = (s, (s[0] + L * math.cos(a), s[1] + L * math.sin(a)), a)
    if weapon:
        par, ang, L = weapon
        s, _, pa = out[par][1], None, out[par][2]
        a = pa + ang
        out['__weapon'] = (s, (s[0] + L * math.cos(a), s[1] + L * math.sin(a)), a)
    return out


def lowest_y(seg, prof):
    return max(seg[n][1][1] for n in prof['drawn'] if n in seg)


# ---- rendering -----------------------------------------------------------------------

def draw_figure(dr, seg, prof, ox, oy, s, width, after_core=None):
    """Far limbs, then core (torso + head disc), then near limbs, then weapon."""
    P = lambda q: (ox + q[0] * s, oy + q[1] * s)
    order = ([n for n in prof['drawn'] if prof['sides'][n] == 'far'] +
             [n for n in prof['drawn'] if prof['sides'][n] == 'core'] +
             [n for n in prof['drawn'] if prof['sides'][n] == 'near'])
    n_back = sum(1 for n in order if prof['sides'][n] != 'near')
    for k, n in enumerate(order):
        if k == n_back and after_core:
            after_core(dr, P)
        a, b, _ = seg[n]
        col = prof['colors'][n]
        if n == prof['head']:
            c = P(((a[0] + b[0]) / 2, (a[1] + b[1]) / 2))
            L = math.dist(a, b) * s
            r = L * 0.55
            dr.ellipse([c[0] - r, c[1] - r, c[0] + r, c[1] + r], fill=col)
            continue
        dr.line([P(a), P(b)], fill=col, width=width)
        for q in (P(a), P(b)):
            w = width / 2
            dr.ellipse([q[0] - w, q[1] - w, q[0] + w, q[1] + w], fill=col)
    if '__weapon' in seg:
        a, b, _ = seg['__weapon']
        dr.line([P(a), P(b)], fill=prof['weapon']['color'], width=max(2, width * 2 // 3))


def render_cell(rig, prof, pose, size=256, weapon=None, label=None, ground_frac=0.86):
    im = Image.new('RGB', (size, size), 'white')
    dr = ImageDraw.Draw(im)
    seg = fk(rig, pose, weapon)
    s = size / 64.0
    ground = size * ground_frac
    dr.line([(8, ground), (size - 8, ground)], fill=GROUND, width=max(2, size // 128))
    draw_figure(dr, seg, prof, size / 2, ground - lowest_y(seg, prof) * s, s, max(3, size // 32))
    if label:
        dr.text((6, 4), label, fill=(120, 120, 120))
    return im


def render_sheet(rig, prof, poses, cols=4, size=256, weapons=None, labels=True):
    rows = (len(poses) + cols - 1) // cols
    sheet = Image.new('RGB', (cols * size, rows * size), 'white')
    dr = ImageDraw.Draw(sheet)
    for i, pose in enumerate(poses):
        x, y = (i % cols) * size, (i // cols) * size
        w = weapons[i] if weapons else None
        sheet.paste(render_cell(rig, prof, pose, size, w, str(i + 1) if labels else None), (x, y))
        dr.rectangle([x, y, x + size - 1, y + size - 1], outline=(200, 200, 200), width=2)
    return sheet


def render_legend(prof, with_weapon=True):
    items = [(prof['colors'][n], prof['labels'][n]) for n in prof['drawn'] if n != prof['head']]
    items = [i for k, i in enumerate(items) if i not in items[:k]]
    if with_weapon:
        items.append((prof['weapon']['color'], 'weapon (sword/knife blade)'))
    im = Image.new('RGB', (420, 16 + 30 * len(items)), 'white')
    dr = ImageDraw.Draw(im)
    for k, (c, l) in enumerate(items):
        y = 10 + k * 30
        dr.line([(12, y + 8), (72, y + 8)], fill=c, width=12)
        dr.text((86, y + 2), f'{l}   rgb{tuple(c)}', fill='black')
    return im
