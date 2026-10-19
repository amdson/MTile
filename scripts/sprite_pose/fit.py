"""Sprite-cell -> rig pose: a whole-skeleton fit, rig-agnostic.

1. Observe: classify pixels into the profile's bone colors (largest blob each), the dark
   core (torso + head disc, head = thickest dark disc), and the weapon color.
2. Fit: unknowns = root pixel position, px-per-unit scale, local rotation of every drawn
   bone. Undrawn bones hold the reference pose. Residuals are two-sided chamfer distances
   between each bone's predicted segment (rig FK at TRUE lengths) and its observed pixels,
   plus the head-disc center. Connectivity and lengths are therefore exact by construction.
3. Escape wrong-end minima: per limb chain, retry with the upper/lower bones flipped.
4. Flag what a human/agent should look at.

Everything here is pixels in, rotations + diagnostics out; mapping-file overrides
(see mapping.py) are applied by the caller through `pins`, `skip`, and `init`.
"""
import math
import numpy as np
from scipy import ndimage
from scipy.optimize import least_squares
from rig import fk

SAMPLES = 40  # observed points kept per bone


def wrap(x):
    return (x + math.pi) % (2 * math.pi) - math.pi


# ---- observation ---------------------------------------------------------------------

def observe(cell, prof):
    a = np.asarray(cell.convert('RGB'), float)
    H, W, _ = a.shape
    mx, mn = a.max(2), a.min(2)
    sat = mx - mn
    limbs = [n for n in prof['drawn'] if prof['sides'][n] != 'core']
    names = limbs + ['__weapon']
    pal = np.array([prof['colors'][n] for n in limbs] + [prof['weapon']['color']], float)
    d = ((a[:, :, None, :] - pal[None, None]) ** 2).sum(-1)
    lab = d.argmin(2)
    ok = (sat > 80) & (d.min(2) < 75 ** 2)
    obs = {'size': (W, H), 'lab': lab, 'ok': ok, 'names': names, 'bones': {}}
    for i, n in enumerate(names):
        m = ok & (lab == i)
        cc, k = ndimage.label(m)
        if k > 1:
            m = cc == (np.argmax(ndimage.sum(m, cc, range(1, k + 1))) + 1)
        ys, xs = np.nonzero(m)
        if len(xs) < 25:
            obs['bones'][n] = None
            continue
        pts = np.stack([xs, ys], 1).astype(float)
        c = pts.mean(0)
        _, _, vt = np.linalg.svd(pts - c, full_matrices=False)
        v = vt[0]
        pr = (pts - c) @ v
        e0, e1 = c + v * np.percentile(pr, 1), c + v * np.percentile(pr, 99)
        sub = pts[np.linspace(0, len(pts) - 1, min(SAMPLES, len(pts))).astype(int)]
        obs['bones'][n] = {'pts': sub, 'n': len(pts), 'ends': (e0, e1), 'len_px': float(np.linalg.norm(e1 - e0))}
    # dark core: torso line + head disc
    dark = (mx < 110) & (sat < 70)
    cc, k = ndimage.label(dark)
    if k:
        sizes = ndimage.sum(dark, cc, range(1, k + 1))
        keep = [i + 1 for i, s in enumerate(sizes) if s >= 0.15 * sizes.max()]
        dark = np.isin(cc, keep)
    obs['dark'] = dark
    if dark.sum() < 50:
        obs['head'] = obs['torso'] = None
        return obs
    # head = the thickest dark disc near one END of the dark shape's long axis (a thick
    # torso can be as wide as the head, but the head is always at an extremity)
    ink = mn < 225
    dt = ndimage.distance_transform_edt(ink)
    ys, xs = np.nonzero(dark)
    pts = np.stack([xs, ys], 1).astype(float)
    c = pts.mean(0)
    _, _, vt = np.linalg.svd(pts - c, full_matrices=False)
    pr = (pts - c) @ vt[0]
    best = None
    for sel in (pr <= np.percentile(pr, 35), pr >= np.percentile(pr, 65)):
        q = pts[sel].astype(int)
        v = dt[q[:, 1], q[:, 0]]
        k = int(v.argmax())
        if best is None or v[k] > best[1]:
            best = (q[k], float(v[k]))
    obs['head'] = {'c': np.array(best[0], float), 'r': best[1]}
    r = best[1]
    ys, xs = np.nonzero(dark)
    pts = np.stack([xs, ys], 1).astype(float)
    far = np.linalg.norm(pts - obs['head']['c'], axis=1) > 1.35 * r
    tp = pts[far]
    if len(tp) < 15:
        obs['torso'] = None
        return obs
    c = tp.mean(0)
    _, _, vt = np.linalg.svd(tp - c, full_matrices=False)
    v = vt[0]
    pr = (tp - c) @ v
    e0, e1 = c + v * np.percentile(pr, 2), c + v * np.percentile(pr, 98)
    neck, hip = (e0, e1) if np.linalg.norm(e0 - obs['head']['c']) < np.linalg.norm(e1 - obs['head']['c']) else (e1, e0)
    sub = tp[np.linspace(0, len(tp) - 1, min(SAMPLES, len(tp))).astype(int)]
    obs['torso'] = {'pts': sub, 'neck': neck, 'hip': hip}
    return obs


