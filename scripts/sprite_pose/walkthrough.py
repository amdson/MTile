"""Build a browsable, step-by-step visual walkthrough of the sprite<->clip pipeline.

    python walkthrough.py <out_dir> [--raw <codex generated_images session dir> ...]
                          [--discarded <codex-delivered png>] [--before <old frame panel png>]

Stages written (open <out_dir>/index.html):
  1  clip -> sprite   : authored rotations, FK joint positions, rendered references
  2  image model      : the exact prompt + attachments, every raw generation
  3  sprite -> clip   : per-stage sheets (classify, axis fit, joint chain, overlay)
                        and per-frame stage strips, for the slash
  4  the clip         : decoded clip JSON, re-rendered (round trip), GIFs
  5  failure case     : the same stages for the walk, where added feet break it
"""
import json, math, os, shutil, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from stick import COLORS, BONES, ORDER, fk, draw_pose, sole_y, render_sheet, pose_from_clip, load_clip
from decode import LIMBS, decode_cell

FONT = ImageFont.truetype('/System/Library/Fonts/Supplemental/Arial.ttf', 15)
BIG = ImageFont.truetype('/System/Library/Fonts/Supplemental/Arial.ttf', 22)
MONO = ImageFont.truetype('/System/Library/Fonts/Menlo.ttc', 13)
SHORT = {'chest': 'torso', 'arm_r_upper': 'nUA', 'arm_r_lower': 'nFA', 'leg_r_upper': 'nTH',
         'leg_r_lower': 'nSH', 'arm_l_upper': 'fUA', 'arm_l_lower': 'fFA', 'leg_l_upper': 'fTH',
         'leg_l_lower': 'fSH'}


