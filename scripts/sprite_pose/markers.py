"""Detect the joint labels the image model was asked to draw (rig.draw_markers).

'dots'   - a bone's tip is the thickest point of its colour (strokes are thin, dots wide);
'shapes' - elbows/knees are hollow rings: the tip is the centre of the hole the ring encloses;
           hands/feet are dots, as above.
The blade tip is the thickest violet point. Hip and neck come from the torso line's ends.
Found joints become FIXED tips in the frame state; missing ones stay free for the solver.
"""
import numpy as np
from scipy import ndimage
import fit as F

JUNCTION_GAP_PX = 14  # upper/lower stroke ends this close = a visible elbow/knee


def _dot(mask, min_ratio=1.5):
    if mask.sum() < 12:
        return None
    dt = ndimage.distance_transform_edt(mask)
    stroke = np.percentile(dt[mask], 60)
    if dt.max() < max(min_ratio * stroke, 3.0):
        return None
    y, x = np.unravel_index(dt.argmax(), dt.shape)
    return [int(x), int(y)]


def _ring(mask):
    holes = ndimage.binary_fill_holes(mask) & ~mask
    lab, k = ndimage.label(holes)
    if not k:
        return None
    sizes = ndimage.sum(holes, lab, range(1, k + 1))
    i = int(np.argmax(sizes))
    if sizes[i] < 6:
        return None
    y, x = ndimage.center_of_mass(holes, lab, i + 1)
    return [int(round(x)), int(round(y))]


def detect(cell, prof, style):
    """-> (marks {bone|'__root'|'__blade': [x, y]}, missing [labels])."""
    obs = F.observe(cell, prof)
    names = obs['names']
    marks, missing = {}, []
    t = obs.get('torso')
    if t is not None:
        marks['__root'] = [int(round(v)) for v in t['hip']]
        marks['chest'] = [int(round(v)) for v in t['neck']]
    else:
        missing += ['hip', 'neck']
    for i, n in enumerate(names):
        mask = obs['ok'] & (obs['lab'] == i)
        if n == '__weapon':
            p = _dot(mask)
            if p:
                marks['__blade'] = p
            else:
                missing.append('blade tip')
            continue
        if style == 'ends' and n.endswith('upper'):
            continue  # elbows/knees: from the colour junction, below
        p = _ring(mask) if (style == 'shapes' and n.endswith('upper')) else None
        p = p or _dot(mask)
        if p:
            marks[n] = p
        else:
            missing.append(n)
    if style == 'ends':
        # elbow/knee = where the upper and lower strokes meet; fixed only when they clearly
        # touch, otherwise left free for the solver's IK (fixed hand/foot + joint limits)
        for ch in F.limb_chains(prof, None):
            if len(ch) < 2:
                continue
            u, l = ch[0], ch[1]
            su, sl = obs['bones'].get(u), obs['bones'].get(l)
            if su is None or sl is None:
                missing.append(f'{u} (free)')
                continue
            d_, i, j = min((np.linalg.norm(a - b), i, j) for i, a in enumerate(su['ends']) for j, b in enumerate(sl['ends']))
            if d_ <= JUNCTION_GAP_PX:
                marks[u] = [int(round(v)) for v in (su['ends'][i] + sl['ends'][j]) / 2]
            else:
                missing.append(f'{u} (free)')
    return marks, missing


def to_state(marks, prev_state=None):
    """Fixed tips for every detected joint; keeps any agent-made entries for other bones."""
    st = dict(prev_state or {})
    bones = dict(st.get('bones', {}))
    if '__root' in marks:
        st['root'] = {'pos': marks['__root'], 'fixed': True}
    for k, v in marks.items():
        if k in ('__root', '__blade'):
            continue
        bones[k] = {'tip': v, 'fixed': ['tip']}
    st['bones'] = bones
    if '__blade' in marks:
        st['knife'] = {'tip': marks['__blade'], 'fixed': ['tip']}
    return st
