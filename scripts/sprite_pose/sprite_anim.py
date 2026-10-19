"""sprite-anim: one-shot animation drafts from an image model, for any rig.

    python sprite_anim.py refs     --run NAME --rig RIG --from CLIP@T [--to CLIP@T]
    python sprite_anim.py gen      --run NAME --move "..." [--frames 16] [--weapon]
    python sprite_anim.py decode   --run NAME [--sheet PNG]
    python sprite_anim.py assemble --run NAME [--duration S] [--loop] [--contacts]
    python sprite_anim.py verify   --run NAME
    python sprite_anim.py report   --run NAME

A run lives in .probe/sprite/NAME/ (run.json holds its settings). The image model is
only ever called by `gen`, once per call, and its raw output is cached as sheet.png.
`decode` reads mapping.json (the agent's per-frame corrections) if present:

    {"frames": {"7": {"reject": true},
                "5": {"swap": ["arm"]},            # near/far arm colors were swapped
                "9": {"occluded": ["arm_l_lower"]},# ignore its pixels; interpolate it
                "3": {"pin": {"arm_r_lower": [x, y]}},  # tip pixel (cell coords)
                "4": {"flip": ["arm_r_upper"]},    # take the other orientation
                "6": {"knife": null}}}             # no blade (or a local angle in rad)
"""
import argparse, glob, io, json, math, os, shutil, subprocess, sys, time
import numpy as np
from PIL import Image, ImageDraw, ImageFont
import rig as R
import fit as F
import joints as J
import solve2 as S2
import markers as M

RUNS = os.path.join(R.REPO, '.probe', 'sprite')
DRAFTS = os.path.join(R.REPO, 'SkeletonDrafts')  # NOT under SkeletonStates/: Desktop + Web copy that tree recursively
CODEX_IMAGES = os.path.expanduser('~/.codex/generated_images')
FONT = ImageFont.truetype('/System/Library/Fonts/Supplemental/Arial.ttf', 14)


# ---- run state -----------------------------------------------------------------------

def run_dir(name):
    return os.path.join(RUNS, name)


def load_run(name):
    return json.load(open(os.path.join(run_dir(name), 'run.json')))


def save_run(name, cfg):
    os.makedirs(run_dir(name), exist_ok=True)
    json.dump(cfg, open(os.path.join(run_dir(name), 'run.json'), 'w'), indent=1)


def ref_pose(cfg, which='from'):
    rig = R.load_rig(cfg['rig'])
    p = {b['Name']: (b['Rotation'], None) for b in rig['Bones']}
    p.update(R.pose_at(cfg[which], cfg['rig']))
    return p


# ---- refs ----------------------------------------------------------------------------

def cmd_refs(a):
    cfg = {'rig': a.rig, 'from': a.__dict__['from'], 'to': a.to, 'weapon': a.weapon, 'markers': a.markers}
    save_run(a.run, cfg)
    rig, prof = R.load_rig(a.rig), R.profile(a.rig)
    d = run_dir(a.run)
    knife = (prof['weapon']['parent'], 0.0, prof['weapon']['length']) if a.weapon else None
    poses = [ref_pose(cfg, 'from')] + ([ref_pose(cfg, 'to')] if a.to else [])
    thin = bool(a.markers)
    R.render_sheet(rig, prof, poses, cols=len(poses), size=512, weapons=[knife] * len(poses),
                   labels=False, markers=a.markers, thin=thin).save(os.path.join(d, 'ref.png'))
    R.render_legend(prof, a.weapon, a.markers).save(os.path.join(d, 'legend.png'))
    if a.example:  # a programmatically drawn sheet of a DIFFERENT move in the exact format
        clip = R.load_clip(a.example, a.rig)
        ex, ws = [], []
        for i in range(8):
            pz = sample_clip(clip, i / 7)
            ka = pz.pop('knife', (0.0, None))[0]
            ex.append(pz)
            ws.append((prof['weapon']['parent'], ka, prof['weapon']['length']) if a.weapon else None)
        R.render_sheet(rig, prof, ex, cols=4, size=320, weapons=ws, markers=a.markers,
                       thin=thin).save(os.path.join(d, 'example.png'))
        cfg['example'] = a.example
        save_run(a.run, cfg)
    print('refs ->', d)


# ---- gen -----------------------------------------------------------------------------

PROMPT = """Generate exactly ONE image with your image generation tool. Do not retry, do not edit it \
afterwards, do not write or run any code, do not save or copy the file anywhere - just generate it once and \
reply with the word DONE.

The image: a {frames}-frame animation sprite sheet of the stick character shown in the attached reference \
(ref.png), performing: {move}
{endpoints}
Format - copy the attached reference EXACTLY:
- {cols}x{rows} grid of equal square cells, read left to right then top to bottom, one figure per cell, \
small frame number in each cell's corner, figure fully inside its cell, same size in every cell.
- Flat colored stick figure on a pure white background, side view, facing right. No shading, outlines, \
motion lines, effects, text or background details.
- Every bone keeps its legend color in every frame (attached legend.png): near limbs warm (red upper arm, \
orange forearm, magenta thigh, yellow shin), far limbs cool (blue upper arm, cyan forearm, green thigh, \
light-green shin), a THIN dark torso line and a dark filled circle head.{weapon_line}
- Limbs are straight rounded strokes that END IN ROUND CAPS: no feet, no hands, no fingers.
- Bone lengths and proportions stay identical to the reference in every frame - nothing stretches.
- A thin gray horizontal ground line at the same height in every cell; feet touch it when standing.
- Frames are EVENLY SPACED IN TIME: fast motion = big change between neighbouring frames, holds = small change. \
Make the motion strong and readable, with clear anticipation, a committed strike and follow-through.
"""


