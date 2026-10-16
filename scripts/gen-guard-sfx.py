#!/usr/bin/env python3
"""Synthesize the guard SFX sources — no sample library involved.

    python3 scripts/gen-guard-sfx.py                       # -> Audio/raw/guard_block/, Audio/raw/guard_parry/
    ./scripts/build-sfx.sh --in Audio/raw/guard_block --name guard_block
    ./scripts/build-sfx.sh --in Audio/raw/guard_parry --name guard_parry
    ./scripts/sync-sounds.sh

Two sounds, three round-robin variants each (Audio/raw is gitignored; this script is the
source of truth, the .ogg in Assets/Sounds is the artifact):

  guard_block  a LATE block (CombatState.ResolveGuard's falloff branch): the stance met
               the hit but the window had closed, so part of it leaked and the guard
               broke. Dull and heavy — a low thump with a pitch drop, a damped wooden
               knock, a lowpassed impact burst, and a short bright crack for the break.
  guard_parry  a PERFECT block: bright struck-metal ting. Noise transient, inharmonic
               bell partials with detuned twins (the slow beating is the shimmer), a
               short low body, and a rising "shing" chirp. Rings ~0.6 s so the reward
               is audible after the hit itself is over.

Everything is deterministic (seeded per variant) so a re-run reproduces the files.
Pure stdlib on purpose: this machine has no numpy, and the clips are tiny.
Output is 44.1 kHz 16-bit mono WAV; build-sfx.sh does the loudness match + Ogg.
"""
import math
import os
import random
import struct
import sys
import wave

SR = 44100


def buf(seconds):
    return [0.0] * int(SR * seconds)


def add_sine(out, freq, amp, tau, f_end=None, glide=0.05, delay=0.0, start_phase=0.0):
    """Exponentially decaying sine. With f_end, the pitch glides freq->f_end over `glide` s."""
    phase = start_phase
    n0 = int(delay * SR)
    for i in range(n0, len(out)):
        t = (i - n0) / SR
        f = freq
        if f_end is not None:
            k = min(t / glide, 1.0)
            f = freq * (f_end / freq) ** k
        phase += 2 * math.pi * f / SR
        out[i] += amp * math.exp(-t / tau) * math.sin(phase)


def add_chirp(out, f0, f1, dur, amp, attack, tau, delay=0.0):
    """Sine sweep f0->f1 over dur seconds with a quick attack and exponential decay."""
    phase = 0.0
    n0 = int(delay * SR)
    for i in range(n0, len(out)):
        t = (i - n0) / SR
        k = min(t / dur, 1.0)
        f = f0 * (f1 / f0) ** k
        phase += 2 * math.pi * f / SR
        env = min(t / attack, 1.0) * math.exp(-max(t - attack, 0.0) / tau)
        out[i] += amp * env * math.sin(phase)


def noise(n, rng):
    return [rng.uniform(-1.0, 1.0) for _ in range(n)]


def lowpass(x, cutoff):
    a = 1.0 - math.exp(-2 * math.pi * cutoff / SR)
    y, acc = [0.0] * len(x), 0.0
    for i, v in enumerate(x):
        acc += a * (v - acc)
        y[i] = acc
    return y


def highpass(x, cutoff):
    lp = lowpass(x, cutoff)
    return [v - l for v, l in zip(x, lp)]


def add_env(out, x, amp, tau, attack=0.0005, delay=0.0):
    n0 = int(delay * SR)
    for i in range(n0, min(len(out), n0 + len(x))):
        t = (i - n0) / SR
        env = min(t / attack, 1.0) * math.exp(-t / tau)
        out[i] += amp * env * x[i - n0]


def finish(out, fade=0.02, peak=0.9):
    n = len(out)
    nf = int(fade * SR)
    for i in range(n - nf, n):
        out[i] *= (n - i) / nf
    m = max(abs(v) for v in out) or 1.0
    s = peak / m
    return [v * s for v in out]


def write_wav(path, samples):
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1.0, min(1.0, v)) * 32767)) for v in samples))


# ── guard_block: dull thump + knock + crack ─────────────────────────────────────
def guard_block(variant):
    rng = random.Random(100 + variant)
    s = rng.uniform(0.92, 1.08)
    out = buf(0.30)

    # Low body thump with a fast pitch drop — the weight of the hit landing on the stance.
    add_sine(out, 175 * s, 0.95, tau=0.07, f_end=80 * s, glide=0.05)
    # Damped wooden/leather knock: a few inharmonic partials, all heavily damped.
    for f, a, tau in ((430, 0.50, 0.045), (690, 0.38, 0.035), (1040, 0.22, 0.028), (1600, 0.10, 0.020)):
        add_sine(out, f * s * rng.uniform(0.985, 1.015), a, tau)
    # Lowpassed impact burst — the "thud" texture.
    add_env(out, lowpass(noise(int(0.12 * SR), rng), 1600), 0.65, tau=0.03)
    # The break: a short bright crack riding on top. Slightly late so the thump leads.
    add_env(out, highpass(noise(int(0.04 * SR), rng), 3000), 0.55, tau=0.006, delay=0.004)
    return finish(out, fade=0.02)


# ── guard_parry: bright bell ting with a shing ─────────────────────────────────
def guard_parry(variant):
    rng = random.Random(200 + variant)
    base = 1500 * rng.uniform(0.95, 1.06)
    out = buf(0.70)

    # Attack transient: the clash itself.
    add_env(out, highpass(noise(int(0.05 * SR), rng), 2500), 0.80, tau=0.008)
    add_env(out, lowpass(noise(int(0.05 * SR), rng), 2500), 0.30, tau=0.014)
    # Inharmonic bell partials. Each gets a slightly detuned twin so the pair beats at a
    # few Hz — that slow shimmer is what makes a ring feel alive rather than like a tone.
    ratios = (1.00, 1.52, 2.07, 2.73, 3.41, 4.40)
    amps   = (1.00, 0.65, 0.50, 0.33, 0.22, 0.12)
    taus   = (0.42, 0.33, 0.26, 0.19, 0.14, 0.10)
    for r, a, tau in zip(ratios, amps, taus):
        f = base * r
        add_sine(out, f, a, tau, start_phase=rng.uniform(0, 2 * math.pi))
        add_sine(out, f * (1 + rng.uniform(0.002, 0.005)), a * 0.6, tau * 0.9,
                 start_phase=rng.uniform(0, 2 * math.pi))
    # A little low body so it isn't all treble.
    add_sine(out, 320 * rng.uniform(0.95, 1.05), 0.35, tau=0.07)
    # The "shing": a fast upward sweep right behind the transient.
    add_chirp(out, 2400, 7000, dur=0.035, amp=0.28, attack=0.008, tau=0.025, delay=0.002)
    return finish(out, fade=0.04)


def main(argv):
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    raw = os.path.join(root, "Audio", "raw")
    for name, fn in (("guard_block", guard_block), ("guard_parry", guard_parry)):
        d = os.path.join(raw, name)
        os.makedirs(d, exist_ok=True)
        for v in range(1, 4):
            p = os.path.join(d, f"{name}_{v:02d}.wav")
            write_wav(p, fn(v))
            print("wrote", os.path.relpath(p, root))


if __name__ == "__main__":
    main(sys.argv[1:])