def cells(path, grid=4):
    im = Image.open(path).convert('RGB')
    W, H = im.size
    cw, ch = W / grid, H / grid
    return [im.crop((int((i % grid) * cw), int((i // grid) * ch),
                     int((i % grid + 1) * cw), int((i // grid + 1) * ch))) for i in range(grid * grid)]


def captioned(img, text, w=None):
    w = w or img.width
    out = Image.new('RGB', (w, img.height + 26), 'white')
    out.paste(img, (0, 26))
    ImageDraw.Draw(out).text((6, 4), text, fill='black', font=FONT)
    return out


def grid_sheet(imgs, cols=4):
    w, h = imgs[0].size
    rows = (len(imgs) + cols - 1) // cols
    s = Image.new('RGB', (cols * w, rows * h), 'white')
    d = ImageDraw.Draw(s)
    for i, im in enumerate(imgs):
        x, y = (i % cols) * w, (i // cols) * h
        s.paste(im, (x, y))
        d.rectangle([x, y, x + w - 1, y + h - 1], outline=(190, 190, 190))
    return s


def hstrip(imgs, gap=8):
    h = max(i.height for i in imgs)
    s = Image.new('RGB', (sum(i.width for i in imgs) + gap * (len(imgs) - 1), h), 'white')
    x = 0
    for i in imgs:
        s.paste(i, (x, 0))
        x += i.width + gap
    return s


def faded(cell, k=0.3):
    return Image.blend(Image.new('RGB', cell.size, 'white'), cell, k)


# ---- per-stage renderings of one cell -------------------------------------------------

def stage_classify(cell, dbg):
    a = np.full((cell.height, cell.width, 3), 255, np.uint8)
    lab, ok = dbg['lab'], dbg['ok']
    src = np.asarray(cell)
    nonwhite = src.min(2) < 200
    a[nonwhite & ~ok] = (200, 200, 200)  # ink the classifier ignored (torso, head, numbers)
    for i, b in enumerate(LIMBS):
        a[ok & (lab == i)] = COLORS[b]
    return Image.fromarray(a)


def stage_axes(cell, dbg):
    im = faded(stage_classify(cell, dbg), 0.45)
    d = ImageDraw.Draw(im)
    for b in LIMBS:
        sg = dbg['segs'][b]
        if sg is None:
            continue
        c, v, e0, e1, _ = sg
        d.line([tuple(e0), tuple(e1)], fill='black', width=3)
        for e in (e0, e1):
            d.ellipse([e[0] - 5, e[1] - 5, e[0] + 5, e[1] + 5], outline='black', width=2)
        d.text((c[0] + 6, c[1] - 8), SHORT[b], fill='black', font=FONT)
    return im


def stage_chain(cell, dbg):
    im = faded(cell, 0.3)
    d = ImageDraw.Draw(im)
    for b, s0, s1 in dbg['chain']:
        col = COLORS[b]
        d.line([tuple(s0), tuple(s1)], fill='black', width=7)
        d.line([tuple(s0), tuple(s1)], fill=col, width=4)
        # arrowhead at the child end
        v = (s1 - s0) / (np.linalg.norm(s1 - s0) + 1e-9)
        n = np.array([-v[1], v[0]])
        tip = s1
        d.polygon([tuple(tip), tuple(tip - 12 * v + 6 * n), tuple(tip - 12 * v - 6 * n)], fill='black')
    for key, name in (('hip', 'hip'), ('sh', 'shoulder')):
        if key in dbg:
            p = dbg[key]
            d.line([(p[0] - 8, p[1] - 8), (p[0] + 8, p[1] + 8)], fill='black', width=3)
            d.line([(p[0] - 8, p[1] + 8), (p[0] + 8, p[1] - 8)], fill='black', width=3)
            d.text((p[0] + 10, p[1] - 6), name, fill='black', font=FONT)
    return im


def rig_scale(dbg):
    r = [np.linalg.norm(s1 - s0) / BONES[b]['Length'] for b, s0, s1 in dbg['chain']]
    return float(np.median(r)) if r else 1.0


def stage_overlay(cell, dbg, rot):
    """Rebuilt rig (TRUE bone lengths) drawn over the source, anchored at the detected hip."""
    im = faded(cell, 0.55)
    if rot is None:
        return im
    d = ImageDraw.Draw(im)
    s = rig_scale(dbg)
    hx, hy = dbg['hip']
    seg = fk(rot)
    P = lambda q: (hx + q[0] * s, hy + q[1] * s)
    for b in ['chest'] + LIMBS:
        a, e = seg[b]
        d.line([P(a), P(e)], fill='black', width=3)
        for q in (P(a), P(e)):
            d.ellipse([q[0] - 3, q[1] - 3, q[0] + 3, q[1] + 3], fill='white', outline='black')
    a, e = seg['head']
    c = P(((a[0] + e[0]) / 2, (a[1] + e[1]) / 2))
    r = BONES['head']['Length'] * s * 0.55
    d.ellipse([c[0] - r, c[1] - r, c[0] + r, c[1] + r], outline='black', width=3)
    return im


def render_pose(rot, size=300, label=None):
    im = Image.new('RGB', (size, size), 'white')
    d = ImageDraw.Draw(im)
    seg = fk(rot)
    s = size / 64.0
    ground = size * 0.86
    d.line([(8, ground), (size - 8, ground)], fill=(150, 150, 150), width=2)
    draw_pose(d, seg, size / 2, ground - sole_y(seg) * s, s, max(3, size // 32))
    if label:
        d.text((6, 4), label, fill=(120, 120, 120), font=FONT)
    return im


def decode_sheet(path, outdir, title, detail_frames):
    os.makedirs(outdir, exist_ok=True)
    cs = cells(path)
    T = 300
    rows = {k: [] for k in ('source', 'classified', 'axes', 'chain', 'overlay')}
    results = []
    for i, cell in enumerate(cs):
        dbg = {}
        rot, flags = decode_cell(cell, dbg)
        results.append({'frame': i + 1, 'rot': rot, 'flags': flags})
        tag = f'{i + 1}' + (('  ⚠ ' + ', '.join(flags)) if flags else '')
        st = {
            'source': cell,
            'classified': stage_classify(cell, dbg),
            'axes': stage_axes(cell, dbg),
            'chain': stage_chain(cell, dbg) if 'hip' in dbg else faded(cell),
            'overlay': stage_overlay(cell, dbg, rot) if 'hip' in dbg else faded(cell),
        }
        for k, im in st.items():
            rows[k].append(captioned(im.resize((T, T)), tag if k != 'source' else str(i + 1)))
        if i + 1 in detail_frames:
            panels = [captioned(st[k].resize((360, 360)), f'{n}. {k}') for n, k in enumerate(st, 1)]
            panels.append(captioned(render_pose(rot, 360) if rot else Image.new('RGB', (360, 360), 'white'),
                                    '6. rebuilt pose (rig lengths)'))
            txt = Image.new('RGB', (330, 386), 'white')
            td = ImageDraw.Draw(txt)
            td.text((6, 4), f'7. local rotations (rad), frame {i + 1}', fill='black', font=FONT)
            for j, b in enumerate(['chest'] + LIMBS):
                v = rot.get(b) if rot else None
                td.text((10, 34 + j * 22), f'{b:<12} {v:+.3f}' if v is not None else f'{b:<12}   —',
                        fill='black', font=MONO)
            for j, f in enumerate(flags):
                td.text((10, 250 + j * 20), '⚠ ' + f, fill=(200, 0, 0), font=MONO)
            panels.append(txt)
            hstrip(panels).save(os.path.join(outdir, f'frame_{i + 1:02d}_stages.png'))
    for n, (k, ims) in enumerate(rows.items()):
        grid_sheet(ims).save(os.path.join(outdir, f'sheet_{n}_{k}.png'))
    json.dump(results, open(os.path.join(outdir, 'decoded_rotations.json'), 'w'), indent=1)
    return results


# ---- clip I/O + playback ------------------------------------------------------------

def to_clip(results, name, duration):
    good = [r for r in results if r['rot']]
    n = len(good)
    kfs = []
    for j, r in enumerate(good):
        kfs.append({'Time': round(j / (n - 1), 4),
                    'Bones': [{'Bone': b, 'Rotation': round(r['rot'].get(b, BONES[b]['Rotation']), 5)}
                              for b in ORDER]})
    return {'Name': name, 'Type': 'GptDraft', 'Skeleton': 'biped', 'Duration': duration,
            'Loop': False, 'Keyframes': kfs}


def sample_clip(clip, t):
    kfs = clip['Keyframes']
    for a, b in zip(kfs, kfs[1:]):
        if a['Time'] <= t <= b['Time']:
            u = (t - a['Time']) / max(1e-9, b['Time'] - a['Time'])
            ra = {x['Bone']: x['Rotation'] for x in a['Bones']}
            rb = {x['Bone']: x['Rotation'] for x in b['Bones']}
            wrap = lambda x: (x + math.pi) % (2 * math.pi) - math.pi
            return {k: ra[k] + u * wrap(rb[k] - ra[k]) for k in ra}
    return {x['Bone']: x['Rotation'] for x in kfs[-1]['Bones']}


def save_gif(frames, path, ms):
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=ms, loop=0)


# ---- main ---------------------------------------------------------------------------

def main(out, raw_dirs, discarded=None, before=None):
    if os.path.exists(out):
        shutil.rmtree(out)
    S = os.path.join(HERE, 'samples')
    P = os.path.join(HERE, 'prompts')

    # 1. clip -> sprite
    d1 = os.path.join(out, '1_clip_to_sprite')
    os.makedirs(d1)
    start, end = pose_from_clip('idle', 0), pose_from_clip('groundslash3', 0.5)
    json.dump({'source': 'Backups/clips-20260918-111934.tar.gz',
               'start (idle.json @ t=0)': start, 'end (groundslash3.json @ t=0.5)': end},
              open(os.path.join(d1, 'a_authored_rotations.json'), 'w'), indent=1)
    for name, rot in (('start', start), ('end', end)):
        seg = fk(rot)
        im = render_pose(rot, 560)
        d = ImageDraw.Draw(im)
        s = 560 / 64.0
        ox, oy = 280, 560 * 0.86 - sole_y(seg) * s
        d.text((8, 8), f'{name}: FK joint positions (rig units, +y down, hip = 0,0)', fill='black', font=FONT)
        for b in ['chest'] + LIMBS:
            x, y = seg[b][1]
            q = (ox + x * s, oy + y * s)
            d.ellipse([q[0] - 4, q[1] - 4, q[0] + 4, q[1] + 4], fill='black')
            d.text((q[0] + 7, q[1] - 7), f'{SHORT[b]} ({x:.1f},{y:.1f})', fill='black', font=MONO)
        im.save(os.path.join(d1, f'b_fk_{name}.png'))
    shutil.copy(os.path.join(S, 'ref_pair.png'), os.path.join(d1, 'c_rendered_reference_pair.png'))
    shutil.copy(os.path.join(S, 'legend.png'), os.path.join(d1, 'd_color_legend.png'))

    # 2. image model in/out
    d2 = os.path.join(out, '2_image_model')
    os.makedirs(d2)
    for f in os.listdir(P):
        shutil.copy(os.path.join(P, f), os.path.join(d2, 'prompt_' + f))
    shutil.copy(os.path.join(S, 'ref_pair.png'), os.path.join(d2, 'input_attachment_1_ref_pair.png'))
    shutil.copy(os.path.join(S, 'legend.png'), os.path.join(d2, 'input_attachment_2_legend.png'))
    gens = []
    for rd in raw_dirs:
        for f in sorted(os.listdir(rd), key=lambda f: os.path.getmtime(os.path.join(rd, f))):
            gens.append(os.path.join(rd, f))
    for j, g in enumerate(gens, 1):
        shutil.copy(g, os.path.join(d2, f'output_generation_{j}.png'))
    if discarded:
        shutil.copy(discarded, os.path.join(d2, 'output_zz_DISCARDED_codex_redrew_it_with_PIL.png'))
    if not gens:
        shutil.copy(os.path.join(S, 'gpt_slash_a.png'), os.path.join(d2, 'output_generation_a.png'))
        shutil.copy(os.path.join(S, 'gpt_slash_b.png'), os.path.join(d2, 'output_generation_b.png'))

    # 3. sprite -> clip (slash)
    d3 = os.path.join(out, '3_sprite_to_clip_slash')
    res = decode_sheet(os.path.join(S, 'gpt_slash_b.png'), d3, 'slash', {1, 5, 10, 16})

    # 4. the clip + round trip
    d4 = os.path.join(out, '4_clip_and_roundtrip')
    os.makedirs(d4)
    clip = to_clip(res, 'gptslash', 0.5)
    json.dump(clip, open(os.path.join(d4, 'gptslash.json'), 'w'), indent=2)
    src = cells(os.path.join(S, 'gpt_slash_b.png'))
    rt = [render_pose(r['rot'], 300, str(r['frame'])) for r in res if r['rot']]
    grid_sheet(rt).save(os.path.join(d4, 'a_clip_rerendered_as_sprites.png'))
    pairs = [hstrip([captioned(c.resize((240, 240)), f'{k + 1} GPT'),
                     captioned(render_pose(r['rot'], 240), f'{k + 1} decoded clip')], gap=2)
             for k, (c, r) in enumerate(zip(src, res))]
    grid_sheet(pairs).save(os.path.join(d4, 'b_gpt_vs_decoded_side_by_side.png'))
    save_gif([c.resize((300, 300)) for c in src], os.path.join(d4, 'c_gpt_frames.gif'), 125)
    save_gif([render_pose(sample_clip(clip, t / 47), 300) for t in range(48)],
             os.path.join(d4, 'd_decoded_clip_interpolated.gif'), 42)

    # 5. failure case (walk)
    d5 = os.path.join(out, '5_failure_case_walk')
    shutil.copy(os.path.join(S, 'gpt_walk.png'), os.path.join(out, 'walk_source_tmp.png'))
    wres = decode_sheet(os.path.join(S, 'gpt_walk.png'), d5, 'walk', {1, 2, 9})
    os.remove(os.path.join(out, 'walk_source_tmp.png'))
    if before:
        shutil.copy(before, os.path.join(d5, 'before_fix_frame_02_stages.png'))
    wclip = to_clip(wres, 'gptwalk', 0.8)
    save_gif([render_pose(sample_clip(wclip, t / 47), 300) for t in range(48)],
             os.path.join(d5, 'decoded_clip_interpolated.gif'), 42)

    write_index(out, res, wres)
    print('wrote', out)


def write_index(out, res, wres):
    def imgs(d, names, w='100%'):
        return ''.join(f'<figure><a href="{d}/{n}"><img src="{d}/{n}" style="max-width:{w}"></a>'
                       f'<figcaption>{n}</figcaption></figure>' for n in names)
    ls = lambda d: sorted(os.listdir(os.path.join(out, d)))
    d2 = ls('2_image_model')
    prompt = open(os.path.join(out, '2_image_model', 'prompt_slash.txt')).read()
    bad = sum(1 for r in wres if r['flags'])
    html = f'''<!doctype html><html><head><meta charset="utf-8"><title>Sprite Pose Walkthrough</title>
<style>body{{font:15px/1.5 -apple-system,sans-serif;max-width:1400px;margin:24px auto;padding:0 16px;background:#fff;color:#222}}
img{{border:1px solid #ddd;max-width:100%}}figure{{display:inline-block;margin:6px;vertical-align:top}}
figcaption{{font-size:12px;color:#666}}pre{{background:#f5f5f5;padding:10px;white-space:pre-wrap}}h2{{margin-top:48px;border-bottom:1px solid #ddd}}</style></head><body>
<h1>Sprite ↔ clip pipeline, step by step</h1>
<p>Slash workflow end to end, then the walk as the failure case. Click any image for full size.</p>

<h2>1 · Clip → sprite</h2>
<p>Authored rotations (<code>a_authored_rotations.json</code>) → forward kinematics → joint positions → rendered in the
stick format (near limbs warm, far limbs cool). This pair is what the image model receives.</p>
{imgs('1_clip_to_sprite', ['b_fk_start.png', 'b_fk_end.png'], '560px')}
{imgs('1_clip_to_sprite', ['c_rendered_reference_pair.png'], '700px')}{imgs('1_clip_to_sprite', ['d_color_legend.png'], '420px')}

<h2>2 · Image model: input → output</h2>
<p><code>codex exec -m gpt-6-luna</code>, attachments + this prompt (walk/pixel prompts are in the folder):</p>
<pre>{prompt}</pre>
{imgs('2_image_model', [n for n in d2 if n.startswith('input')], '420px')}
<p>Every raw generation from that one request (Codex retried on its own; the last file is the sheet Codex actually
delivered — it redrew the poses itself in PIL, i.e. code-authored poses, and was discarded):</p>
{imgs('2_image_model', [n for n in d2 if n.startswith('output')], '640px')}

<h2>3 · Sprite → clip, stage by stage (slash)</h2>
<p>Per frame: <b>1 source</b> cell → <b>2 classified</b> (each pixel → nearest legend color; grey = ink ignored: torso, head,
numbers) → <b>3 axes</b> (principal axis + extreme endpoints per bone) → <b>4 chain</b> (hip/shoulder estimated from the
thighs'/upper-arms' top ends; each bone oriented away from its parent joint, arrow = parent→child) → <b>5 overlay</b> (pose
rebuilt at the rig's TRUE bone lengths, anchored at the hip, over the source) → <b>6</b> rebuilt pose → <b>7</b> numbers.</p>
{imgs('3_sprite_to_clip_slash', [n for n in ls('3_sprite_to_clip_slash') if n.startswith('frame')])}
<p>Whole sheet at each stage:</p>
{imgs('3_sprite_to_clip_slash', [n for n in ls('3_sprite_to_clip_slash') if n.startswith('sheet')], '640px')}

<h2>4 · The clip, and back to sprites</h2>
<p><code>gptslash.json</code> is a real <code>SkeletonStates</code>-format clip (16 keys, evenly spaced, 0.5 s). Re-rendered
from the clip's rotations alone, side by side with the GPT frames, and as playback (GIFs: GPT frames at 8 fps; decoded clip
interpolated at 24 fps).</p>
{imgs('4_clip_and_roundtrip', ['b_gpt_vs_decoded_side_by_side.png'])}
{imgs('4_clip_and_roundtrip', ['c_gpt_frames.gif', 'd_decoded_clip_interpolated.gif'], '300px')}
{imgs('4_clip_and_roundtrip', ['a_clip_rerendered_as_sprites.png'], '700px')}

<h2>5 · Walk: a decode bug found by inspecting these stages ({bad}/16 frames flagged now)</h2>
<p><b>Before the fix</b> (11/16 frames flagged "backward knee"): in stage 3 the near-shin axis (nSH) runs up past the knee
to near the hip. Three stray pixels, RGB ≈ (252,185,60) — the anti-aliased rim of the orange forearm — were classified as
yellow shin, stretched its extreme endpoint, and stage 4 then oriented the shin from the wrong end. The fix keeps only
each color's largest connected blob. <b>After</b>: every walk frame decodes; the added feet only tilt the shin fit
slightly (compare stage 5 overlays).</p>
{imgs('5_failure_case_walk', [n for n in ls('5_failure_case_walk') if n.startswith('before')])}
<p>After the fix:</p>
{imgs('5_failure_case_walk', [n for n in ls('5_failure_case_walk') if n.startswith('frame')])}
{imgs('5_failure_case_walk', [n for n in ls('5_failure_case_walk') if n.startswith('sheet')], '640px')}
{imgs('5_failure_case_walk', ['decoded_clip_interpolated.gif'], '300px')}
</body></html>'''
    open(os.path.join(out, 'index.html'), 'w').write(html)


if __name__ == '__main__':
    args = sys.argv[1:]
    opt = lambda k: next((args[i + 1] for i, a in enumerate(args) if a == k), None)
    raw = [args[i + 1] for i, a in enumerate(args) if a == '--raw']
    main(args[0], raw, opt('--discarded'), opt('--before'))
