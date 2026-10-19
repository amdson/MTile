"""Joint marks: the agent's reading of a frame, as rough pixel positions.

joints.json (per run):
    {"frames": {"1": {"__root": [x, y],          # hip / root
                      "chest": [x, y],           # neck: tip of the torso bone
                      "arm_r_upper": [x, y],     # near elbow  (a bone's KEY = its TIP)
                      "arm_r_lower": [x, y],     # near hand
                      "leg_l_upper": "hidden",   # far knee not visible
                      "__blade": [x, y]}}}       # blade tip
Keys are bone names (their tip), so the format is rig-agnostic. Frames without an entry
are decoded warm-started from the previous frame. `candidates` proposes marks from the
strokes; the agent accepts, moves, or hides them — it never writes angles.
"""
import numpy as np
from PIL import Image, ImageDraw, ImageFont
import fit as F

FONT = ImageFont.truetype('/System/Library/Fonts/Supplemental/Arial.ttf', 13)


def short_label(n, prof):
    if n == '__root':
        return 'hip'
    if n == '__blade':
        return 'blade'
    if n == 'chest':
        return 'neck'
    side = {'near': 'n', 'far': 'f'}.get(prof['sides'].get(n, ''), '')
    if 'arm' in n:
        return side + ('Elbow' if n.endswith('upper') else 'Hand')
    if 'leg' in n:
        return side + ('Knee' if n.endswith('upper') else 'Foot')
    return n


def candidates(obs, prof, rig):
    """Proposed joint marks {key: [x, y] | 'hidden'} and notes on anything ambiguous."""
    out, notes = {}, []
    t = obs.get('torso')
    if t is not None:
        out['__root'] = [round(float(v)) for v in t['hip']]
        out['chest'] = [round(float(v)) for v in t['neck']]
    for ch in F.limb_chains(prof, rig):
        if len(ch) < 2:
            continue
        u, l = ch[0], ch[1]
        su, sl = obs['bones'].get(u), obs['bones'].get(l)
        anchor = np.asarray(t['neck'] if 'arm' in u else t['hip'], float) if t is not None else None
        if su is not None and sl is not None:
            pairs = [(np.linalg.norm(a - b), i, j) for i, a in enumerate(su['ends']) for j, b in enumerate(sl['ends'])]
            _, i, j = min(pairs)
            out[u] = [round(float(v)) for v in (su['ends'][i] + sl['ends'][j]) / 2]
            out[l] = [round(float(v)) for v in sl['ends'][1 - j]]
        elif su is not None:
            ends = su['ends']
            far = max(ends, key=lambda e: np.linalg.norm(e - anchor)) if anchor is not None else ends[1]
            out[u] = [round(float(v)) for v in far]
            out[l] = 'hidden'
            notes.append(f'{short_label(l, prof)} not visible')
        elif sl is not None:
            out[u] = 'hidden'
            far = max(sl['ends'], key=lambda e: np.linalg.norm(e - anchor)) if anchor is not None else sl['ends'][1]
            out[l] = [round(float(v)) for v in far]
            notes.append(f'{short_label(u, prof)} not visible; {short_label(l, prof)} guessed as the far end')
        else:
            out[u] = out[l] = 'hidden'
    blade = obs['bones'].get('__weapon')
    hand = prof['weapon']['parent']
    if blade is not None and isinstance(out.get(hand), list):
        h = np.asarray(out[hand], float)
        out['__blade'] = [round(float(v)) for v in max(blade['ends'], key=lambda e: np.linalg.norm(e - h))]
    return out, notes


def review_image(cell, marks, prof, title='', scale=2):
    """The cell enlarged with every mark drawn and labelled — what the agent checks."""
    im = cell.resize((cell.width * scale, cell.height * scale), Image.NEAREST)
    im = Image.blend(Image.new('RGB', im.size, 'white'), im, 0.6)
    dr = ImageDraw.Draw(im)
    for k, v in marks.items():
        if not isinstance(v, (list, tuple)):
            continue
        x, y = v[0] * scale, v[1] * scale
        col = (0, 0, 0) if k in ('__root', 'chest', '__blade') else tuple(int(c * 0.6) for c in prof['colors'].get(k, (0, 0, 0)))
        dr.line([(x - 7, y), (x + 7, y)], fill=col, width=3)
        dr.line([(x, y - 7), (x, y + 7)], fill=col, width=3)
        dr.text((x + 8, y - 16), short_label(k, prof), fill=col, font=FONT)
    hidden = [short_label(k, prof) for k, v in marks.items() if v == 'hidden']
    dr.text((6, 4), title + ('   hidden: ' + ', '.join(hidden) if hidden else ''), fill='black', font=FONT)
    return im
