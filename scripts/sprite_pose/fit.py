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
import copy
from rig import fk
import limb3d

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
        if k > 1:  # drop anti-aliasing specks, keep pieces of a stroke split by an occluder
            sizes = ndimage.sum(m, cc, range(1, k + 1))
            m = np.isin(cc, [i + 1 for i, sz in enumerate(sizes) if sz >= 0.15 * sizes.max()])
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


W_PHI, W_PSI, W_TEMPORAL = 1.2, 1.0, 0.8   # priors: planar unless the drawing says otherwise; smooth in time
W_PIN_MARKED, W_DATA_MARKED = 12.0, 0.35     # agent-marked frame: the marks lead, strokes refine
PHI_MAX, PSI_MAX = 1.2, 1.4                   # tilt <= ~69 deg (Stretch >= 0.36)


class Model:
    """mode3d: limb chains are ball (upper) + hinge (lower) joints with an out-of-plane tilt
    phi and hinge swivel psi per chain (limb3d.py); the clip receives the projection as
    Rotation + Stretch. `lengths` = per-sheet drawn-proportion factors (GPT's style, not
    foreshortening): the fit matches the image with them, the clip keeps the rig's lengths."""

    def __init__(self, rig, prof, ref_pose, obs, pins=None, skip=(), mode3d=False, lengths=None):
        self.rig, self.prof, self.obs = rig, prof, obs
        self.rig_fit = copy.deepcopy(rig)
        for bn in self.rig_fit['Bones']:
            bn['Length'] *= (lengths or {}).get(bn['Name'], 1.0)
        self.ref = dict(ref_pose)
        self.drawn = [n for n in prof['drawn'] if n != prof['head']]
        self.free = list(self.drawn)  # pinned bones stay free; the pin is a residual on the tip
        # bone -> (x, y) pixel the bone's TIP must reach ('__root' = the root/hip pixel). An
        # agent's joint marks arrive here; 'hidden' entries and the blade tip are not pins.
        self.pins = {k: v for k, v in (pins or {}).items()
                     if isinstance(v, (list, tuple)) and len(v) == 2 and k != '__blade'}
        self.skip = set(skip)           # bones whose observation is ignored (occluded)
        self.scale0 = None
        self.mode3d = mode3d
        self.chains = [c[:2] for c in limb_chains(prof, rig) if len(c) >= 2] if mode3d else []
        self.n_base = 3 + len(self.free)
        self.occ = self._occluders()
        self.prev3d = None              # {upper: (phi, psi)} of the previous frame
        self.anchored = False           # scale0 is the sheet's running median, not a guess
        # an agent-marked frame: its reading leads, pixels only refine locally
        self.marked = '__root' in self.pins or len(self.pins) >= 3

    def _occluders(self):
        """Pixels drawn in front of each limb bone: draw order is far limbs < torso/head <
        near limbs < weapon (the renderer's and the prompt's layering). A predicted point
        of a bone that lands on one of these is hidden, not missing."""
        ob = self.obs
        if 'lab' not in ob:
            return {}
        layer = lambda n: {'far': 0, 'core': 1, 'near': 2}[self.prof['sides'][n]]
        names = ob['names']
        occ = {}
        for n in self.drawn:
            if self.prof['sides'][n] == 'core':
                continue
            L = layer(n)
            front = [i for i, m in enumerate(names) if m == '__weapon' or (m != n and m in self.prof['sides'] and layer(m) > L)]
            mask = np.isin(ob['lab'], front) & ob['ok']
            if L < 1 and ob.get('dark') is not None:
                mask = mask | ob['dark']
            occ[n] = ndimage.binary_dilation(mask, iterations=2)
        return occ

    def hidden(self, n, pt):
        m = self.occ.get(n)
        if m is None:
            return False
        x, y = int(round(pt[0])), int(round(pt[1]))
        return 0 <= y < m.shape[0] and 0 <= x < m.shape[1] and bool(m[y, x])

    def nparams(self):
        return self.n_base + 2 * len(self.chains)

    def extras(self, x, k):
        if len(x) < self.nparams():
            return 0.0, 0.0  # initialisation runs on the planar base vector
        return x[self.n_base + 2 * k], x[self.n_base + 2 * k + 1]

    def project(self, x):
        """-> (pose dict for FK on rig_fit, stretch {bone: s}, depth {bone: z})."""
        p = dict(self.ref)
        for i, n in enumerate(self.free):
            old = p.get(n)
            st = old[1] if isinstance(old, (tuple, list)) else None
            p[n] = (x[3 + i], st)
        stretch, depth = {}, {}
        if self.chains:
            seg = fk(self.rig_fit, p)
            idx = {n: 3 + i for i, n in enumerate(self.free)}
            for k, (u, l) in enumerate(self.chains):
                phi, psi = self.extras(x, k)
                theta, alpha = x[idx[u]], x[idx[l]]
                parent_world = seg[u][2] - theta
                lu, su, ll, sl, zu, zl = limb3d.project(parent_world, theta, phi, alpha, psi)
                p[u], p[l] = (lu, su), (ll, sl)
                stretch[u], stretch[l], depth[u], depth[l] = su, sl, zu, zl
        return p, stretch, depth

    def pose(self, x):
        return self.project(x)[0]

    def segs(self, x):
        return fk(self.rig_fit, self.pose(x)), np.array([x[0], x[1]]), x[2]

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
                    wt = W_DATA_MARKED if self.marked else 1.0
                    res.append(wt * seg_dist(ob['torso']['pts'], a_, t_) / s)
                    for q in np.linspace(0, 1, 6):
                        pt = a_ + q * (t_ - a_)
                        res.append([wt * np.min(np.linalg.norm(ob['torso']['pts'] - pt, axis=1)) / s])
                continue
            if b is None or n in self.skip:
                continue
            w = W_DATA_MARKED if self.marked else 1.0
            res.append(w * seg_dist(b['pts'], a_, t_) / s)
            for q in np.linspace(0, 1, 8):
                pt = a_ + q * (t_ - a_)
                # a predicted point under something drawn in front is hidden, not missing
                d_ = 0.0 if self.hidden(n, pt) else np.min(np.linalg.norm(b['pts'] - pt, axis=1)) / s
                res.append([w * d_])
        if self.prof['head'] and ob.get('head') is not None:
            hs, ht = P(seg[self.prof['head']][0]), P(seg[self.prof['head']][1])
            res.append(2.0 * (0.5 * (hs + ht) - ob['head']['c']) / s)
        wp = W_PIN_MARKED if self.marked else 4.0
        for n, px in self.pins.items():
            if n == '__root':
                res.append(wp * (o - np.asarray(px)) / s)
            elif n in seg:
                res.append(wp * (P(seg[n][1]) - np.asarray(px)) / s)
        if self.scale0 and not ('__root' in self.pins and 'chest' in self.pins):
            # keep the figure's size near the torso/sheet estimate (hip + neck marks fix it instead)
            res.append([(6.0 if self.anchored else 2.0) * math.log(x[2] / self.scale0)])
        for k, (u, _) in enumerate(self.chains):
            phi, psi = self.extras(x, k)
            res.append([W_PHI * phi, W_PSI * psi])
            if self.prev3d and u in self.prev3d:
                pp, ps = self.prev3d[u]
                res.append([W_TEMPORAL * (phi - pp), W_TEMPORAL * (psi - ps)])
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
    pins = model.pins
    chest_fit = next((b['Length'] for b in model.rig_fit['Bones'] if b['Name'] == 'chest'), chest_len)
    if '__root' in pins and 'chest' in pins:  # the agent's reading wins over the torso detector
        hip, neck = np.asarray(pins['__root'], float), np.asarray(pins['chest'], float)
        s = max(np.linalg.norm(neck - hip) / chest_fit, 0.5)
    elif ob.get('torso') is not None:
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
    if ob.get('torso') is not None or ('__root' in pins and 'chest' in pins):
        wa = math.atan2(*(neck - hip)[::-1])
        pose['chest'] = (wa, None)
    x = np.array([hip[0], hip[1], s] + [0.0] * len(model.free))
    for i, n in enumerate(model.free):
        v = pose.get(n, bm[n]['Rotation'])
        x[3 + i] = v[0] if isinstance(v, (tuple, list)) else v
    # chain order = rig order (parents before children)
    for i, n in enumerate(model.free):
        b = ob['bones'].get(n) if prof['sides'][n] != 'core' else None
        if n not in pins and b is None:
            continue
        seg, o, sc = model.segs(x)
        start = o + sc * np.asarray(seg[n][0])
        if n in pins:  # aim straight at the agent's mark
            far = np.asarray(pins[n], float)
        else:
            e0, e1 = b['ends']
            far = e1 if np.linalg.norm(e0 - start) < np.linalg.norm(e1 - start) else e0
        world = math.atan2(*(far - start)[::-1])
        parent_world = seg[n][2] - x[3 + i]
        x[3 + i] = wrap(world - parent_world)
    return x


