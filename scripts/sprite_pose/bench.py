"""Synthetic decoder benchmark — zero GPT.

Renders authored poses (pre-wipe clip backup) as stick cells, degrades them the way
image models do, decodes, and compares world angles with the truth.

    python bench.py [--rig biped] [--n 48] [--modes clean,gpt] [--seed 0] [--show out.png]

Modes:
  clean  the reference renderer, untouched
  gpt    per-bone length ×U(0.8,1.25), thick torso blob, little feet, color drift,
         line-width variation, blur + JPEG, cell scale/offset jitter, random knife angle
  fore   gpt-style damage (milder length jitter) on a FORESHORTENED truth: each limb chain
         tilted/swivelled out of plane with p=0.6 (limb3d ball+hinge), rendered with Stretch
--d3 decodes with the ball+hinge model (mode3d); stretch error is reported for tilted bones.
"""
import argparse, copy, io, json, math, random, sys, tarfile, time
import numpy as np
from PIL import Image, ImageDraw, ImageFilter
import rig as R
import fit as F
import limb3d


def all_poses(rig_name):
    out = []
    with tarfile.open(R.BACKUP) as tf:
        for m in tf.getmembers():
            if m.name.startswith(f'SkeletonStates/{rig_name}/') and m.name.endswith('.json') and '/._' not in m.name:
                d = json.load(tf.extractfile(m))
                for k in d['Keyframes']:
                    out.append((d['Name'], k['Time'], {b['Bone']: (b['Rotation'], b.get('Stretch')) for b in k['Bones']}))
    return out


def foreshorten(rig, prof, pose, rng):
    """Tilt/swivel limb chains out of plane (ball + hinge), returning the projected truth pose."""
    out = dict(pose)
    chains = [c[:2] for c in F.limb_chains(prof, rig) if len(c) >= 2]
    bm = {b['Name']: b for b in rig['Bones']}
    for u, l in chains:
        if rng.random() > 0.6:
            continue
        seg = R.fk(rig, out)
        theta = out.get(u, (bm[u]['Rotation'], None))[0]
        alpha = out.get(l, (bm[l]['Rotation'], None))[0]
        lim = F.joint_limits(bm[l])
        if lim:
            alpha = min(max(alpha, lim[0]), lim[1])
        if abs(alpha) < 0.3:  # give the hinge something to swivel
            alpha = (lim[0] * 0.4) if lim and abs(lim[0]) > abs(lim[1]) else (lim[1] * 0.4 if lim else alpha)
        phi = rng.choice([-1, 1]) * rng.uniform(0.35, 1.1)
        psi = rng.uniform(-1.0, 1.0)
        pw = seg[u][2] - theta
        lu, su, ll, sl, _, _ = limb3d.project(pw, theta, phi, alpha, psi)
        out[u], out[l] = (lu, su), (ll, sl)
    return out


