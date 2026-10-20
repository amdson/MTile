"""Hard-masked frame solver: the agent fixes what it is sure of, the solver fills the rest.

A frame STATE (state.json, one per frame) is both the solver's output and the agent's input:

    {"root":  {"pos": [165, 185], "fixed": true},
     "scale": {"value": 4.6},
     "bones": {"chest":       {"rot": -1.52, "fixed": ["rot"]},
               "arm_r_upper": {"tip": [169, 136], "fixed": ["tip"]},
               "arm_r_lower": {"tip": [219, 125], "fixed": ["tip"], "depth": "front"},
               "arm_l_upper": {"rot": 2.4, "stretch": 0.7},
               "leg_l_upper": {"hidden": true}},
     "knife": {"tip": [266, 98], "fixed": ["tip"]},
     "reject": false}

Fixed values are never optimised:
- fixed rot / stretch are constants;
- a fixed TIP is eliminated: every evaluation derives the bone's rotation and Stretch from
  wherever its parent joint currently is to the marked pixel, so it lands exactly.
Only free values are parameters. A fixed hand under a free elbow is therefore IK: the
solver places the elbow so both bones keep a plausible Stretch (<= 1, foreshortening paid as
its out-of-screen angle acos(s)) and the hinge stays inside its joint limits, with the
stroke pixels as a soft guide. Contradictory fixes are flagged, never silently averaged.
"""
import copy, math
import numpy as np
from scipy.optimize import least_squares
import fit as F

S_MIN = 0.36            # Stretch floor (out-of-screen tilt <= ~69 deg)
W_TILT = 1.2            # free Stretch pays its out-of-screen angle acos(s)
W_OVER = 10.0           # derived Stretch above 1 / below S_MIN
W_LIMIT = 10.0          # derived hinge outside its joint limits
W_DERIVED_ONE = 0.5     # derived Stretch gently toward 1 (sets the scale when everything is fixed)
W_HIDDEN = 1.0          # hidden bone stays near its warm-start value
W_DATA_DERIVED = 0.5    # strokes of a tip-fixed bone still inform its (free) parent joint


def wrap(a):
    return (a + math.pi) % (2 * math.pi) - math.pi


def fixed(entry, key):
    return entry is not None and key in (entry.get('fixed') or [])