# ---- model ---------------------------------------------------------------------------

def seg_dist(p, a, b):
    """Distance from points p (N,2) to segment a-b."""
    ab = b - a
    t = np.clip(((p - a) @ ab) / max(ab @ ab, 1e-9), 0, 1)
    return np.linalg.norm(p - (a + t[:, None] * ab), axis=1)


class Model:
    def __init__(self, rig, prof, ref_pose, obs, pins=None, skip=()):
        self.rig, self.prof, self.obs = rig, prof, obs
        self.ref = dict(ref_pose)
        self.drawn = [n for n in prof['drawn'] if n != prof['head']]
        self.free = list(self.drawn)  # pinned bones stay free; the pin is a residual on the tip
        self.pins = pins or {}          # bone -> (x, y) pixel the bone's tip must reach
        self.skip = set(skip)           # bones whose observation is ignored (occluded)
        self.scale0 = None

    def pose(self, x):
        p = dict(self.ref)
        for i, n in enumerate(self.free):
            old = p.get(n)
            st = old[1] if isinstance(old, (tuple, list)) else None
            p[n] = (x[3 + i], st)
        return p

    def segs(self, x):
        return fk(self.rig, self.pose(x)), np.array([x[0], x[1]]), x[2]

    def residuals(self, x):
        seg, o, s = self.segs(x)
        P = lambda q: o + s * np.asarray(q)
        res = []
        ob = self.obs
        for n in self.drawn:
            b = ob['bones'].get(n) if self.prof['sides'][n] != 'core' else None
            a_, t_ = P(seg[n][0]), P(seg[n][1])
            if self.prof['sides'][n] == 'core':
                if ob.get('torso') is not None:
                    res.append(seg_dist(ob['torso']['pts'], a_, t_) / s)
                    for q in np.linspace(0, 1, 6):
                        pt = a_ + q * (t_ - a_)
                        res.append([np.min(np.linalg.norm(ob['torso']['pts'] - pt, axis=1)) / s])
                continue
            if b is None or n in self.skip:
                continue
            w = 1.0
            res.append(w * seg_dist(b['pts'], a_, t_) / s)
            for q in np.linspace(0, 1, 8):
                pt = a_ + q * (t_ - a_)
                res.append([w * np.min(np.linalg.norm(b['pts'] - pt, axis=1)) / s])
        if self.prof['head'] and ob.get('head') is not None:
            hs, ht = P(seg[self.prof['head']][0]), P(seg[self.prof['head']][1])
            res.append(2.0 * (0.5 * (hs + ht) - ob['head']['c']) / s)
        for n, px in self.pins.items():
            res.append(4.0 * (P(seg[n][1]) - np.asarray(px)) / s)
        if self.scale0:  # weak prior: keep the figure's size near the torso/sheet estimate
            res.append([2.0 * math.log(x[2] / self.scale0)])
        return np.concatenate([np.atleast_1d(np.asarray(r, float)) for r in res])

    def cost(self, x):
        r = self.residuals(x)
        return float(np.mean(np.minimum(r ** 2, 9.0)))


# ---- initialisation ------------------------------------------------------------------