def degrade_render(rig, prof, pose, size, rng, mode, knife):
    if mode == 'clean':
        return R.render_cell(rig, prof, pose, size, weapon=knife)
    rg = copy.deepcopy(rig)
    jl, jh = (0.92, 1.08) if mode == 'fore' else (0.8, 1.25)
    for b in rg['Bones']:
        if b['Name'] in prof['drawn'] and b['Name'] != prof['head']:
            b['Length'] *= rng.uniform(jl, jh)
    jit = {n: tuple(int(np.clip(c + rng.uniform(-25, 25), 0, 255)) for c in col) for n, col in prof['colors'].items()}
    pf = dict(prof, colors=jit)
    pf['weapon'] = dict(prof['weapon'], color=tuple(int(np.clip(c + rng.uniform(-25, 25), 0, 255)) for c in prof['weapon']['color']))
    im = Image.new('RGB', (size, size), 'white')
    dr = ImageDraw.Draw(im)
    seg = R.fk(rg, pose, knife)
    s = size / 64.0 * rng.uniform(0.85, 1.15)
    ground = size * rng.uniform(0.8, 0.9)
    ox = size / 2 + rng.uniform(-0.08, 0.08) * size
    oy = ground - R.lowest_y(seg, pf) * s
    dr.line([(8, ground), (size - 8, ground)], fill=R.GROUND, width=3)
    w = s * rng.uniform(2.0, 3.5)

    def blob(dr_, P_):  # thick torso, drawn over far limbs and under near ones (as GPT does)
        a, b, _ = seg['chest']
        dr_.line([P_(a), P_(b)], fill=pf['colors']['chest'], width=int(w * 2))
    R.draw_figure(dr, seg, pf, ox, oy, s, max(3, int(size / 32 * rng.uniform(0.8, 1.6))), after_core=blob)
    P = lambda q: (ox + q[0] * s, oy + q[1] * s)
    # feet: a short forward stub at each shin tip, in the shin's color
    for n in pf['drawn']:
        if 'leg' in n and n.endswith('lower'):
            t = P(seg[n][1])
            dr.line([t, (t[0] + 3.5 * s, t[1] + 0.3 * s)], fill=jit[n], width=max(3, size // 32))
    im = im.filter(ImageFilter.GaussianBlur(rng.uniform(0.3, 1.2)))
    buf = io.BytesIO()
    im.save(buf, 'JPEG', quality=int(rng.uniform(60, 90)))
    return Image.open(io.BytesIO(buf.getvalue())).convert('RGB')


def run(rig_name, n, modes, seed, show=None, d3=False):
    rig, prof = R.load_rig(rig_name), R.profile(rig_name)
    rng = random.Random(seed)
    poses = all_poses(rig_name)
    rng.shuffle(poses)
    poses = poses[:n]
    bind = {b['Name']: (b['Rotation'], None) for b in rig['Bones']}
    report = {}
    shows = []
    for mode in modes:
        errs, flips, werrs, t0, flagged = [], 0, [], time.time(), 0
        serrs = []  # |stretch error| on truth-tilted bones
        fstats = {}  # flag kind -> [fired on broken pose, fired on good pose]
        missed = 0  # broken poses with no flag
        for clip, t, pose in poses:
            ka = rng.uniform(-1.2, 1.2)
            knife = (prof['weapon']['parent'], ka, prof['weapon']['length'])
            if mode == 'fore':
                pose = foreshorten(rig, prof, pose, rng)
            cell = degrade_render(rig, prof, pose, 300, rng, mode, knife)
            # undrawn bones are given (struts/feet hold the true value): measure the drawn fit only
            ref = dict(bind)
            ref.update({k: v for k, v in pose.items() if k not in prof['drawn']})
            obs = F.observe(cell, prof)
            sol = F.solve(rig, prof, ref, obs, mode3d=d3)
            flags, _, _ = F.diagnose(sol, rig, prof)
            flagged += bool(flags)
            dec = dict(ref)
            dec.update({k: (v, sol['stretch'].get(k)) for k, v in sol['pose'].items()})
            for k, v in pose.items():
                if k in prof['drawn'] and v[1] is not None and v[1] < 0.95:
                    serrs.append(abs(sol['stretch'].get(k, 1.0) - v[1]))
            st, sd = R.fk(rig, pose), R.fk(rig, dec)
            worst = 0
            for b in prof['drawn']:
                if b == prof['head']:
                    continue
                e = abs(math.degrees(F.wrap(st[b][2] - sd[b][2])))
                errs.append(e)
                worst = max(worst, e)
            flips += worst > 45
            for k in {f.split(':')[0] for f in flags}:
                fstats.setdefault(k, [0, 0])[0 if worst > 45 else 1] += 1
            missed += worst > 45 and not flags
            wa = F.weapon_angle(sol, prof)
            if wa is not None:
                werrs.append(abs(math.degrees(F.wrap(wa - ka))))
            if show and len(shows) < 16 and mode == modes[-1]:
                shows.append((cell, dec, f'{clip}@{t:.2f} worst {worst:.0f}°' + (' ⚠' if flags else '')))
        e = np.array(errs)
        report[mode] = {
            'poses': len(poses), 'median_deg': round(float(np.median(e)), 2),
            'p90_deg': round(float(np.percentile(e, 90)), 2), 'p99_deg': round(float(np.percentile(e, 99)), 2),
            'poses_with_a_bone_off_45deg': flips, 'poses_flagged': flagged,
            'knife_median_deg': round(float(np.median(werrs)), 2) if werrs else None,
            'knife_found': f'{len(werrs)}/{len(poses)}', 'sec_per_pose': round((time.time() - t0) / len(poses), 2),
            'flags_[on_broken,on_good]': fstats, 'broken_unflagged': missed,
            'stretch_err_median': round(float(np.median(serrs)), 3) if serrs else None,
            'tilted_bones': len(serrs),
        }
        print(mode, json.dumps(report[mode]))
    if show and shows:
        T = 220
        sheet = Image.new('RGB', (4 * 2 * T, 4 * (T + 18)), 'white')
        d = ImageDraw.Draw(sheet)
        for i, (cell, dec, lab) in enumerate(shows):
            x, y = (i % 4) * 2 * T, (i // 4) * (T + 18)
            sheet.paste(cell.resize((T, T)), (x, y + 18))
            sheet.paste(R.render_cell(rig, prof, dec, T), (x + T, y + 18))
            d.text((x + 4, y + 3), lab, fill='black')
        sheet.save(show)
    return report


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--rig', default='biped')
    ap.add_argument('--n', type=int, default=48)
    ap.add_argument('--modes', default='clean,gpt')
    ap.add_argument('--seed', type=int, default=0)
    ap.add_argument('--show')
    ap.add_argument('--d3', action='store_true', help='decode with the ball+hinge model')
    a = ap.parse_args()
    run(a.rig, a.n, a.modes.split(','), a.seed, a.show, a.d3)
