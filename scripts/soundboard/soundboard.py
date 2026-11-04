#!/usr/bin/env python3
"""Soundboard: swap, audition and crop game sounds while MTile runs.

    python3 scripts/soundboard/soundboard.py seed     # first time: build the library + sound_map.json
    python3 scripts/soundboard/soundboard.py serve    # open http://localhost:8765, then run the game
    python3 scripts/soundboard/soundboard.py ship     # bake sound_map.json into Assets/Sounds + content

Pieces (see scripts/soundboard/README.md):
  Audio/sound_map.json   committed master mapping: event stem -> list of clips, each a library
                         file plus crop/gain/fades. The source files are never edited.
  library                labeled folders of source audio, outside the repo (default
                         ~/dev/MTile-sfx-library, or MTILE_SFX_LIBRARY / "library" in the map).
                         Add sounds by dropping files or folders in; the page rescans on load.
  Audio/dev_cache/       gitignored. Every mapped event rendered to WAV + manifest.json. The
                         game (Audio/SoundDevOverrides.cs) watches the manifest and swaps clips
                         live. Delete the folder to go back to the shipped clips.

Rendering uses the same chain as scripts/build-sfx.sh (lead-silence trim, -16 LUFS loudnorm,
tail fade, mono 22.05 kHz), applied after the crop, so what you hear in game is what ships.
Stdlib only; needs ffmpeg (and, for `ship`, an ffmpeg with libvorbis — on macOS the keg-only
ffmpeg-full is picked up automatically).
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
MAP_PATH = os.path.join(REPO, 'Audio', 'sound_map.json')
KIND_CS = os.path.join(REPO, 'Audio', 'SoundKind.cs')
SOUNDS = os.path.join(REPO, 'Assets', 'Sounds')
CACHE = os.path.join(REPO, 'Audio', 'dev_cache')
PREVIEW = os.path.join(CACHE, 'preview')
STATE = os.path.join(CACHE, 'render_state.json')
DEFAULT_LIBRARY = '~/dev/MTile-sfx-library'
AUDIO_EXT = ('.wav', '.flac', '.ogg', '.mp3', '.aif', '.aiff', '.m4a')

RATE, LUFS, QUALITY = 22050, -16.0, 4
# Defaults for a clip entry; a clip equal to these on every key is "uncropped".
CLIP_DEFAULTS = {'start': 0.0, 'end': None, 'gain_db': 0.0, 'fade_in_ms': 0.0, 'fade_out_ms': 5.0,
                 'trim_silence': True}

_lock = threading.Lock()


# ── ffmpeg ──────────────────────────────────────────────────────────────────────────────
def ffmpeg_bin(need_vorbis=False):
    cands = ['/opt/homebrew/opt/ffmpeg-full/bin/ffmpeg', '/usr/local/opt/ffmpeg-full/bin/ffmpeg',
             shutil.which('ffmpeg')]
    for c in cands:
        if not c or not os.path.exists(c):
            continue
        if not need_vorbis:
            return c
        enc = subprocess.run([c, '-hide_banner', '-encoders'], capture_output=True, text=True).stdout
        if 'libvorbis' in enc:
            return c
    sys.exit('ffmpeg%s not found (macOS: brew install ffmpeg-full; Windows: winget install Gyan.FFmpeg)'
             % (' with libvorbis' if need_vorbis else ''))


def lead_silence(src, start, dur):
    """Seconds of leading silence (< -50 dBFS) in the cropped segment, less build-sfx's 5 ms
    keep. Done here rather than with ffmpeg's silenceremove, which (ffmpeg 9) swallows short
    crops whole."""
    cmd = [ffmpeg_bin(), '-hide_banner', '-loglevel', 'error', '-nostdin']
    if start > 0:
        cmd += ['-ss', '%.4f' % start]
    if dur is not None:
        cmd += ['-t', '%.4f' % dur]
    raw = subprocess.run(cmd + ['-i', src, '-ac', '1', '-ar', str(RATE), '-f', 's16le', '-'],
                         capture_output=True).stdout
    n = len(raw) // 2
    if not n:
        return 0.0
    import array
    a = array.array('h'); a.frombytes(raw[:n * 2])
    if sys.byteorder == 'big':
        a.byteswap()
    thresh = int(32768 * 10 ** (-50 / 20))
    for i, x in enumerate(a):
        if x > thresh or x < -thresh:
            return max(0.0, i / RATE - 0.005)
    return 0.0


def clip_filters(clip, loop):
    c = dict(CLIP_DEFAULTS, **clip)
    f = []
    f.append('loudnorm=I=%g:TP=-1.5:LRA=11' % LUFS)
    if c['gain_db']:
        f.append('volume=%gdB' % c['gain_db'])
    if c['fade_in_ms'] > 0:
        f.append('afade=t=in:d=%g' % (c['fade_in_ms'] / 1000))
    if not loop:
        # Tail fade without knowing the post-trim length: reverse, fade in, reverse back.
        f.append('areverse,afade=t=in:d=%g,areverse' % (max(c['fade_out_ms'], 5.0) / 1000))
    return ','.join(f)


def render(src, clip, loop, dst, ogg=False):
    c = dict(CLIP_DEFAULTS, **clip)
    start = c['start']
    dur = (c['end'] - start) if c['end'] is not None and c['end'] > start else None
    if not loop and c['trim_silence']:
        skip = lead_silence(src, start, dur)
        start += skip
        if dur is not None:
            dur = max(0.01, dur - skip)
    cmd = [ffmpeg_bin(need_vorbis=ogg), '-hide_banner', '-loglevel', 'error', '-nostdin', '-y']
    if start > 0:
        cmd += ['-ss', '%.4f' % start]
    if dur is not None:
        cmd += ['-t', '%.4f' % dur]
    cmd += ['-i', src, '-af', clip_filters(c, loop), '-ac', '1', '-ar', str(RATE)]
    cmd += ['-c:a', 'libvorbis', '-q:a', str(QUALITY)] if ogg else ['-c:a', 'pcm_s16le']
    tmp = dst + '.part' + os.path.splitext(dst)[1]
    r = subprocess.run(cmd + [tmp], capture_output=True, text=True)
    if r.returncode != 0:
        if os.path.exists(tmp):
            os.remove(tmp)
        raise RuntimeError(r.stderr.strip().splitlines()[-1] if r.stderr.strip() else 'ffmpeg failed')
    os.replace(tmp, dst)


# ── mapping + events ────────────────────────────────────────────────────────────────────
def events():
    """SoundKind enum -> [{stem, name, comment}], in enum order. The enum is the event list."""
    src = open(KIND_CS, encoding='utf-8-sig').read()
    body = src[src.index('enum SoundKind'):]
    body = body[body.index('{') + 1:body.index('}')]
    out = []
    for line in body.splitlines():
        m = re.match(r'\s*([A-Z]\w*)\s*(?:=\s*\d+)?\s*,?\s*(?://\s*(.*))?$', line)
        if not m or m.group(1) in ('None', 'DevTone'):
            continue
        name = m.group(1)
        out.append({'name': name, 'stem': re.sub(r'(?<!^)([A-Z])', r'_\1', name).lower(),
                    'comment': (m.group(2) or '').strip()})
    return out


def load_map():
    if not os.path.exists(MAP_PATH):
        return {'library': DEFAULT_LIBRARY, 'events': {}}
    return json.load(open(MAP_PATH, encoding='utf-8'))


def save_map(m):
    order = {e['stem']: i for i, e in enumerate(events())}
    m['events'] = dict(sorted(m['events'].items(), key=lambda kv: (order.get(kv[0], 1e9), kv[0])))
    for ev in m['events'].values():
        ev['clips'] = [{k: v for k, v in c.items() if k == 'file' or CLIP_DEFAULTS.get(k, object()) != v}
                       for c in ev.get('clips', [])]
    tmp = MAP_PATH + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(m, f, indent=1, ensure_ascii=False)
        f.write('\n')
    os.replace(tmp, MAP_PATH)


def library_root(m=None):
    m = m or load_map()
    return os.path.abspath(os.path.expanduser(os.environ.get('MTILE_SFX_LIBRARY') or m.get('library')
                                              or DEFAULT_LIBRARY))


def lib_path(rel, m=None):
    root = library_root(m)
    p = os.path.abspath(os.path.join(root, rel))
    if not (p == root or p.startswith(root + os.sep)):
        raise ValueError('path escapes library: ' + rel)
    return p


def scan_library(m=None):
    root = library_root(m)
    folders = {}
    if os.path.isdir(root):
        for d, subs, files in os.walk(root, followlinks=True):
            subs[:] = sorted(s for s in subs if not s.startswith('.'))
            hits = sorted(f for f in files if f.lower().endswith(AUDIO_EXT) and not f.startswith('.'))
            if hits:
                rel = os.path.relpath(d, root).replace(os.sep, '/')
                folders[rel] = [{'path': (f if rel == '.' else rel + '/' + f), 'name': f} for f in hits]
    return {'root': root, 'folders': [{'folder': k, 'files': v} for k, v in sorted(folders.items())]}


# ── dev cache ───────────────────────────────────────────────────────────────────────────
def event_signature(ev, m):
    parts = [json.dumps(ev, sort_keys=True)]
    for c in ev.get('clips', []):
        try:
            st = os.stat(lib_path(c['file'], m))
            parts.append('%s:%d:%d' % (c['file'], st.st_size, int(st.st_mtime)))
        except (OSError, ValueError):
            parts.append(c['file'] + ':missing')
    return hashlib.sha1('|'.join(parts).encode()).hexdigest()


def cache_name(stem, sig, i):
    # Generation-stamped, so a render never rewrites a file the game may be loading from
    # the previous manifest. One previous generation is kept per event (see render_dev).
    return '%s__%s_%02d.wav' % (stem, sig[:8], i + 1)


def _cache_files(stem):
    pat = re.compile(re.escape(stem) + r'(__[0-9a-f]{8})?_\d\d\.wav')
    return [f for f in os.listdir(CACHE) if pat.fullmatch(f)]


def render_dev(m):
    """Re-render changed events into Audio/dev_cache, then rewrite manifest.json.
    Returns (rendered stems, errors, manifest events)."""
    os.makedirs(CACHE, exist_ok=True)
    try:
        state = json.load(open(STATE))
    except (OSError, ValueError):
        state = {}
    rendered, errors = [], []
    for stem, ev in m['events'].items():
        sig = event_signature(ev, m)
        st = state.get(stem) if isinstance(state.get(stem), dict) else {}
        clips = ev.get('clips', [])
        if st.get('sig') == sig and st.get('ok') and \
                all(os.path.exists(os.path.join(CACHE, cache_name(stem, sig, i))) for i in range(len(clips))):
            continue
        ok = True
        for i, c in enumerate(clips):
            try:
                render(lib_path(c['file'], m), c, ev.get('loop', False), os.path.join(CACHE, cache_name(stem, sig, i)))
            except Exception as ex:  # one bad clip must not take the event down
                ok = False
                errors.append('%s #%d (%s): %s' % (stem, i + 1, c['file'], ex))
        keep = {sig[:8], (st.get('sig') or '')[:8]}
        for f in _cache_files(stem):
            mm = re.search(r'__([0-9a-f]{8})_', f)
            if not mm or mm.group(1) not in keep:
                os.remove(os.path.join(CACHE, f))
        state[stem] = {'sig': sig, 'ok': ok}
        rendered.append(stem)
    for stem in [x for x in state if x not in m['events']]:
        del state[stem]
        for f in _cache_files(stem):
            os.remove(os.path.join(CACHE, f))
    json.dump(state, open(STATE, 'w'))
    return rendered, errors, write_manifest(m, state)


def write_manifest(m, state):
    evs = {}
    for stem, ev in m['events'].items():
        sig = (state.get(stem) or {}).get('sig') or ''
        evs[stem] = [cache_name(stem, sig, i) for i in range(len(ev.get('clips', [])))
                     if sig and os.path.exists(os.path.join(CACHE, cache_name(stem, sig, i)))]
    man = {'version': int(time.time() * 1000), 'events': evs}
    tmp = os.path.join(CACHE, 'manifest.json.tmp')
    with open(tmp, 'w') as f:
        json.dump(man, f, indent=1)
    os.replace(tmp, os.path.join(CACHE, 'manifest.json'))
    return evs


def current_manifest():
    try:
        return json.load(open(os.path.join(CACHE, 'manifest.json')))['events']
    except (OSError, ValueError, KeyError):
        return {}


def preview_wav(rel, m):
    """Any library file -> a browser-safe WAV (cached), so every format plays and decodes."""
    src = lib_path(rel, m)
    st = os.stat(src)
    key = hashlib.sha1(('%s|%d|%d' % (src, st.st_size, int(st.st_mtime))).encode()).hexdigest()[:16]
    os.makedirs(PREVIEW, exist_ok=True)
    dst = os.path.join(PREVIEW, key + '.wav')
    if not os.path.exists(dst):
        tmp = dst + '.part.wav'
        subprocess.run([ffmpeg_bin(), '-hide_banner', '-loglevel', 'error', '-nostdin', '-y', '-i', src,
                        '-ac', '1', '-ar', '44100', '-c:a', 'pcm_s16le', tmp], check=True)
        os.replace(tmp, dst)
    return dst


# ── server ──────────────────────────────────────────────────────────────────────────────
class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def send(self, code, body, ctype='application/json'):
        if isinstance(body, (dict, list)):
            body = json.dumps(body).encode()
        elif isinstance(body, str):
            body = body.encode()
        self.send_response(code)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(body)

    def send_file(self, path, ctype):
        with open(path, 'rb') as f:
            self.send(200, f.read(), ctype)

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = dict(urllib.parse.parse_qsl(u.query))
        try:
            if u.path == '/favicon.ico':
                self.send_response(204)
                self.end_headers()
                return
            if u.path in ('/', '/index.html'):
                return self.send_file(os.path.join(HERE, 'index.html'), 'text/html; charset=utf-8')
            if u.path == '/api/state':
                m = load_map()
                return self.send(200, {'events': events(), 'map': m, 'library': scan_library(m),
                                       'defaults': CLIP_DEFAULTS, 'rendered': current_manifest()})
            if u.path == '/api/audio':
                return self.send_file(preview_wav(q['path'], load_map()), 'audio/wav')
            if u.path == '/api/rendered':
                name = os.path.basename(q['file'])
                return self.send_file(os.path.join(CACHE, name), 'audio/wav')
            self.send(404, {'error': 'not found'})
        except Exception as ex:
            self.send(500, {'error': str(ex)})

    def do_POST(self):
        try:
            body = json.loads(self.rfile.read(int(self.headers.get('Content-Length', 0))) or b'{}')
            if self.path == '/api/map':
                with _lock:
                    m = body['map']
                    save_map(m)
                    rendered, errors, files = render_dev(load_map())
                return self.send(200, {'ok': not errors, 'rendered': rendered, 'errors': errors,
                                       'files': files})
            self.send(404, {'error': 'not found'})
        except Exception as ex:
            self.send(500, {'error': str(ex)})


def cmd_serve(a):
    m = load_map()
    if not m['events']:
        print('sound_map.json is empty — run `soundboard.py seed` first.')
    sys.stdout.reconfigure(line_buffering=True)
    print('library: %s' % library_root(m))
    print('rendering dev cache...', end=' ', flush=True)
    rendered, errors, _ = render_dev(m)
    print('%d event(s) rendered%s' % (len(rendered), ''.join('\n  ! ' + e for e in errors)))
    srv = ThreadingHTTPServer(('127.0.0.1', a.port), Handler)
    print('soundboard: http://localhost:%d   (Ctrl-C to stop)' % a.port)
    print('run the game from the repo root (dotnet run --project MTile.Desktop) and it picks up every change.')
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


# ── seed ────────────────────────────────────────────────────────────────────────────────
def cmd_seed(a):
    m = load_map()
    if a.library:
        m['library'] = a.library
    root = library_root(m)
    os.makedirs(root, exist_ok=True)
    stems = {e['stem'] for e in events()}

    # shipped/<stem>/  — every clip the game ships today, so each event starts mapped.
    shipped = {}
    for f in sorted(os.listdir(SOUNDS)):
        mm = re.fullmatch(r'(.+?)(?:_(\d\d))?\.ogg', f)
        if not mm or mm.group(1) not in stems:
            continue
        stem = mm.group(1)
        d = os.path.join(root, 'shipped', stem)
        os.makedirs(d, exist_ok=True)
        if not os.path.exists(os.path.join(d, f)):
            shutil.copy2(os.path.join(SOUNDS, f), os.path.join(d, f))
        shipped.setdefault(stem, []).append('shipped/%s/%s' % (stem, f))
    print('shipped/: %d event folder(s)' % len(shipped))

    # picks/<row>/  — clips picked in the 2026-10 OGA picker (sfx-picks.json export).
    picks = os.path.expanduser(a.picks)
    cands = os.path.expanduser(a.candidates)
    if os.path.exists(picks):
        n = 0
        for c in json.load(open(picks)).get('clips', []):
            if not c.get('pick'):
                continue
            src = os.path.join(cands, c['file'])
            if not os.path.exists(src):
                continue
            d = os.path.join(root, 'picks', c['row'])
            os.makedirs(d, exist_ok=True)
            dst = os.path.join(d, os.path.basename(src))
            if not os.path.exists(dst):
                shutil.copy2(src, dst)
            n += 1
        print('picks/: %d picked clip(s) from %s' % (n, picks))

    if not m['events']:
        loops = {'wall_scrape', 'mass_flight'}
        m['events'] = {}
        for stem, files in shipped.items():
            ev = {'loop': True} if stem in loops else {}
            ev['clips'] = [{'file': f} for f in files]
            m['events'][stem] = ev
        save_map(m)
        print('wrote %s (%d events)' % (os.path.relpath(MAP_PATH, REPO), len(m['events'])))
    else:
        print('sound_map.json already has events; left it alone')


# ── ship ────────────────────────────────────────────────────────────────────────────────
def is_pristine_shipped(c):
    return c['file'].startswith('shipped/') and c['file'].endswith('.ogg') and \
        all(c.get(k, v) == v for k, v in CLIP_DEFAULTS.items())


def cmd_ship(a):
    m = load_map()
    sources = []
    for stem, ev in m['events'].items():
        old = [f for f in os.listdir(SOUNDS) if re.fullmatch(re.escape(stem) + r'(_\d\d)?\.ogg', f)]
        clips = ev.get('clips', [])
        print('%-22s %d clip(s)%s' % (stem, len(clips), '  (was %d)' % len(old) if len(old) != len(clips) else ''))
        if a.dry_run:
            continue
        staged = []
        for i, c in enumerate(clips):
            dst = os.path.join(SOUNDS, '%s_%02d.ogg.new' % (stem, i + 1))
            src = lib_path(c['file'], m)
            if is_pristine_shipped(c):
                shutil.copy2(src, dst)
            else:
                render(src, c, ev.get('loop', False), dst + '.ogg', ogg=True)
                os.replace(dst + '.ogg', dst)
                sources.append('%s_%02d.ogg <- %s' % (stem, i + 1, c['file']))
            staged.append(dst)
        for f in old:
            os.remove(os.path.join(SOUNDS, f))
        for s in staged:
            os.replace(s, s[:-len('.new')])
    if a.dry_run:
        print('dry run — nothing written')
        return
    sync = os.path.join(REPO, 'scripts', 'sync-sounds.ps1' if os.name == 'nt' else 'sync-sounds.sh')
    subprocess.run((['pwsh', sync] if os.name == 'nt' else [sync]), cwd=REPO, check=True)
    if sources:
        print('\nRe-rendered from library sources — check Audio/CREDITS.md covers each:')
        for s in sources:
            print('  ' + s)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    s = sub.add_parser('serve'); s.add_argument('--port', type=int, default=8765)
    s = sub.add_parser('seed')
    s.add_argument('--library', default=None, help='library root (default %s)' % DEFAULT_LIBRARY)
    s.add_argument('--picks', default='~/dev/sfx-picks.json')
    s.add_argument('--candidates', default='~/dev/MTile-sfx-candidates')
    s = sub.add_parser('ship'); s.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()
    {'serve': cmd_serve, 'seed': cmd_seed, 'ship': cmd_ship}[a.cmd](a)


if __name__ == '__main__':
    main()