def init_guess(model, prev=None):
    """Root at the observed hip, scale from the torso, each bone along its PCA axis,
    oriented away from its parent joint as predicted so far (chain order)."""
    rig, prof, ob = model.rig, model.prof, model.obs
    bm = {b['Name']: b for b in rig['Bones']}
    chest_len = bm['chest']['Length'] if 'chest' in bm else 16.0
    if ob.get('torso') is not None:
        hip, neck = ob['torso']['hip'], ob['torso']['neck']
        s = max(np.linalg.norm(neck - hip) / chest_len, 0.5)
    else:
        W, H = ob['size']
        hip, s = np.array([W / 2, H * 0.55]), W / 64.0
    if prev is not None:
        def ref_rot(n):
            v = model.ref.get(n, bm[n]['Rotation'])
            return v[0] if isinstance(v, (tuple, list)) else v
        return np.array([hip[0], hip[1], s] + [prev.get(n, ref_rot(n)) for n in model.free], float)
    pose = dict(model.ref)
    if ob.get('torso') is not None:
        wa = math.atan2(*(neck - hip)[::-1])
        pose['chest'] = (wa, None)
    x = np.array([hip[0], hip[1], s] + [0.0] * len(model.free))
    for i, n in enumerate(model.free):
        v = pose.get(n, bm[n]['Rotation'])
        x[3 + i] = v[0] if isinstance(v, (tuple, list)) else v
    # chain order = rig order (parents before children)
    for i, n in enumerate(model.free):
        b = ob['bones'].get(n) if prof['sides'][n] != 'core' else None
        if b is None:
            continue
        seg, o, sc = model.segs(x)
        start = o + sc * np.asarray(seg[n][0])
        e0, e1 = b['ends']
        far = e1 if np.linalg.norm(e0 - start) < np.linalg.norm(e1 - start) else e0
        world = math.atan2(*(far - start)[::-1])
        parent_world = seg[n][2] - x[3 + i]
        x[3 + i] = wrap(world - parent_world)
    return x


def limb_chains(prof, rig):
    bm = {b['Name']: b for b in rig['Bones']}
    chains = []
    for n in prof['drawn']:
        if prof['sides'][n] == 'core':
            continue
        p = bm[n].get('Parent')
        while p and p not in prof['drawn']:
            p = bm[p].get('Parent')
        if p is None or prof['sides'].get(p) == 'core':
            ch = [n]
            while True:
                kids = [c for c in prof['drawn'] if bm[c].get('Parent') == ch[-1]]
                if not kids:
                    break
                ch.append(kids[0])
            chains.append(ch)
    return chains


ARM_LIMIT = (-2.6, 0.15)    # elbows bend one way, to ~150° (authored arms sit at lower ≈ -0.5..-0.9)


def joint_limits(b):
    """(min, max) local rotation for a hinge bone: the rig's own limits (knees), else the
    elbow convention for '*arm*lower' bones; None for free bones."""
    if b.get('MinRotation') is not None:
        return b['MinRotation'], b['MaxRotation']
    if 'arm' in b['Name'] and b['Name'].endswith('lower'):
        return ARM_LIMIT
    return None


# ---- solve ---------------------------------------------------------------------------

def solve(rig, prof, ref_pose, obs, prev=None, scale0=None, pins=None, skip=(), init=None):
    m = Model(rig, prof, ref_pose, obs, pins, skip)
    m.scale0 = scale0
    lo = np.full(3 + len(m.free), -np.inf)
    hi = np.full(3 + len(m.free), np.inf)
    lo[2], hi[2] = 0.3, 40.0
    # joint limits are HARD bounds: an elbow/knee folded past its range is never the right
    # reading of a drawing (it is how hidden upper arms used to decode), so the solver may
    # not go there. Angles are local; limited bones get a window, the rest stay unbounded.
    bm = {b['Name']: b for b in rig['Bones']}
    for i, n in enumerate(m.free):
        lim = joint_limits(bm[n])
        if lim:
            lo[3 + i], hi[3 + i] = lim[0] - 0.05, lim[1] + 0.05

    def run(x0):
        x0 = x0.copy()
        for k in range(3, len(x0)):
            if np.isfinite(lo[k]):
                v = wrap(x0[k])
                x0[k] = min(max(v, lo[k] + 1e-3), hi[k] - 1e-3)
        x0[2] = min(max(x0[2], lo[2] + 1e-3), hi[2] - 1e-3)
        r = least_squares(m.residuals, x0, bounds=(lo, hi), loss='soft_l1', f_scale=1.5, max_nfev=150)
        return r.x, m.cost(r.x)

    starts = [init_guess(m)]
    if obs.get('torso') is not None:
        t = obs['torso']
        obs_rev = dict(obs, torso=dict(t, neck=t['hip'], hip=t['neck']))
        m.obs = obs_rev
        starts.append(init_guess(m))
        m.obs = obs
    s0 = starts[0][2]
    m.scale0 = scale0 or s0
    lo[2], hi[2] = 0.6 * m.scale0, 1.7 * m.scale0
    if prev is not None:
        starts.append(init_guess(m, prev))
    if init is not None:
        starts.append(init_guess(m, init))
    best = min((run(x0) for x0 in starts), key=lambda t: t[1])
    # escape wrong-end minima chain by chain
    idx = {n: 3 + i for i, n in enumerate(m.free)}
    for ch in limb_chains(prof, rig):
        ch = [n for n in ch if n in idx]
        for flips in ([ch[0]], ch[1:2], ch[:2]):
            if not flips:
                continue
            x = best[0].copy()
            for k, n in enumerate(ch):
                if n in flips:
                    x[idx[n]] = wrap(x[idx[n]] + math.pi)
                    # keep the next bone's world angle: compensate its local rotation
                    if k + 1 < len(ch) and ch[k + 1] not in flips:
                        x[idx[ch[k + 1]]] = wrap(x[idx[ch[k + 1]]] - math.pi)
            cand = run(x)
            if cand[1] < best[1] - 1e-4:
                best = cand
    x, c = best
    pose = {n: wrap(x[3 + i]) for i, n in enumerate(m.free)}
    for n, v in m.pins.items():
        pass
    return {'x': x, 'pose': pose, 'cost': c, 'model': m}