def cmd_gen(a):
    cfg = load_run(a.run)
    d = run_dir(a.run)
    cols, rows = grid_dims(a.frames)
    end = ('Frame 1 starts in the LEFT pose of ref.png; the motion ends in (or settles toward) the RIGHT pose.'
           if cfg.get('to') else 'Frame 1 starts in the pose shown in ref.png.')
    wl = ('\n- The character holds a short blade in the near hand, drawn as a straight VIOLET stroke '
          '(rgb 120,0,255) starting at the near hand - it stays the same length in every frame.'
          if cfg.get('weapon') else '')
    mk = cfg.get('markers')
    if mk == 'dots':
        wl += ('\n- JOINT DOTS: draw a big filled dot at the END of every limb bone, in that bone\'s colour '
               '(elbow dot red/blue, hand dot orange/cyan, knee dot magenta/green, foot dot yellow/light-green) '
               'and a violet dot at the blade tip. Limbs are THIN lines; every dot is drawn even where it overlaps.')
    elif mk == 'shapes':
        wl += ('\n- JOINT SHAPES: elbows and knees are HOLLOW RINGS with a white centre in the upper bone\'s colour; '
               'hands and feet are FILLED DOTS in the lower bone\'s colour; the blade tip is a violet dot. '
               'Limbs are THIN lines; every ring and dot is drawn even where it overlaps.')
    if os.path.exists(os.path.join(d, 'example.png')):
        wl += ('\n- example.png shows the EXACT drawing format on a different move (8 frames, 4x2). Copy its '
               'style precisely; do not copy its poses.')
    prompt = PROMPT.format(frames=a.frames, move=a.move, endpoints=end, cols=cols, rows=rows, weapon_line=wl)
    k = len(glob.glob(os.path.join(d, 'gen_*.prompt.txt'))) + 1
    open(os.path.join(d, f'gen_{k}.prompt.txt'), 'w').write(prompt)
    t0 = time.time()
    cmd = ['codex', 'exec', '-m', 'gpt-6-luna', '--skip-git-repo-check', '-s', 'read-only', '-C', d,
           prompt, '-i', os.path.join(d, 'ref.png'), os.path.join(d, 'legend.png')] + \
          ([os.path.join(d, 'example.png')] if os.path.exists(os.path.join(d, 'example.png')) else [])
    with open(os.path.join(d, f'gen_{k}.log'), 'w') as log:
        subprocess.run(cmd, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, timeout=900)
    new = session_images(os.path.join(d, f'gen_{k}.log'))
    if not new:
        sys.exit(f'no image generated - see gen_{k}.log')
    for j, f in enumerate(new):
        shutil.copy(f, os.path.join(d, f'gen_{k}_{j + 1}.png'))
    shutil.copy(new[0], os.path.join(d, 'sheet.png'))
    cfg.setdefault('gens', []).append({'k': k, 'move': a.move, 'frames': a.frames, 'images': len(new),
                                       'sec': round(time.time() - t0)})
    cfg['move'], cfg['frames'] = a.move, a.frames
    save_run(a.run, cfg)
    print(f'gen {k}: {len(new)} image(s) in {time.time() - t0:.0f}s -> sheet.png'
          + ('  WARNING: codex generated more than one image' if len(new) > 1 else ''))


def session_images(log_path):
    """Images from THIS codex session only (its id is in the log header), so parallel
    gens never pick up each other's output."""
    sid = next((l.split(':', 1)[1].strip() for l in open(log_path) if l.lower().startswith('session id:')), None)
    if not sid:
        return []
    return sorted(glob.glob(os.path.join(CODEX_IMAGES, sid, '*.png')), key=os.path.getmtime)


# ---- decode --------------------------------------------------------------------------

def grid_dims(frames):
    """(cols, rows): 8 frames -> 4x2, else square."""
    if frames == 8:
        return 4, 2
    n = int(round(math.sqrt(frames)))
    return n, n