class Frame:
    def __init__(self, rig, prof, ref, obs, state=None, warm=None, lengths=None, scale0=None, stretch_free=True):
        self.rig, self.prof, self.obs = rig, prof, obs
        self.rf = copy.deepcopy(rig)
        for b in self.rf['Bones']:
            b['Length'] *= (lengths or {}).get(b['Name'], 1.0)
        self.bm = {b['Name']: b for b in self.rf['Bones']}
        self.ref = dict(ref)
        self.state = state or {}
        self.warm = warm or {}
        self.scale0 = scale0
        self.sb = self.state.get('bones', {})
        self.head = prof['head']
        self.drawn = [n for n in prof['drawn'] if n != self.head]
        self.limbs = [n for n in self.drawn if prof['sides'][n] != 'core']
        root = self.state.get('root') or {}
        scale = self.state.get('scale') or {}
        self.root_fixed = np.asarray(root['pos'], float) if root.get('fixed') else None
        self.scale_fixed = float(scale['value']) if scale.get('fixed') else None
        if self.scale_fixed is None and self.root_fixed is not None:
            # with most tips fixed the scale is otherwise nearly free (a bigger figure just
            # foreshortens every bone): read it off the marks - the 80th percentile of marked
            # length / bone length over bones whose start joint is also marked
            tips = {'__root': self.root_fixed}
            for b in self.rf['Bones']:
                e = self.sb.get(b['Name'])
                if fixed(e, 'tip'):
                    tips[b['Name']] = np.asarray(e['tip'], float)
            ratios = []
            for b in self.rf['Bones']:
                par, n = b.get('Parent'), b['Name']
                start = tips['__root'] if (par in (None, 'hip') and 'hip' not in tips) else tips.get(par)
                if n in tips and start is not None and b['Length'] > 0 and n in self.drawn:
                    ratios.append(np.linalg.norm(tips[n] - start) / b['Length'])
            if len(ratios) >= 4:
                self.scale_fixed = float(np.percentile(ratios, 80))
        # parameter layout: only FREE values
        self.layout = []
        if self.root_fixed is None:
            self.layout += [('root', 0), ('root', 1)]
        if self.scale_fixed is None:
            self.layout.append(('scale', None))
        for n in self.drawn:
            e = self.sb.get(n)
            if fixed(e, 'tip'):
                continue
            if not fixed(e, 'rot'):
                self.layout.append(('rot', n))
            if stretch_free and n in self.limbs and not fixed(e, 'stretch'):
                self.layout.append(('stretch', n))
        self.occ = self._occluders()

    # ---- geometry -------------------------------------------------------------------
    def ref_val(self, n):
        v = self.ref.get(n, self.bm[n]['Rotation'])
        return (v[0], v[1]) if isinstance(v, (tuple, list)) else (v, None)

    def unpack(self, x):
        p = {k: v for k, v in zip(self.layout, x)}
        root = self.root_fixed if self.root_fixed is not None else np.array([p[('root', 0)], p[('root', 1)]])
        s = self.scale_fixed if self.scale_fixed is not None else p[('scale', None)]
        return p, root, s

    def evaluate(self, x):
        """-> per-bone {start, tip (px), world, rot (local), stretch}, root, scale."""
        p, root, s = self.unpack(x)
        out = {}
        for b in self.rf['Bones']:
            n, par = b['Name'], b.get('Parent')
            start = root if not par else out[par]['tip']
            pw = out[par]['world'] if par else 0.0
            L = b['Length']
            e = self.sb.get(n)
            if fixed(e, 'tip') and L > 0:
                v = np.asarray(e['tip'], float) - start
                world = math.atan2(v[1], v[0])
                st = float(np.linalg.norm(v)) / (s * L)
                rot = wrap(world - pw)
            else:
                r0, s0 = self.ref_val(n)
                rot = e['rot'] if fixed(e, 'rot') else p.get(('rot', n), self.warm.get(n, {}).get('rot', r0))
                if fixed(e, 'stretch'):
                    st = e['stretch']
                elif ('stretch', n) in p:
                    st = p[('stretch', n)]
                else:
                    st = s0 if s0 is not None and n not in self.drawn else 1.0
                world = pw + rot
            tip = start + s * L * st * np.array([math.cos(world), math.sin(world)])
            out[n] = {'start': start, 'tip': tip, 'world': world, 'rot': wrap(rot), 'stretch': st}
        return out, root, s

    # ---- objective -------------------------------------------------------------------
    def _occluders(self):
        tmp = F.Model.__new__(F.Model)
        tmp.obs, tmp.prof, tmp.drawn = self.obs, self.prof, self.drawn
        return F.Model._occluders(tmp)

    def hidden_px(self, n, pt):
        m = self.occ.get(n)
        if m is None:
            return False
        x, y = int(round(pt[0])), int(round(pt[1]))
        return 0 <= y < m.shape[0] and 0 <= x < m.shape[1] and bool(m[y, x])

    def residuals(self, x):
        ev, root, s = self.evaluate(x)
        p, _, _ = self.unpack(x)
        ob, res = self.obs, []
        for n in self.drawn:
            e = self.sb.get(n) or {}
            a_, t_ = ev[n]['start'], ev[n]['tip']
            if self.prof['sides'][n] == 'core':
                if ob.get('torso') is not None and not fixed(e, 'tip'):
                    res.append(F.seg_dist(ob['torso']['pts'], a_, t_) / s)
                    for q in np.linspace(0, 1, 6):
                        pt = a_ + q * (t_ - a_)
                        res.append([np.min(np.linalg.norm(ob['torso']['pts'] - pt, axis=1)) / s])
                continue
            b = ob['bones'].get(n)
            if b is None or e.get('hidden'):
                continue
            w = W_DATA_DERIVED if fixed(e, 'tip') else 1.0
            res.append(w * F.seg_dist(b['pts'], a_, t_) / s)
            for q in np.linspace(0, 1, 8):
                pt = a_ + q * (t_ - a_)
                d_ = 0.0 if self.hidden_px(n, pt) else np.min(np.linalg.norm(b['pts'] - pt, axis=1)) / s
                res.append([w * d_])
        if self.head and ob.get('head') is not None and not fixed(self.sb.get('chest'), 'tip'):
            res.append(2.0 * (0.5 * (ev[self.head]['start'] + ev[self.head]['tip']) - ob['head']['c']) / s)
        pri = []
        for n in self.drawn:
            e = self.sb.get(n) or {}
            st = ev[n]['stretch']
            if fixed(e, 'tip'):
                pri += [W_OVER * max(st - 1.0, 0.0), W_OVER * max(S_MIN - st, 0.0), W_DERIVED_ONE * (1.0 - min(st, 1.0))]
                lim = F.joint_limits(self.bm[n])
                if lim:
                    r = ev[n]['rot']
                    pri.append(W_LIMIT * (max(lim[0] - r, 0.0) + max(r - lim[1], 0.0)))
            elif ('stretch', n) in p:
                pri.append(W_TILT * math.acos(min(max(st, -1.0), 1.0)))
            if e.get('hidden') and n in self.warm:
                pri.append(W_HIDDEN * wrap(ev[n]['rot'] - self.warm[n]['rot']))
        if self.scale_fixed is None and self.scale0:
            pri.append(2.0 * math.log(s / self.scale0))
        res.append(pri)
        return np.concatenate([np.atleast_1d(np.asarray(r, float)) for r in res if len(np.atleast_1d(r))])

    def cost(self, x):
        r = self.residuals(x)
        return float(np.mean(np.minimum(r ** 2, 9.0)))

    # ---- initialisation ----------------------------------------------------------------
    def init(self, use_warm):
        ob = self.obs
        chest_L = self.bm['chest']['Length'] if 'chest' in self.bm else 16.0
        if self.root_fixed is not None:
            hip = self.root_fixed
        elif use_warm and 'root' in self.warm:
            hip = np.asarray(self.warm['root'], float)
        elif ob.get('torso') is not None:
            hip = ob['torso']['hip']
        else:
            hip = np.array([ob['size'][0] / 2, ob['size'][1] * 0.55])
        if self.scale_fixed is not None:
            s = self.scale_fixed
        elif use_warm and 'scale' in self.warm:
            s = self.warm['scale']
        elif ob.get('torso') is not None:
            s = max(np.linalg.norm(ob['torso']['neck'] - ob['torso']['hip']) / chest_L, 0.5)
        else:
            s = ob['size'][0] / 64.0
        x = []
        for kind, key in self.layout:
            if kind == 'root':
                x.append(hip[key])
            elif kind == 'scale':
                x.append(s)
            elif kind == 'rot':
                x.append(self.warm.get(key, {}).get('rot', self.ref_val(key)[0]) if use_warm else self.ref_val(key)[0])
            else:
                x.append(min(max(self.warm.get(key, {}).get('stretch', 1.0), S_MIN), 1.0) if use_warm else 1.0)
        x = np.array(x, float)
        if use_warm:
            return x
        # observation init: aim each free bone along its stroke, away from its start (rig order)
        idx = {k: i for i, k in enumerate(self.layout)}
        if ob.get('torso') is not None and ('rot', 'chest') in idx:
            v = ob['torso']['neck'] - ob['torso']['hip']
            x[idx[('rot', 'chest')]] = math.atan2(v[1], v[0])
        for n in self.limbs:
            if ('rot', n) not in idx or ob['bones'].get(n) is None:
                continue
            ev, _, _ = self.evaluate(x)
            start = ev[n]['start']
            e0, e1 = ob['bones'][n]['ends']
            far = e1 if np.linalg.norm(e0 - start) < np.linalg.norm(e1 - start) else e0
            pw = ev[n]['world'] - ev[n]['rot']
            x[idx[('rot', n)]] = wrap(math.atan2(far[1] - start[1], far[0] - start[0]) - pw)
        return x

    def bounds(self):
        lo, hi = [], []
        for kind, key in self.layout:
            if kind == 'root':
                lo.append(-np.inf); hi.append(np.inf)
            elif kind == 'scale':
                s0 = self.scale0 or 4.0
                lo.append(0.4 * s0 if not self.scale0 else 0.6 * s0); hi.append(2.5 * s0 if not self.scale0 else 1.7 * s0)
            elif kind == 'rot':
                lim = F.joint_limits(self.bm[key])
                lo.append(lim[0] - 0.05 if lim else -np.inf); hi.append(lim[1] + 0.05 if lim else np.inf)
            else:
                lo.append(S_MIN); hi.append(1.0)
        return np.array(lo), np.array(hi)