# ---- weapon + diagnostics -----------------------------------------------------------

def weapon_angle(sol, prof):
    """Local angle of the observed blade relative to its parent bone, or None."""
    m = sol['model']
    b = m.obs['bones'].get('__weapon')
    par = prof['weapon']['parent']
    if b is None or not par:
        return None
    seg, o, s = m.segs(sol['x'])
    hand = o + s * np.asarray(seg[par][1])
    e0, e1 = b['ends']
    base, tip = (e0, e1) if np.linalg.norm(e0 - hand) < np.linalg.norm(e1 - hand) else (e1, e0)
    if np.linalg.norm(base - hand) > 0.6 * np.linalg.norm(tip - base) + 6 * s:
        return None  # blade not attached to the hand: don't trust it
    return wrap(math.atan2(*(tip - base)[::-1]) - seg[par][2])





def diagnose(sol, rig, prof, prev_pose=None):
    """Returns (flags, notes, per_bone). Flags mean "look at this frame" (on the synthetic
    benchmark every badly decoded pose raised one); notes are context (a partly hidden or
    short-drawn bone) that is usually harmless."""
    m, x = sol['model'], sol['x']
    seg, o, s = m.segs(x)
    bm = {b['Name']: b for b in rig['Bones']}
    flags, notes, per_bone = [], [], {}
    for n in m.drawn:
        if prof['sides'][n] == 'core' or n in m.skip:
            continue
        b = m.obs['bones'].get(n)
        if b is None:
            notes.append(f'hidden:{n}')
            continue
        a_, t_ = o + s * np.asarray(seg[n][0]), o + s * np.asarray(seg[n][1])
        fit = float(np.median(seg_dist(b['pts'], a_, t_)) / s)
        ratio = b['len_px'] / (s * bm[n]['Length'])
        per_bone[n] = {'fit': round(fit, 2), 'drawn_len_ratio': round(ratio, 2)}
        if fit > 1.8:
            flags.append(f'poor_fit:{n}({fit:.1f})')
        if ratio < 0.35:
            flags.append(f'foreshortened:{n}({ratio:.2f})')
        elif ratio < 0.6:
            notes.append(f'short:{n}({ratio:.2f})')
    for n, v in sol['pose'].items():
        b = bm[n]
        lim = joint_limits(b)
        lo_, hi_ = lim if lim else (None, None)
        if lo_ is not None and not (lo_ - 0.3 <= v <= hi_ + 0.3):
            flags.append(f'joint_limit:{n}({v:.2f})')
        elif lo_ is not None:
            fold = lo_ if abs(lo_) > abs(hi_) else hi_  # the full-flexion side of the hinge
            if abs(v - fold) < 0.12:
                # pinned at max flexion by the solver's bound: usually a limb drawn shorter than
                # the rig's (hidden/foreshortened upper arm) folding to match - look at it
                flags.append(f'at_limit:{n}({v:.2f})')
        if prev_pose and n in prev_pose and abs(wrap(v - prev_pose[n])) > 2.5:
            flags.append(f'jump:{n}({wrap(v - prev_pose[n]):+.2f})')
    if m.obs.get('torso') is None:
        flags.append('missing:torso')
    return flags, notes, per_bone