def split_cells(sheet, frames):
    cols, rows = grid_dims(frames)
    W, H = sheet.size
    cw, ch = W / cols, H / rows
    m = 0.02  # trim grid lines / borders
    return [sheet.crop((int((i % cols + m) * cw), int((i // cols + m) * ch),
                        int((i % cols + 1 - m) * cw), int((i // cols + 1 - m) * ch))) for i in range(frames)]


def ground_row(cell):
    a = np.asarray(cell.convert('RGB'), int)
    gray = (a.max(2) - a.min(2) < 25) & (a.max(2) > 100) & (a.max(2) < 215)
    rows = gray.sum(1)
    r = int(rows.argmax())
    return r if rows[r] > 0.3 * a.shape[1] else None


def swap_obs(obs, prof, kinds):
    for kind in kinds:
        near = [n for n in prof['drawn'] if prof['sides'][n] == 'near' and R.kind_of(n) == kind]
        for n in near:
            f = n.replace('_r_', '_l_')
            obs['bones'][n], obs['bones'][f] = obs['bones'].get(f), obs['bones'].get(n)


def calibrate_lengths(cells, rig, prof, ref, fm):
    """Per-sheet drawn proportions: a planar pass over every frame, then per bone the 80th
    percentile of (drawn stroke length / rig length * scale). The image model's STYLE (GPT draws
    a shorter torso and low shoulders, so upper arms read ~0.55 in every frame) must not decode
    as foreshortening; only a stroke shorter than its own sheet-typical length is a tilt."""
    ratios = {}
    for i, cell in enumerate(cells):
        o = fm.get(i + 1, {})
        if o.get('reject'):
            continue
        obs = F.observe(cell, prof)
        if o.get('swap'):
            swap_obs(obs, prof, o['swap'])
        sol = F.solve(rig, prof, ref, obs, skip=o.get('occluded', ()))
        _, _, per_bone = F.diagnose(sol, rig, prof)
        for n, pb in per_bone.items():
            if n not in o.get('occluded', ()):
                ratios.setdefault(n, []).append(pb['drawn_len_ratio'])
    return {n: round(float(min(max(np.percentile(v, 80), 0.5), 1.6)), 3) for n, v in ratios.items() if len(v) >= 3}


def cmd_decode(a):
    cfg = load_run(a.run)
    d = run_dir(a.run)
    rig, prof = R.load_rig(cfg['rig']), R.profile(cfg['rig'])
    sheet = Image.open(a.sheet or os.path.join(d, 'sheet.png')).convert('RGB')
    frames = cfg.get('frames', 16)
    cells = split_cells(sheet, frames)
    mp = json.load(open(os.path.join(d, 'mapping.json'))) if os.path.exists(os.path.join(d, 'mapping.json')) else {}
    fm = {int(k): v for k, v in mp.get('frames', {}).items()}
    ref = ref_pose(cfg, 'from')
    os.makedirs(os.path.join(d, 'cells'), exist_ok=True)
    mode3d = not a.planar
    lengths = calibrate_lengths(cells, rig, prof, ref, fm) if mode3d else {}
    if lengths:
        print('sheet proportions (drawn/rig):', ' '.join(f'{k}={v}' for k, v in sorted(lengths.items())))
    jpath = os.path.join(d, 'joints.json')
    jm_all = {int(k): v for k, v in json.load(open(jpath)).get('frames', {}).items()} if os.path.exists(jpath) else {}
    out, prev, prev_pose, scales = [], None, None, []
    for i, cell in enumerate(cells):
        f = i + 1
        o = dict(fm.get(f, {}))
        jm = jm_all.get(f)
        if a.auto_joints and jm is None:
            jm = J.candidates(F.observe(cell, prof), prof, rig)[0]
        if jm:  # the agent's marks are pins; explicit mapping pins win on conflict
            o['pin'] = {**{k: v for k, v in jm.items() if isinstance(v, list) and k != '__blade'}, **o.get('pin', {})}
        cell.save(os.path.join(d, 'cells', f'{f:02d}.png'))
        if o.get('reject'):
            out.append({'frame': f, 'rejected': True})
            continue
        obs = F.observe(cell, prof)
        if o.get('swap'):
            swap_obs(obs, prof, o['swap'])
        scale0 = float(np.median(scales)) if len(scales) >= 2 else None
        kw = dict(scale0=scale0, pins=o.get('pin'), skip=o.get('occluded', ()), mode3d=mode3d, lengths=lengths)
        sol = F.solve(rig, prof, ref, obs, prev=prev, **kw)
        for b in o.get('flip', []):  # forced orientation: re-run from the flipped solution
            init = dict(sol['params'])
            if mode3d and any(b == l for _, l in sol['model'].chains):
                print(f'  frame {f}: flip of hinge bone {b} ignored (the hinge is joint-limited) - flip its upper bone')
                continue
            init[b] = F.wrap(init[b] + math.pi)
            sol = F.solve(rig, prof, ref, obs, init=init, **kw)
        flags, notes, per_bone = F.diagnose(sol, rig, prof, prev_pose)
        knife = o['knife'] if 'knife' in o else (F.weapon_angle(sol, prof) if cfg.get('weapon') else None)
        if jm and isinstance(jm.get('__blade'), list) and 'knife' not in o and prof['weapon']['parent']:
            sg, og, sc = sol['model'].segs(sol['x'])
            hp = og + sc * np.asarray(sg[prof['weapon']['parent']][1])
            bt = np.asarray(jm['__blade'], float)
            knife = F.wrap(math.atan2(bt[1] - hp[1], bt[0] - hp[0]) - sg[prof['weapon']['parent']][2])
        if cfg.get('weapon') and knife is None and 'knife' not in o:
            notes.append('blade:not_found')
        x = sol['x']
        scales.append(float(x[2]))
        seg, org, s = sol['model'].segs(x)
        gr = ground_row(cell)
        soles = {}
        for pt in rig.get('Points', []):
            if pt.get('Role') == 'support' and pt['Bone'] in seg:
                tip = org + s * np.asarray(seg[pt['Bone']][1])
                soles[pt['Id']] = bool(gr is not None and abs(tip[1] - gr) < 1.5 * s)
        out.append({'frame': f, 'pose': sol['pose'], 'stretch': {k: round(v, 4) for k, v in sol['stretch'].items()},
                    'depth': {k: round(v, 3) for k, v in sol['depth'].items()}, 'params': sol['params'],
                    'knife': knife, 'flags': flags, 'notes': notes,
                    'per_bone': per_bone, 'cost': round(sol['cost'], 3), 'root_px': [float(x[0]), float(x[1])],
                    'scale': float(x[2]), 'ground_row': gr, 'planted': soles,
                    'occluded': o.get('occluded', []), 'marked': bool(jm)})
        occ = set(o.get('occluded', ()))
        prev = {k: v for k, v in sol['params'].items() if k not in occ}
        prev_pose = {k: v for k, v in sol['pose'].items() if k not in occ}
        print(f'frame {f:2d}: cost {sol["cost"]:.2f}  ' + ('FLAGS ' + ' '.join(flags) if flags else 'ok')
              + ('  notes ' + ' '.join(notes) if notes else ''))
    # occluded bones: interpolate rotation (and stretch) from the nearest frames that saw them
    for k, r in enumerate(out):
        for b in r.get('occluded', []):
            seen = [(j, q['pose'][b], q.get('stretch', {}).get(b, 1.0)) for j, q in enumerate(out)
                    if 'pose' in q and b not in q.get('occluded', [])]
            lo = [t for t in seen if t[0] < k]
            hi = [t for t in seen if t[0] > k]
            if lo and hi:
                (j0, v0, s0), (j1, v1, s1) = lo[-1], hi[0]
                u = (k - j0) / (j1 - j0)
                r['pose'][b] = v0 + u * F.wrap(v1 - v0)
                if b in r.get('stretch', {}):
                    r['stretch'][b] = s0 + u * (s1 - s0)
            elif lo or hi:
                t = lo[-1] if lo else hi[0]
                r['pose'][b] = t[1]
                if b in r.get('stretch', {}):
                    r['stretch'][b] = t[2]
    json.dump(out, open(os.path.join(d, 'decoded.json'), 'w'), indent=1)
    cfg['lengths'], cfg['mode3d'] = lengths, mode3d
    save_run(a.run, cfg)
    flagged = [r['frame'] for r in out if r.get('flags')]
    print(f'decoded {len(out)} frames; flagged: {flagged or "none"}; rejected: '
          f'{[r["frame"] for r in out if r.get("rejected")] or "none"}')
    render_decode_sheets(a.run)


def cmd_points(a):
    """Per-frame stroke endpoints in cell pixel coords, for writing mapping.json pins."""
    cfg = load_run(a.run)
    d = run_dir(a.run)
    prof = R.profile(cfg['rig'])
    frames = [int(f) for f in a.frames.split(',')] if a.frames else range(1, cfg.get('frames', 16) + 1)
    for f in frames:
        cell = Image.open(os.path.join(d, 'cells', f'{f:02d}.png')).convert('RGB')
        obs = F.observe(cell, prof)
        parts = []
        for n, b in obs['bones'].items():
            if b is None:
                continue
            e0, e1 = b['ends']
            parts.append(f'{n}: ({e0[0]:.0f},{e0[1]:.0f})-({e1[0]:.0f},{e1[1]:.0f})')
        blade = obs['bones'].get('__weapon')
        hand = prof['weapon']['parent']
        if blade is not None and obs['bones'].get(hand) is not None:
            fe = obs['bones'][hand]['ends']
            be = blade['ends']
            dd = min(((i, j) for i in range(2) for j in range(2)), key=lambda ij: np.linalg.norm(fe[ij[0]] - be[ij[1]]))
            hp = (fe[dd[0]] + be[dd[1]]) / 2
            parts.append(f'HAND (forearm/blade junction) ~ [{hp[0]:.0f}, {hp[1]:.0f}]')
        print(f'frame {f}: ' + ' | '.join(parts))


def cmd_joints(a):
    """Propose joint marks for the agent: joints_auto.json + joints_review.png. With
    --current, draw the agent's joints.json instead (to check its own marks)."""
    cfg = load_run(a.run)
    d = run_dir(a.run)
    rig, prof = R.load_rig(cfg['rig']), R.profile(cfg['rig'])
    frames = cfg.get('frames', 16)
    cells = split_cells(Image.open(os.path.join(d, 'sheet.png')).convert('RGB'), frames)
    want = [int(f) for f in a.frames.split(',')] if a.frames else range(1, frames + 1)
    cur = {}
    if a.current and os.path.exists(os.path.join(d, 'joints.json')):
        cur = json.load(open(os.path.join(d, 'joints.json'))).get('frames', {})
    auto, tiles = {}, []
    for f in want:
        cand, notes = J.candidates(F.observe(cells[f - 1], prof), prof, rig)
        auto[str(f)] = cand
        marks = cur.get(str(f), cand) if a.current else cand
        tiles.append(J.review_image(cells[f - 1], marks, prof, f'frame {f}' + ('  (' + '; '.join(notes) + ')' if notes else '')))
        print(f'frame {f}: ' + ' '.join(f'{J.short_label(k, prof)}={v}' for k, v in marks.items())
              + (f'   NOTE {"; ".join(notes)}' if notes else ''))
    json.dump({'frames': auto}, open(os.path.join(d, 'joints_auto.json'), 'w'), indent=1)
    cols = min(4, len(tiles))
    w, h = tiles[0].size
    g = Image.new('RGB', (cols * w, ((len(tiles) + cols - 1) // cols) * h), 'white')
    for i, t in enumerate(tiles):
        g.paste(t, ((i % cols) * w, (i // cols) * h))
    g.save(os.path.join(d, 'joints_review.png'))
    print('->', os.path.join(d, 'joints_review.png'))


def cmd_synth(a):
    """Experiment tooling: a synthetic sheet from an authored clip with GPT-like damage (and
    optional foreshortening), plus truth.json (poses + joint pixels) for `score`."""
    import random, bench as B
    rig, prof = R.load_rig(a.rig), R.profile(a.rig)
    rng = random.Random(a.seed)
    clip = R.load_clip(a.clip, a.rig)
    n = a.frames
    cols = int(round(math.sqrt(n)))
    size = 300
    sheet = Image.new('RGB', (cols * size, cols * size), 'white')
    truth = []
    m = int(0.02 * size)  # split_cells trims this margin
    for i in range(n):
        t = i / (n - 1)
        pose = sample_clip(clip, t)
        knife_ang = pose.pop('knife', (0.0, None))[0] + rng.uniform(-0.3, 0.3)
        if a.fore:
            pose = B.foreshorten(rig, prof, pose, rng)
        knife = (prof['weapon']['parent'], knife_ang, prof['weapon']['length'])
        cell, (ox, oy, sc, seg) = B.degrade_render(rig, prof, pose, size, rng, 'fore' if a.fore else 'gpt', knife, return_xform=True)
        sheet.paste(cell, ((i % cols) * size, (i // cols) * size))
        px = lambda q: [round(ox + sc * q[0] - m, 1), round(oy + sc * q[1] - m, 1)]
        jt = {'__root': [round(ox - m, 1), round(oy - m, 1)]}
        for nme in prof['drawn']:
            if nme != prof['head']:
                jt[nme] = px(seg[nme][1])
        jt['__blade'] = px(seg['__weapon'][1])
        truth.append({'frame': i + 1, 'pose': {k: list(v) if isinstance(v, tuple) else v for k, v in pose.items()},
                      'knife': knife_ang, 'joints': jt})
    cfg = {'rig': a.rig, 'from': a.clip + '@0', 'weapon': True, 'frames': n, 'synthetic': True}
    save_run(a.run, cfg)
    sheet.save(os.path.join(run_dir(a.run), 'sheet.png'))
    json.dump(truth, open(os.path.join(run_dir(a.run), 'truth.json'), 'w'), indent=1)
    print('synthetic sheet ->', run_dir(a.run))


def cmd_score(a):
    """Experiment tooling (NOT for agents in real runs): decoded vs truth, per frame."""
    cfg = load_run(a.run)
    d = run_dir(a.run)
    rig, prof = R.load_rig(cfg['rig']), R.profile(cfg['rig'])
    truth = {t['frame']: t for t in json.load(open(os.path.join(d, 'truth.json')))}
    dec = json.load(open(os.path.join(d, 'decoded.json')))
    errs, worst, serr = [], [], []
    for r in dec:
        if 'pose' not in r:
            continue
        tp = {k: tuple(v) if isinstance(v, list) else v for k, v in truth[r['frame']]['pose'].items()}
        dp = dict(tp)
        dp.update({k: (v, r.get('stretch', {}).get(k)) for k, v in r['pose'].items()})
        st, sd = R.fk(rig, tp), R.fk(rig, dp)
        e = [abs(math.degrees(F.wrap(st[b][2] - sd[b][2]))) for b in prof['drawn'] if b != prof['head']]
        errs += e
        worst.append(max(e))
        for b in prof['drawn']:
            v = tp.get(b)
            if isinstance(v, tuple) and v[1] is not None and v[1] < 0.95:
                serr.append(abs(r.get('stretch', {}).get(b, 1.0) - v[1]))
    e = np.array(errs)
    res = {'frames': len(worst), 'median_deg': round(float(np.median(e)), 2), 'p90_deg': round(float(np.percentile(e, 90)), 2),
           'frames_with_a_bone_off_30deg': int(sum(w > 30 for w in worst)),
           'stretch_err_median': round(float(np.median(serr)), 3) if serr else None,
           'worst_by_frame': [round(w) for w in worst]}
    print(json.dumps(res))
    return res


def cmd_detect(a):
    """Read the joint labels the image model drew (markers.py) into state.json as fixed tips,
    and draw them for review (joints_review.png). Missing joints stay free for `solve`."""
    cfg = load_run(a.run)
    d = run_dir(a.run)
    rig, prof = R.load_rig(cfg['rig']), R.profile(cfg['rig'])
    frames = cfg.get('frames', 16)
    cells = split_cells(Image.open(os.path.join(d, 'sheet.png')).convert('RGB'), frames)
    sp = os.path.join(d, 'state.json')
    states = json.load(open(sp)) if os.path.exists(sp) else {'frames': {}}
    tiles, total = [], 0
    for f in range(1, frames + 1):
        marks, missing = M.detect(cells[f - 1], prof, cfg.get('markers') or 'dots')
        states['frames'][str(f)] = M.to_state(marks)
        total += len(marks)
        tiles.append(J.review_image(cells[f - 1], marks, prof, f'frame {f}' + (f'  missing: {", ".join(missing)}' if missing else '')))
        print(f'frame {f}: {len(marks)} joints found' + (f'; missing {", ".join(missing)}' if missing else ''))
    json.dump(states, open(sp, 'w'), indent=1)
    cols = 4
    w, h = tiles[0].size
    g = Image.new('RGB', (cols * w, ((len(tiles) + cols - 1) // cols) * h), 'white')
    for i, t in enumerate(tiles):
        g.paste(t, ((i % cols) * w, (i // cols) * h))
    g.save(os.path.join(d, 'joints_review.png'))
    print(f'{total} joints detected over {frames} frames -> state.json, joints_review.png')


def cmd_solve(a):
    """Hard-masked solve (solve2.py). state.json holds one state per frame: whatever the
    agent marked "fixed" is kept exactly, everything else is solved, warm-started from the
    previous frame. Writes the solved states back (fixed flags preserved) + decoded.json."""
    cfg = load_run(a.run)
    d = run_dir(a.run)
    rig, prof = R.load_rig(cfg['rig']), R.profile(cfg['rig'])
    frames = cfg.get('frames', 16)
    cells = split_cells(Image.open(os.path.join(d, 'sheet.png')).convert('RGB'), frames)
    os.makedirs(os.path.join(d, 'cells'), exist_ok=True)
    for i, c in enumerate(cells):
        c.save(os.path.join(d, 'cells', f'{i + 1:02d}.png'))
    ref = ref_pose(cfg, 'from')
    sp = os.path.join(d, 'state.json')
    states = json.load(open(sp)) if os.path.exists(sp) else {'frames': {}}
    if a.recalibrate or 'lengths' not in cfg:
        cfg['lengths'] = calibrate_lengths(cells, rig, prof, ref, {})
        save_run(a.run, cfg)
    lengths = cfg['lengths']
    dp = os.path.join(d, 'decoded.json')
    decoded = {r['frame']: r for r in json.load(open(dp))} if os.path.exists(dp) and a.frames else {}
    want = [int(f) for f in a.frames.split(',')] if a.frames else list(range(1, frames + 1))
    scales = [r['scale'] for r in decoded.values() if 'scale' in r]
    for f in want:
        sf = states['frames'].get(str(f), {})
        if sf.get('reject'):
            decoded[f] = {'frame': f, 'rejected': True}
            continue
        # warm start: the nearest earlier solved, non-rejected frame
        warm = None
        for g in range(f - 1, 0, -1):
            sg = states['frames'].get(str(g))
            if sg and not sg.get('reject') and 'scale' in sg:
                warm = {n: {'rot': b['rot'], 'stretch': b.get('stretch', 1.0)} for n, b in sg['bones'].items()}
                warm['root'], warm['scale'] = sg['root']['pos'], sg['scale']['value']
                break
        obs = F.observe(cells[f - 1], prof)
        scale0 = float(np.median(scales)) if len(scales) >= 2 else None
        fr, x, cost = S2.solve(rig, prof, ref, obs, sf, warm, lengths, scale0)
        ev, root, sc = fr.evaluate(x)
        knife, kentry = S2.blade(fr, ev, sc) if cfg.get('weapon') else (None, None)
        if kentry is not None and (sf.get('knife') or {}).get('fixed'):
            kentry['fixed'] = sf['knife']['fixed']
        solved, ev = S2.solved_state(fr, x, kentry)
        flags = S2.diagnose(fr, ev, sc, warm)
        states['frames'][str(f)] = solved
        scales.append(float(sc))
        gr = ground_row(cells[f - 1])
        planted = {pt['Id']: bool(gr is not None and abs(ev[pt['Bone']]['tip'][1] - gr) < 1.5 * sc)
                   for pt in rig.get('Points', []) if pt.get('Role') == 'support' and pt['Bone'] in ev}
        nfix = sum(1 for b in sf.get('bones', {}).values() if b.get('fixed')) + bool((sf.get('root') or {}).get('fixed'))
        decoded[f] = {'frame': f, 'pose': {n: solved['bones'][n]['rot'] for n in fr.drawn},
                      'stretch': {n: solved['bones'][n]['stretch'] for n in fr.drawn},
                      'knife': knife, 'flags': flags, 'notes': [f'fixed:{nfix}'] if nfix else [],
                      'cost': round(cost, 3), 'root_px': [float(v) for v in root], 'scale': float(sc),
                      'ground_row': gr, 'planted': planted, 'occluded': [], 'marked': nfix > 0}
        print(f'frame {f:2d}: cost {cost:.2f}  fixed {nfix:2d}  ' + ('FLAGS ' + ' '.join(flags) if flags else 'ok'))
    json.dump(states, open(sp, 'w'), indent=1)
    out = [decoded[k] for k in sorted(decoded)]
    json.dump(out, open(dp, 'w'), indent=1)
    print(f'solved {len(want)} frame(s); flagged: {[r["frame"] for r in out if r.get("flags")] or "none"}')
    render_decode_sheets(a.run)


# ---- assemble ------------------------------------------------------------------------

def cmd_assemble(a):
    cfg = load_run(a.run)
    d = run_dir(a.run)
    rig, prof = R.load_rig(cfg['rig']), R.profile(cfg['rig'])
    dec = [r for r in json.load(open(os.path.join(d, 'decoded.json'))) if not r.get('rejected')]
    if a.keep:
        k0, k1 = (int(x) for x in a.keep.split('-'))
        dec = [r for r in dec if k0 <= r['frame'] <= k1]
    n = len(dec)
    ref = ref_pose(cfg, 'from')
    clamp_limits(dec, rig)
    if a.key_tol > 0:
        before = len(dec)
        dec = reduce_keys(dec, a.key_tol)
        n = len(dec)
        print(f'keys: {before} frames -> {n} keys {[r["frame"] for r in dec]} (tolerance {a.key_tol} rad)')
    travel = {}
    for i in range(n - 1):
        for b in dec[i]['pose']:
            travel[b] = travel.get(b, 0) + abs(F.wrap(dec[i + 1]['pose'][b] - dec[i]['pose'][b]))
    fb = max(travel, key=travel.get) if travel else None
    if fb:
        print(f'fastest bone travel: {fb} {travel[fb]:.1f} rad over the clip '
              f'(digest STEEP needs <= 8 per unit phase, so <= ~{a.steep_limit:.0f} total fits)')
    times, steep = retime(dec, rig, a.steep_limit)
    if steep:
        print(f'retime: {len(steep)} interval(s) still above {a.steep_limit} rad/phase '
              f'(the motion needs more phase than the clip has): {steep}')
    knives = [r.get('knife') for r in dec]
    have_knife = cfg.get('weapon') and any(k is not None for k in knives)
    if have_knife:  # fill missing blade angles from neighbours
        idx = [i for i, k in enumerate(knives) if k is not None]
        for i, k in enumerate(knives):
            if k is None:
                lo = [j for j in idx if j < i]
                hi = [j for j in idx if j > i]
                knives[i] = knives[lo[-1]] if lo else knives[hi[0]]
    ground_k = com_ground_offset(cfg['rig'])
    kfs = []
    for r, t, kv in zip(dec, times, knives):
        bones = []
        for b in rig['Bones']:
            nme = b['Name']
            if nme in r['pose']:
                rot, st = r['pose'][nme], r.get('stretch', {}).get(nme)
                if st is not None and abs(st - 1) < 0.02:
                    st = None
            else:
                rot, st = ref.get(nme, (b['Rotation'], None))
            e = {'Bone': nme, 'Rotation': round(float(rot), 5)}
            if st is not None:
                e['Stretch'] = round(float(st), 4)
            bones.append(e)
        if have_knife:
            bones.append({'Bone': prof['weapon']['bone'], 'Rotation': round(float(kv), 5)})
        kf = {'Time': round(t, 5), 'Bones': bones}
        if not a.no_ground:
            # com anchor = the grounded body point: the engine places the rig so com sits at
            # the physics body, so com.Y = sole.Y - K keeps the lowest sole on the floor
            # (a crouch sinks the body instead of lifting the feet). K from the rig's idle.
            seg = R.fk(rig, {e['Bone']: (e['Rotation'], e.get('Stretch')) for e in bones})
            sole = max(seg[p_['Bone']][1][1] for p_ in rig.get('Points', []) if p_.get('Role') == 'support')
            kf['Additions'] = [{'Name': 'com', 'Kind': 'Point', 'Px': 0, 'Py': round(sole - ground_k, 4),
                                'Dx': 0, 'Dy': 0}]
        kfs.append(kf)
    clip = {'Name': a.run, 'Type': 'Draft', 'Skeleton': cfg['rig'], 'Duration': a.duration, 'Loop': a.loop}
    if have_knife:
        clip['ExtraBones'] = [{'Name': prof['weapon']['bone'], 'Parent': prof['weapon']['parent'],
                               'Rotation': 0, 'Length': 0}]
        seen = [t for t, r in zip(times, dec) if r.get('knife') is not None]
        clip['Attachments'] = [{'Bone': prof['weapon']['bone'], 'Effect': 'knife',
                                'Start': round(min(seen), 4), 'End': round(min(max(seen) + 1e-3, 0.999), 4)}]
    if a.contacts:
        spans = []
        for pid in [p['Id'] for p in rig.get('Points', []) if p.get('Role') == 'support']:
            on = [r.get('planted', {}).get(pid, False) for r in dec]
            i = 0
            while i < n:
                if on[i]:
                    j = i
                    while j + 1 < n and on[j + 1]:
                        j += 1
                    end = times[j] if j > i else min(times[i] + 0.5 / max(n - 1, 1), 1.0)
                    spans.append({'Point': pid, 'Start': round(times[i], 4), 'End': round(end, 4),
                                  'Source': 'SelfPlant'})
                    i = j + 1
                else:
                    i += 1
        clip['Contacts'] = spans
    clip['Keyframes'] = kfs
    path = os.path.join(d, a.run + '.json')
    json.dump(clip, open(path, 'w'), indent=2)
    os.makedirs(os.path.join(DRAFTS, cfg['rig']), exist_ok=True)
    shutil.copy(path, os.path.join(DRAFTS, cfg['rig'], a.run + '.json'))
    cfg['clip'] = {'duration': a.duration, 'loop': a.loop, 'contacts': a.contacts, 'keys': n,
                   'keep': a.keep, 'fastest_travel': [fb, round(travel.get(fb, 0), 2)] if fb else None,
                   'steep_intervals': steep}
    save_run(a.run, cfg)
    print(f'assembled {n} keys -> {path} (+ SkeletonDrafts/{cfg["rig"]}/)')
    render_playback(a.run, clip, rig, prof)


def com_ground_offset(rig_name, default=39.9):
    """sole.Y - com.Y of the rig's authored idle (pre-wipe backup): the height of the com
    anchor above the floor in rig units. 39.88-40.06 across every grounded biped clip."""
    try:
        d = R.load_clip('backup:idle', rig_name)
        rg = R.load_rig(rig_name)
        k = d['Keyframes'][0]
        com = next(x for x in k.get('Additions', []) if x.get('Name') == 'com')
        seg = R.fk(rg, {b['Bone']: (b['Rotation'], b.get('Stretch')) for b in k['Bones']})
        sole = max(seg[p['Bone']][1][1] for p in rg.get('Points', []) if p.get('Role') == 'support')
        return sole - com['Py']
    except Exception:
        return default


def clamp_limits(dec, rig, tol=0.35):
    """Pull small violations of the rig's own joint limits (straight-ish legs drawn a hair
    past straight) back inside; larger ones are decode/drawing errors and are left for review."""
    bm = {b['Name']: b for b in rig['Bones']}
    for r in dec:
        for n, v in r['pose'].items():
            lo, hi = bm[n].get('MinRotation'), bm[n].get('MaxRotation')
            if lo is None:
                continue
            if lo - tol <= v < lo:
                r['pose'][n] = lo + 0.03
            elif hi < v <= hi + tol:
                r['pose'][n] = hi - 0.03


def reduce_keys(dec, tol):
    """Douglas-Peucker over the pose trajectory: keep a frame only if linear interpolation
    (in frame index, wrap-aware) between the kept neighbours misses some bone by > tol rad.
    Drops in-betweens and the decoder's jitter in holds; keeps key poses and reversals."""
    def worst(i, j):
        best, at = 0.0, None
        for k in range(i + 1, j):
            u = (dec[k]['frame'] - dec[i]['frame']) / (dec[j]['frame'] - dec[i]['frame'])
            for b, v in dec[k]['pose'].items():
                interp = dec[i]['pose'][b] + u * F.wrap(dec[j]['pose'][b] - dec[i]['pose'][b])
                e = abs(F.wrap(v - interp))
                if e > best:
                    best, at = e, k
            for b, v in dec[k].get('stretch', {}).items():
                si, sj = dec[i].get('stretch', {}).get(b, 1.0), dec[j].get('stretch', {}).get(b, 1.0)
                e = abs(v - (si + u * (sj - si)))
                if e > best:
                    best, at = e, k
        return best, at

    keep = {0, len(dec) - 1}
    stack = [(0, len(dec) - 1)]
    while stack:
        i, j = stack.pop()
        e, k = worst(i, j)
        if k is not None and e > tol:
            keep.add(k)
            stack += [(i, k), (k, j)]
    return [dec[i] for i in sorted(keep)]


def retime(dec, rig, limit):
    """Keyframe times from the image model's even spacing, widening only the intervals whose
    fastest bone would exceed `limit` rad per unit phase (Probe digest's STEEP gate is 8).
    Spacing d_i = max(c, delta_i / limit), with c solved so the times span [0, 1]."""
    n = len(dec)
    if n < 2:
        return [0.0] * n, []
    frames = [r['frame'] for r in dec]
    gaps = [frames[i + 1] - frames[i] for i in range(n - 1)]  # rejected frames leave bigger gaps
    kn = lambda r: r.get('knife') if r.get('knife') is not None else 0.0
    delta = [max([abs(F.wrap(dec[i + 1]['pose'][b] - dec[i]['pose'][b])) for b in dec[i]['pose']]
                 + [abs(F.wrap(kn(dec[i + 1]) - kn(dec[i])))])
             for i in range(n - 1)]
    need = [dl / limit for dl in delta]
    if sum(need) >= 1:
        d = [x / sum(need) for x in need]
    else:
        lo, hi = 0.0, 1.0
        for _ in range(60):
            c = (lo + hi) / 2
            if sum(max(c * g, x) for g, x in zip(gaps, need)) > 1:
                hi = c
            else:
                lo = c
        d = [max(lo * g, x) for g, x in zip(gaps, need)]
        k = 1 / sum(d)
        d = [x * k for x in d]
    t = [0.0]
    for x in d:
        t.append(t[-1] + x)
    steep = [f'{frames[i]}->{frames[i + 1]}' for i in range(n - 1) if delta[i] / max(d[i], 1e-9) > 8]
    return [round(x / t[-1], 5) for x in t], steep


# ---- verify (engine) -----------------------------------------------------------------

def cmd_verify(a):
    cfg = load_run(a.run)
    d = run_dir(a.run)
    rig = cfg['rig']
    pool = os.path.join(R.REPO, 'SkeletonStates', rig)
    tmp_name = f'zzdraft_{a.run}'
    dst = os.path.join(pool, tmp_name + '.json')
    if os.path.exists(dst):
        sys.exit(f'{dst} exists - refusing to overwrite')
    clip = json.load(open(os.path.join(d, a.run + '.json')))
    clip['Name'] = tmp_name
    json.dump(clip, open(dst, 'w'), indent=2)
    try:
        probe = os.path.join(R.REPO, 'MTile.Probe', 'bin', 'Debug', 'net8.0', 'MTile.Probe.dll')
        r = subprocess.run(['dotnet', probe, 'digest', tmp_name, '--rig', rig], cwd=R.REPO,
                           capture_output=True, text=True, timeout=300)
        open(os.path.join(d, 'digest.txt'), 'w').write(r.stdout + r.stderr)
        flags = [l for l in (r.stdout + r.stderr).splitlines() if 'FLAG' in l.upper()]
        print(f'digest: {len(flags)} FLAG line(s) -> digest.txt')
        for l in flags[:12]:
            print('  ', l.strip())
        demo = os.path.join(R.REPO, 'MTile.Demo', 'bin', 'Debug', 'net8.0', 'MTile.Demo.dll')
        strip = os.path.join(d, 'engine_strip.png')
        r = subprocess.run(['dotnet', demo, tmp_name, '--rig', rig, '--strip', strip, '--frames', '12',
                            '--columns', '6'], cwd=R.REPO, capture_output=True, text=True, timeout=300)
        print('engine strip:', strip if os.path.exists(strip) else 'FAILED\n' + (r.stdout + r.stderr)[-800:])
    finally:
        os.remove(dst)


# ---- visual outputs ------------------------------------------------------------------

def overlay(cell, r, rig, prof):
    im = Image.blend(Image.new('RGB', cell.size, 'white'), cell, 0.45)
    if 'pose' not in r:
        ImageDraw.Draw(im).line([(0, 0), cell.size], fill='red', width=4)
        return im
    dr = ImageDraw.Draw(im)
    pose = {k: (v, r.get('stretch', {}).get(k)) for k, v in r['pose'].items()}
    full = ref_pose_cache(rig, prof, r)
    full.update(pose)
    rig_fit = _REF.get('rig_fit', rig)
    seg = R.fk(rig_fit, full, (prof['weapon']['parent'], r['knife'], prof['weapon']['length']) if r.get('knife') is not None else None)
    ox, oy = r['root_px']
    s = r['scale']
    P = lambda q: (ox + q[0] * s, oy + q[1] * s)
    for n in prof['drawn'] + (['__weapon'] if '__weapon' in seg else []):
        if n == prof['head']:
            a, b, _ = seg[n]
            c = P(((a[0] + b[0]) / 2, (a[1] + b[1]) / 2))
            rr = math.dist(a, b) * s * 0.55
            dr.ellipse([c[0] - rr, c[1] - rr, c[0] + rr, c[1] + rr], outline='black', width=2)
            continue
        a, b, _ = seg[n]
        dr.line([P(a), P(b)], fill='black', width=3)
        dr.ellipse([P(b)[0] - 3, P(b)[1] - 3, P(b)[0] + 3, P(b)[1] + 3], fill='white', outline='black')
    if r.get('ground_row') is not None:
        dr.line([(0, r['ground_row']), (cell.size[0], r['ground_row'])], fill=(0, 160, 255), width=1)
    return im


_REF = {}


def ref_pose_cache(rig, prof, r):
    return dict(_REF.get('pose', {}))


def label(im, text, color='black'):
    out = Image.new('RGB', (im.width, im.height + 20), 'white')
    out.paste(im, (0, 20))
    ImageDraw.Draw(out).text((4, 2), text, fill=color, font=FONT)
    return out


def grid(ims, cols=4):
    w, h = ims[0].size
    g = Image.new('RGB', (cols * w, ((len(ims) + cols - 1) // cols) * h), 'white')
    for i, im in enumerate(ims):
        g.paste(im, ((i % cols) * w, (i // cols) * h))
    return g


def render_decode_sheets(name):
    cfg = load_run(name)
    d = run_dir(name)
    rig, prof = R.load_rig(cfg['rig']), R.profile(cfg['rig'])
    _REF['pose'] = ref_pose(cfg, 'from')
    rf = json.loads(json.dumps(rig))
    for bn in rf['Bones']:
        bn['Length'] *= cfg.get('lengths', {}).get(bn['Name'], 1.0)
    _REF['rig_fit'] = rf
    dec = json.load(open(os.path.join(d, 'decoded.json')))
    T = 260
    ov, rb, cl = [], [], []
    for r in dec:
        cell = Image.open(os.path.join(d, 'cells', f'{r["frame"]:02d}.png')).convert('RGB')
        tag = f'{r["frame"]}' + ('  REJECTED' if r.get('rejected') else
                                 ('  ⚠ ' + ' '.join(r['flags'])) if r.get('flags') else '')
        col = (200, 0, 0) if r.get('flags') or r.get('rejected') else (0, 0, 0)
        ov.append(label(overlay(cell, r, rig, prof).resize((T, T)), tag[:44], col))
        obs = F.observe(cell, prof)
        a = np.full((cell.height, cell.width, 3), 255, np.uint8)
        src = np.asarray(cell)
        a[(src.min(2) < 200) & ~obs['ok']] = (205, 205, 205)
        for i, n in enumerate(obs['names']):
            b = obs['bones'].get(n)
            if b is None:
                continue
            col_ = prof['weapon']['color'] if n == '__weapon' else prof['colors'][n]
            a[obs['ok'] & (obs['lab'] == i)] = col_
        cl.append(label(Image.fromarray(a).resize((T, T)), f'{r["frame"]}'))
        if 'pose' in r:
            full = dict(_REF['pose'])
            full.update({k: (v, r.get('stretch', {}).get(k)) for k, v in r['pose'].items()})
            w = (prof['weapon']['parent'], r['knife'], prof['weapon']['length']) if r.get('knife') is not None else None
            rb.append(label(R.render_cell(rig, prof, full, T, weapon=w), f'{r["frame"]}'))
        else:
            rb.append(label(Image.new('RGB', (T, T), (255, 235, 235)), f'{r["frame"]} rejected'))
    grid(ov).save(os.path.join(d, 'decode_overlay.png'))
    grid(cl).save(os.path.join(d, 'decode_classified.png'))
    grid(rb).save(os.path.join(d, 'decode_rebuilt.png'))


def sample_clip(clip, t):
    kfs = clip['Keyframes']
    get = lambda k: {x['Bone']: (x['Rotation'], x.get('Stretch')) for x in k['Bones']}
    for a, b in zip(kfs, kfs[1:]):
        if a['Time'] <= t <= b['Time']:
            u = (t - a['Time']) / max(1e-9, b['Time'] - a['Time'])
            u = u * u * (3 - 2 * u)
            ra, rb = get(a), get(b)
            return {k: (ra[k][0] + u * F.wrap(rb[k][0] - ra[k][0]), ra[k][1]) for k in ra}
    return get(kfs[-1])


def render_playback(name, clip, rig, prof):
    d = run_dir(name)
    frames = []
    for i in range(48):
        p = sample_clip(clip, i / 47)
        w = None
        if 'knife' in p:
            w = (prof['weapon']['parent'], p.pop('knife')[0], prof['weapon']['length'])
        frames.append(R.render_cell(rig, prof, p, 300, weapon=w))
    frames[0].save(os.path.join(d, 'playback.gif'), save_all=True, append_images=frames[1:], duration=42, loop=0)
    src = [Image.open(f).convert('RGB').resize((300, 300)) for f in sorted(glob.glob(os.path.join(d, 'cells', '*.png')))]
    if src:
        src[0].save(os.path.join(d, 'source.gif'), save_all=True, append_images=src[1:], duration=125, loop=0)


def cmd_report(a):
    cfg = load_run(a.run)
    d = run_dir(a.run)
    dec = json.load(open(os.path.join(d, 'decoded.json'))) if os.path.exists(os.path.join(d, 'decoded.json')) else []
    rows = ''.join(
        f'<tr><td>{r["frame"]}</td><td>{"REJECTED" if r.get("rejected") else r.get("cost", "")}</td>'
        f'<td style="color:#c00">{" ".join(r.get("flags", []))}</td><td>{" ".join(r.get("notes", []))}</td>'
        f'<td>{"" if r.get("knife") is None else round(r["knife"], 2)}</td></tr>' for r in dec)
    img = lambda f, w='100%': (f'<figure><a href="{f}"><img src="{f}" style="max-width:{w}"></a>'
                               f'<figcaption>{f}</figcaption></figure>' if os.path.exists(os.path.join(d, f)) else '')
    prompts = sorted(glob.glob(os.path.join(d, 'gen_*.prompt.txt')))
    gens = sorted(os.path.basename(f) for f in glob.glob(os.path.join(d, 'gen_*_*.png')))
    digest = open(os.path.join(d, 'digest.txt')).read() if os.path.exists(os.path.join(d, 'digest.txt')) else ''
    html = f'''<!doctype html><html><head><meta charset="utf-8"><title>{a.run}</title><style>
body{{font:14px/1.5 -apple-system,sans-serif;max-width:1500px;margin:20px auto;padding:0 16px;background:#fff;color:#222}}
img{{border:1px solid #ddd}}figure{{display:inline-block;margin:6px;vertical-align:top}}figcaption{{font-size:12px;color:#666}}
pre{{background:#f5f5f5;padding:10px;white-space:pre-wrap;max-height:420px;overflow:auto}}td,th{{padding:2px 10px;text-align:left}}
h2{{border-bottom:1px solid #ddd;margin-top:36px}}</style></head><body>
<h1>{a.run}</h1><p>rig <b>{cfg["rig"]}</b> · from <code>{cfg["from"]}</code>{" · to <code>" + cfg["to"] + "</code>" if cfg.get("to") else ""}
 · move: <i>{cfg.get("move", "")}</i></p>
<h2>1 · Image model input</h2>{img("ref.png", "640px")}{img("legend.png", "380px")}
<pre>{open(prompts[-1]).read() if prompts else ""}</pre>
<h2>2 · Image model output</h2>{"".join(img(g, "520px") for g in gens)}
<h2>3 · Decode</h2><p>Overlay: fitted rig (true lengths, black) over the source; blue = detected ground line; red caption = flagged.</p>
{img("decode_overlay.png")}{img("decode_classified.png", "49%")}{img("decode_rebuilt.png", "49%")}
<table><tr><th>frame</th><th>cost</th><th>flags</th><th>notes</th><th>knife</th></tr>{rows}</table>
<h2>4 · Clip</h2>{img("source.gif", "300px")}{img("playback.gif", "300px")}
<p>Left: the image model's frames (8 fps). Right: the assembled clip, smoothstep-interpolated (24 fps).</p>
<h2>5 · In engine</h2>{img("engine_strip.png")}<pre>{digest}</pre></body></html>'''
    open(os.path.join(d, 'index.html'), 'w').write(html)
    print('report ->', os.path.join(d, 'index.html'))


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    p = sub.add_parser('refs'); p.add_argument('--run', required=True); p.add_argument('--rig', default='biped')
    p.add_argument('--from', required=True); p.add_argument('--to'); p.add_argument('--weapon', action='store_true')
    p.add_argument('--markers', choices=['dots', 'shapes']); p.add_argument('--example', help='clip for a format example sheet')
    p = sub.add_parser('gen'); p.add_argument('--run', required=True); p.add_argument('--move', required=True)
    p.add_argument('--frames', type=int, default=16)
    p = sub.add_parser('decode'); p.add_argument('--run', required=True); p.add_argument('--sheet')
    p.add_argument('--planar', action='store_true', help='2D-only fit (no tilt/swivel, no Stretch)')
    p.add_argument('--auto-joints', action='store_true', help='pin every unmarked frame to the candidate joints')
    p = sub.add_parser('joints'); p.add_argument('--run', required=True); p.add_argument('--frames')
    p.add_argument('--current', action='store_true', help="draw joints.json instead of fresh candidates")
    p = sub.add_parser('synth'); p.add_argument('--run', required=True); p.add_argument('--rig', default='biped')
    p.add_argument('--clip', required=True); p.add_argument('--frames', type=int, default=16)
    p.add_argument('--fore', action='store_true'); p.add_argument('--seed', type=int, default=0)
    p = sub.add_parser('score'); p.add_argument('--run', required=True)
    p = sub.add_parser('detect'); p.add_argument('--run', required=True)
    p = sub.add_parser('solve'); p.add_argument('--run', required=True); p.add_argument('--frames')
    p.add_argument('--recalibrate', action='store_true')
    p = sub.add_parser('points'); p.add_argument('--run', required=True); p.add_argument('--frames')
    p = sub.add_parser('assemble'); p.add_argument('--run', required=True)
    p.add_argument('--duration', type=float, default=0.5); p.add_argument('--loop', action='store_true')
    p.add_argument('--contacts', action='store_true')
    p.add_argument('--steep-limit', type=float, default=7.0, help='rad per unit phase (digest gate is 8)')
    p.add_argument('--keep', help='frame range to keep, e.g. 4-13 (trim to the strike)')
    p.add_argument('--key-tol', type=float, default=0.12, help='key reduction tolerance, rad (0 = keep all)')
    p.add_argument('--no-ground', action='store_true', help='skip the per-key com anchor (feet-on-floor)')
    p = sub.add_parser('verify'); p.add_argument('--run', required=True)
    p = sub.add_parser('report'); p.add_argument('--run', required=True)
    a = ap.parse_args()
    {'refs': cmd_refs, 'gen': cmd_gen, 'decode': cmd_decode, 'points': cmd_points, 'assemble': cmd_assemble,
     'joints': cmd_joints, 'synth': cmd_synth, 'score': cmd_score, 'solve': cmd_solve, 'detect': cmd_detect,
     'verify': cmd_verify, 'report': cmd_report}[a.cmd](a)


if __name__ == '__main__':
    main()