def solve(rig, prof, ref, obs, state=None, warm=None, lengths=None, scale0=None):
    fr = Frame(rig, prof, ref, obs, state, warm, lengths, scale0)
    lo, hi = fr.bounds()

    def run(x0):
        x0 = np.minimum(np.maximum(x0, lo + 1e-6), hi - 1e-6)
        if not len(x0):
            return x0, fr.cost(x0)
        r = least_squares(fr.residuals, x0, bounds=(lo, hi), loss='soft_l1', f_scale=1.5, max_nfev=200)
        return r.x, fr.cost(r.x)

    starts = [fr.init(False)] + ([fr.init(True)] if warm else [])
    best = min((run(x0) for x0 in starts), key=lambda t: t[1])
    # wrong-end escape for limbs whose upper rotation is free
    idx = {k: i for i, k in enumerate(fr.layout)}
    for ch in F.limb_chains(prof, rig):
        u = ch[0]
        if ('rot', u) not in idx:
            continue
        x = best[0].copy()
        x[idx[('rot', u)]] = wrap(x[idx[('rot', u)]] + math.pi)
        if len(ch) > 1 and ('rot', ch[1]) in idx and not F.joint_limits(fr.bm[ch[1]]):
            x[idx[('rot', ch[1])]] = wrap(x[idx[('rot', ch[1])]] - math.pi)
        cand = run(x)
        if cand[1] < best[1] - 1e-4:
            best = cand
    return fr, best[0], best[1]