def limb_chains(prof, rig):
    if rig is None:
        import rig as _R
        rig = _R.load_rig(prof['rig'])
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

def solve(rig, prof, ref_pose, obs, prev=None, scale0=None, pins=None, skip=(), init=None,
          mode3d=False, lengths=None):
    """prev/init: a previous solution's 'params' ({bone: theta-or-alpha, '__3d': {upper: (phi, psi)}})."""
    m = Model(rig, prof, ref_pose, obs, pins, skip, mode3d, lengths)
    m.scale0 = scale0
    if prev and mode3d:
        m.prev3d = prev.get('__3d')
    lo = np.full(m.nparams(), -np.inf)
    hi = np.full(m.nparams(), np.inf)
    lo[2], hi[2] = 0.3, 40.0
    # joint limits are HARD bounds: an elbow/knee folded past its range is never the right
    # reading of a drawing (it is how hidden upper arms used to decode), so the solver may
    # not go there. Angles are local; limited bones get a window, the rest stay unbounded.
    bm = {b['Name']: b for b in rig['Bones']}
    for i, n in enumerate(m.free):
        lim = joint_limits(bm[n])
        if lim:
            lo[3 + i], hi[3 + i] = lim[0] - 0.05, lim[1] + 0.05
    for k in range(len(m.chains)):
        lo[m.n_base + 2 * k], hi[m.n_base + 2 * k] = -PHI_MAX, PHI_MAX
        lo[m.n_base + 2 * k + 1], hi[m.n_base + 2 * k + 1] = -PSI_MAX, PSI_MAX

    def seeded(x0, src=None, sign=1.0):
        """Append (phi, psi) per chain: from a previous solution, else seeded off the planar
        saddle from how short the stroke is drawn (phi = acos(drawn/expected))."""
        if len(x0) >= m.nparams():
            return x0
        ext = []
        for (u, l) in m.chains:
            if src and u in src:
                ext += list(src[u])
                continue
            bo = m.obs['bones'].get(u)
            L = next(b_['Length'] for b_ in m.rig_fit['Bones'] if b_['Name'] == u)
            ratio = bo['len_px'] / (x0[2] * L) if bo is not None else 1.0
            phi = math.acos(min(max(ratio, 0.36), 1.0)) if ratio < 0.9 else 0.0
            bl = m.obs['bones'].get(l)
            Ll = next(b_['Length'] for b_ in m.rig_fit['Bones'] if b_['Name'] == l)
            rl = bl['len_px'] / (x0[2] * Ll) if bl is not None else 1.0
            ext += [sign * max(phi, 0.05), sign * (0.6 if rl < 0.85 else 0.05)]
        return np.concatenate([x0, ext])

    def run(x0):
        x0 = seeded(x0, (prev or {}).get('__3d')).copy()
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
    m.anchored = scale0 is not None and not ('__root' in m.pins and 'chest' in m.pins)
    lo[2], hi[2] = ((0.8, 1.25) if m.anchored else (0.6, 1.7))
    lo[2], hi[2] = lo[2] * m.scale0, hi[2] * m.scale0
    if prev is not None:
        starts.append(init_guess(m, prev))
    if init is not None:
        starts.append(init_guess(m, init))
    best = min((run(x0) for x0 in starts), key=lambda t: t[1])
    # escape wrong-end minima chain by chain
    idx = {n: 3 + i for i, n in enumerate(m.free)}
    for kc, (u, l) in enumerate(m.chains):
        for dphi, dpsi in ((1, -1), (-1, 1)):
            x = best[0].copy()
            x[m.n_base + 2 * kc] *= dphi
            x[m.n_base + 2 * kc + 1] *= dpsi
            cand = run(x)
            if cand[1] < best[1] - 1e-4:
                best = cand
    # 3D: the hinge can't flip (joint-limited), so a wrong-end limb is escaped by flipping the
    # upper bone AND re-seeding the hinge at a moderate and a deep bend inside its limits
    for (u, l) in m.chains:
        lim = joint_limits(bm[l])
        if not lim:
            continue
        fold = lim[0] if abs(lim[0]) > abs(lim[1]) else lim[1]
        for bend in (0.35 * fold, 0.7 * fold):
            x = best[0].copy()
            x[idx[u]] = wrap(x[idx[u]] + math.pi)
            x[idx[l]] = bend
            cand = run(x)
            if cand[1] < best[1] - 1e-4:
                best = cand
    for ch in limb_chains(prof, rig):
        ch = [n for n in ch if n in idx]
        for flips in (([ch[0]],) if m.chains else ([ch[0]], ch[1:2], ch[:2])):
            if not flips:
                continue
            x = best[0].copy()
            for k, n in enumerate(ch):
                if n in flips:
                    x[idx[n]] = wrap(x[idx[n]] + math.pi)
                    # keep the next bone's world angle: compensate its local rotation
                    if k + 1 < len(ch) and ch[k + 1] not in flips and not m.chains:
                        x[idx[ch[k + 1]]] = wrap(x[idx[ch[k + 1]]] - math.pi)
            cand = run(x)
            if cand[1] < best[1] - 1e-4:
                best = cand
    x, c = best
    p, stretch, depth = m.project(x)
    pose = {n: wrap(p[n][0]) for n in m.free}
    params = {n: float(x[3 + i]) for i, n in enumerate(m.free)}
    params['__3d'] = {u: (float(x[m.n_base + 2 * k]), float(x[m.n_base + 2 * k + 1]))
                      for k, (u, l) in enumerate(m.chains)}
    return {'x': x, 'pose': pose, 'stretch': stretch, 'depth': depth, 'params': params,
            'cost': c, 'model': m}


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
        modelled = float(np.linalg.norm(t_ - a_))
        ratio = b['len_px'] / max(modelled, 1e-6)
        per_bone[n] = {'fit': round(fit, 2), 'drawn_len_ratio': round(ratio, 2)}
        if fit > 1.8:
            flags.append(f'poor_fit:{n}({fit:.1f})')
        if ratio < 0.35:
            flags.append(f'foreshortened:{n}({ratio:.2f})')
        elif ratio < 0.6:
            notes.append(f'short:{n}({ratio:.2f})')
    for n, st in sol.get('stretch', {}).items():
        if st < 0.85:
            notes.append(f'tilted:{n}({st:.2f})')
    hinge = sol.get('params', {})
    for n, v in sol['pose'].items():
        b = bm[n]
        if prev_pose and n in prev_pose and abs(wrap(v - prev_pose[n])) > 2.5:
            flags.append(f'jump:{n}({wrap(v - prev_pose[n]):+.2f})')  # projected vs projected
        if m.chains and any(n == l for _, l in m.chains):
            v = hinge.get(n, v)  # judge the hinge's own flexion, not its projection
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
    if m.obs.get('torso') is None:
        flags.append('missing:torso')
    return flags, notes, per_bone