def solved_state(fr, x, knife=None):
    """The frame state with every value filled in; the agent's fixed flags are preserved."""
    ev, root, s = fr.evaluate(x)
    st = {'root': {'pos': [round(float(v), 1) for v in root], 'fixed': fr.root_fixed is not None},
          'scale': {'value': round(float(s), 4), 'fixed': fr.scale_fixed is not None}, 'bones': {}}
    for n in fr.drawn + ([fr.head] if fr.head else []):
        e = fr.sb.get(n) or {}
        out = {'rot': round(ev[n]['rot'], 4), 'stretch': round(ev[n]['stretch'], 4),
               'tip': [round(float(v), 1) for v in ev[n]['tip']]}
        if e.get('fixed'):
            out['fixed'] = e['fixed']
        for k in ('hidden', 'depth'):
            if k in e:
                out[k] = e[k]
        st['bones'][n] = out
    if knife is not None:
        st['knife'] = knife
    return st, ev


def blade(fr, ev, s):
    """Knife local angle: from a fixed blade tip, else from the observed violet stroke."""
    par = fr.prof['weapon']['parent']
    if not par:
        return None, None
    hand, pw = ev[par]['tip'], ev[par]['world']
    k = fr.state.get('knife') or {}
    if fixed(k, 'tip'):
        v = np.asarray(k['tip'], float) - hand
        return wrap(math.atan2(v[1], v[0]) - pw), k
    b = fr.obs['bones'].get('__weapon')
    if b is None:
        return None, None
    e0, e1 = b['ends']
    base, tip = (e0, e1) if np.linalg.norm(e0 - hand) < np.linalg.norm(e1 - hand) else (e1, e0)
    if np.linalg.norm(base - hand) > 0.6 * np.linalg.norm(tip - base) + 6 * s:
        return None, None
    return wrap(math.atan2(tip[1] - base[1], tip[0] - base[0]) - pw), {'tip': [round(float(v), 1) for v in tip]}


def diagnose(fr, ev, s, warm=None):
    flags = []
    for n in fr.drawn:
        e = fr.sb.get(n) or {}
        st = ev[n]['stretch']
        if fixed(e, 'tip') and (st > 1.2 or st < 0.25):
            flags.append(f'conflict:{n}(stretch {st:.2f} - mark too far/near for this bone)')
        lim = F.joint_limits(fr.bm[n])
        if lim and not (lim[0] - 0.3 <= ev[n]['rot'] <= lim[1] + 0.3):
            flags.append(f'joint_limit:{n}({ev[n]["rot"]:.2f})')
        b = fr.obs['bones'].get(n)
        if b is not None and not e.get('hidden') and not fixed(e, 'tip') and fr.prof['sides'][n] != 'core':
            fit = float(np.median(F.seg_dist(b['pts'], ev[n]['start'], ev[n]['tip'])) / s)
            if fit > 1.8:
                flags.append(f'poor_fit:{n}({fit:.1f})')
        if warm and n in warm and not fixed(e, 'tip') and abs(wrap(ev[n]['rot'] - warm[n]['rot'])) > 2.5:
            flags.append(f'jump:{n}')
    return flags
