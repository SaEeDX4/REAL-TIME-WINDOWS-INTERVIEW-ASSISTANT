#!/usr/bin/env python3
"""
Deterministic generator for the recorded-style audio fixtures in tests/fixtures/audio/.

Every file is 24 kHz mono 16-bit PCM WAV (the exact format the engine analyses). Nothing is downloaded and no
third-party audio is used: non-speech sounds and music are synthesized here from first principles, and speech is
produced at run time by the eSpeak NG formant synthesizer (its *output* only; no eSpeak code or data is copied into
the repository, and the MBROLA voices, which carry their own licence, are never used).

Requirements: Python 3.10+, numpy, espeak-ng (speech), ffmpeg with libopus (resampling and the Opus call-codec
round trip). Run from the repository root:

    python3 scripts/generate-audio-fixtures.py            # writes tests/fixtures/audio/*.wav + fixtures.json
    python3 scripts/generate-audio-fixtures.py --check    # regenerate into a temp dir and compare checksums

Each base sound is rendered dry (with its own room reverb where it belongs) and then passed through 2-3 variants of
the path it takes to the loopback capture:
  direct  - a local system sound captured by WASAPI loopback: no codec, no background noise, -12 dBFS peak.
  callq   - remote call path, quiet: pink background noise at -45 dBFS RMS (at a -6 dBFS reference peak), meeting
            codec band limit (high-pass 100 Hz, low-pass 7 kHz), Opus 16 kHz VOIP round trip, light AGC/compression,
            then the playback volume puts the peak at -18 .. -30 dBFS.
  calln   - remote call path, noisy and loud: fan noise at -35 dBFS RMS, same codec chain, heavier compression, peak at
            -6 .. -10 dBFS; every second base is also driven into hard clipping (~1 % of samples).
Noise is added before the volume stage on purpose: on a real call the remote room noise is scaled together with the
remote signal by the local playback volume.

Determinism: every random source is numpy's PCG64 seeded from a CRC32 of the fixture name, so repeated runs on the
same tool versions are bit-identical. eSpeak NG and libopus versions can change their output slightly; the versions
used are recorded in MANIFEST.md / fixtures.json.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
import wave
import zlib

import numpy as np

SR = 24000
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "tests", "fixtures", "audio")


# ----------------------------------------------------------------------------------------------------------- helpers

def rng_for(name: str) -> np.random.Generator:
    return np.random.default_rng(zlib.crc32(name.encode("utf-8")))


def t_axis(seconds: float) -> np.ndarray:
    return np.arange(int(round(seconds * SR))) / SR


def silence(seconds: float) -> np.ndarray:
    return np.zeros(int(round(seconds * SR)))


def place(dst: np.ndarray, src: np.ndarray, at_s: float) -> None:
    i = int(round(at_s * SR))
    n = min(len(src), len(dst) - i)
    if n > 0:
        dst[i:i + n] += src[:n]


def normalize_peak(x: np.ndarray, peak: float = 1.0) -> np.ndarray:
    m = float(np.max(np.abs(x))) if len(x) else 0.0
    return x if m <= 0 else x * (peak / m)


def db(x: float) -> float:
    return 10 ** (x / 20)


def biquad(x: np.ndarray, b, a) -> np.ndarray:
    """Direct form I biquad (pure Python loop; inputs are short)."""
    b0, b1, b2 = (v / a[0] for v in b)
    a1, a2 = a[1] / a[0], a[2] / a[0]
    y = np.empty_like(x)
    x1 = x2 = y1 = y2 = 0.0
    for i, xi in enumerate(x.tolist()):
        yi = b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1, y2, y1 = x1, xi, y1, yi
        y[i] = yi
    return y


def rbj(kind: str, f0: float, q: float = 0.7071):
    """RBJ audio-EQ-cookbook coefficients."""
    w = 2 * math.pi * f0 / SR
    al = math.sin(w) / (2 * q)
    c = math.cos(w)
    if kind == "lp":
        b = ((1 - c) / 2, 1 - c, (1 - c) / 2)
    elif kind == "hp":
        b = ((1 + c) / 2, -(1 + c), (1 + c) / 2)
    elif kind == "bp":
        b = (al, 0.0, -al)
    else:
        raise ValueError(kind)
    return b, (1 + al, -2 * c, 1 - al)


def filt(x: np.ndarray, kind: str, f0: float, q: float = 0.7071, order: int = 2) -> np.ndarray:
    b, a = rbj(kind, f0, q)
    for _ in range(order // 2):
        x = biquad(x, b, a)
    return x


def fft_convolve(x: np.ndarray, h: np.ndarray) -> np.ndarray:
    n = len(x) + len(h) - 1
    size = 1 << (n - 1).bit_length()
    return np.fft.irfft(np.fft.rfft(x, size) * np.fft.rfft(h, size), size)[:n]


def room_reverb(x: np.ndarray, rng: np.random.Generator, rt60: float, wet: float, tail: bool = True) -> np.ndarray:
    """Synthetic room: convolution with exponentially decaying (and slowly darkening) noise, plus the dry signal."""
    n = int(rt60 * SR)
    t = np.arange(n) / SR
    h = rng.standard_normal(n) * np.exp(-6.91 * t / rt60)
    # Air/wall absorption: the later part of the tail is darker (two-band split, high band decays twice as fast).
    hi = h - filt(h, "lp", 3000)
    h = (h - hi) + hi * np.exp(-6.91 * t / (rt60 / 2))
    h[: int(0.004 * SR)] = 0  # ~4 ms pre-delay before the first reflections
    h /= math.sqrt(float(np.sum(h * h)))
    y = fft_convolve(x, h)
    dry = np.concatenate([x, np.zeros(len(y) - len(x))])
    out = dry + wet * y * 3.0
    return out if tail else out[: len(x)]


def pink_noise(n: int, rng: np.random.Generator) -> np.ndarray:
    spec = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / SR)
    f[0] = f[1]
    spec /= np.sqrt(f)
    y = np.fft.irfft(spec, n)
    return y / math.sqrt(float(np.mean(y * y)))


def fan_noise(n: int, rng: np.random.Generator) -> np.ndarray:
    """Laptop/desk fan: steeper-than-pink broadband noise with a broad bump around 300-600 Hz and slow 'whoosh'."""
    spec = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / SR)
    f[0] = f[1]
    shape = 1 / f ** 0.75 + 0.02 * np.exp(-((f - 450) / 250) ** 2)
    y = np.fft.irfft(spec * shape, n)
    y *= 1 + 0.15 * np.sin(2 * math.pi * 0.7 * np.arange(n) / SR + rng.uniform(0, 6.28))
    return y / math.sqrt(float(np.mean(y * y)))


def compress(x: np.ndarray, threshold_db: float, ratio: float, attack_ms: float = 5, release_ms: float = 120) -> np.ndarray:
    """Feed-forward peak compressor / AGC with make-up gain back to the input peak."""
    ga = math.exp(-1 / (attack_ms * SR / 1000))
    gr = math.exp(-1 / (release_ms * SR / 1000))
    thr = db(threshold_db)
    env = 0.0
    g = np.empty_like(x)
    for i, v in enumerate(np.abs(x).tolist()):
        env = ga * env + (1 - ga) * v if v > env else gr * env + (1 - gr) * v
        g[i] = 1.0 if env <= thr else (env / thr) ** (1 / ratio - 1)
    y = x * g
    return normalize_peak(y, float(np.max(np.abs(x))))


def run(cmd: list[str]) -> None:
    subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def write_wav(path: str, x: np.ndarray) -> None:
    pcm = np.clip(np.round(x * 32767), -32768, 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


def read_wav(path: str) -> np.ndarray:
    with wave.open(path, "rb") as w:
        assert w.getnchannels() == 1 and w.getsampwidth() == 2 and w.getframerate() == SR, path
        return np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768


def opus_roundtrip(x: np.ndarray, tmp: str, bitrate: str = "20k") -> np.ndarray:
    """Meeting codec: Opus at 16 kHz in VOIP mode (what WebRTC-style clients send), decoded back to 24 kHz."""
    src, enc, dst = (os.path.join(tmp, n) for n in ("codec_in.wav", "codec.opus", "codec_out.wav"))
    write_wav(src, x * 0.98)
    run(["ffmpeg", "-y", "-v", "error", "-i", src, "-ar", "16000", "-c:a", "libopus", "-b:a", bitrate,
         "-application", "voip", "-frame_duration", "20", "-bitexact", enc])
    run(["ffmpeg", "-y", "-v", "error", "-i", enc, "-ar", str(SR), "-ac", "1", "-c:a", "pcm_s16le", "-bitexact", dst])
    y = read_wav(dst) / 0.98
    # Opus pre-skip/resampler delay: align to the input by cross-correlation of the envelopes, then trim to length.
    tpl = np.abs(x[: min(len(x), SR // 2)])
    lag = int(np.argmax(np.correlate(np.abs(y[: len(tpl) + 2000]), tpl, "valid")))
    y = y[lag: lag + len(x)]
    return np.concatenate([y, np.zeros(len(x) - len(y))])


# ------------------------------------------------------------------------------------------------- non-speech bases

GLOCK = (1.0, 2.76, 5.40, 8.93)          # free-free bar (glockenspiel / notification "ding") partial ratios
MARIMBA = (1.0, 3.93, 9.24)             # tuned marimba bar


def struck(f0: float, seconds: float, partials, decay_s: float, rng: np.random.Generator, bright: float = 0.6) -> np.ndarray:
    """A struck bar/bell note: inharmonic partials, ~1 ms attack, exponential decay (higher partials decay faster)."""
    t = t_axis(seconds)
    y = np.zeros_like(t)
    for k, r in enumerate(partials):
        f = f0 * r
        if f >= SR / 2 - 500:
            continue
        amp = bright ** k
        tau = decay_s / (1 + 1.6 * k)
        detune = 1 + rng.uniform(-0.002, 0.002)
        y += amp * np.sin(2 * math.pi * f * detune * t + rng.uniform(0, 6.28)) * np.exp(-t / tau)
    attack = np.minimum(1, t / 0.0012)
    return y * attack


def chime(name: str, notes, gap_s: float, decay_s: float, partials, rt60: float, total_s: float) -> np.ndarray:
    rng = rng_for(name)
    y = silence(total_s)
    for i, f in enumerate(notes):
        place(y, struck(f, total_s - i * gap_s, partials, decay_s, rng) * (1 - 0.12 * i), 0.05 + i * gap_s)
    return normalize_peak(room_reverb(y, rng, rt60, 0.35, tail=False))


def error_tone(name: str, freqs, note_s: float, total_s: float, harmonics=(1.0, 0.55, 0.3, 0.18, 0.1)) -> np.ndarray:
    """'Critical stop' style: descending two-tone sequence, harmonic timbre, 5.5 Hz vibrato, soft release."""
    rng = rng_for(name)
    y = silence(total_s)
    for i, f in enumerate(freqs):
        t = t_axis(note_s + 0.25)
        vib = 1 + 0.006 * np.sin(2 * math.pi * 5.5 * t + rng.uniform(0, 6.28))
        phase = 2 * math.pi * np.cumsum(f * vib) / SR
        tone = sum(a * np.sin((h + 1) * phase) for h, a in enumerate(harmonics))
        env = np.minimum(1, t / 0.008) * np.where(t < note_s, 1.0, np.exp(-(t - note_s) / 0.06)) * np.exp(-t / 0.9)
        place(y, tone * env, 0.05 + i * note_s)
    return normalize_peak(room_reverb(y, rng, 0.35, 0.25, tail=False))


def click(rng: np.random.Generator, dur_s: float, center_hz: float, q: float, decay_s: float) -> np.ndarray:
    """A mechanical click: band-passed noise transient with a fast exponential decay."""
    t = t_axis(dur_s)
    burst = rng.standard_normal(len(t)) * np.exp(-t / decay_s)
    return filt(burst, "bp", center_hz, q)


def shutter(name: str) -> np.ndarray:
    """Camera / screenshot shutter: first-curtain click, spring rattle, second-curtain click, small-room reverb."""
    rng = rng_for(name)
    y = silence(0.65)
    place(y, normalize_peak(click(rng, 0.02, 2800, 1.2, 0.004) + 0.6 * click(rng, 0.02, 900, 2.0, 0.006)), 0.05)
    rattle_t = t_axis(0.09)
    rattle = rng.standard_normal(len(rattle_t)) * (0.5 + 0.5 * np.sign(np.sin(2 * math.pi * 140 * rattle_t))) \
        * np.exp(-rattle_t / 0.03)
    place(y, 0.25 * filt(rattle, "bp", 4200, 1.5), 0.065)
    place(y, 0.85 * normalize_peak(click(rng, 0.015, 3300, 1.4, 0.003) + 0.5 * click(rng, 0.015, 1200, 2.0, 0.005)), 0.17)
    return normalize_peak(room_reverb(y, rng, 0.3, 0.2, tail=False))


def keyboard(name: str) -> np.ndarray:
    """Typing burst on a laptop keyboard (9 keystrokes, irregular timing, key-down + key-up clicks)."""
    rng = rng_for(name)
    y = silence(1.15)
    at = 0.05
    for _ in range(9):
        down = click(rng, 0.03, rng.uniform(1800, 3500), 1.0, rng.uniform(0.003, 0.006))
        thock = click(rng, 0.03, rng.uniform(250, 450), 2.5, 0.008)
        key = normalize_peak(down + 0.7 * thock) * rng.uniform(0.5, 1.0)
        place(y, key, at)
        place(y, 0.35 * normalize_peak(click(rng, 0.02, rng.uniform(2500, 4500), 1.0, 0.003)), at + rng.uniform(0.06, 0.1))
        at += rng.uniform(0.07, 0.16)
    return normalize_peak(room_reverb(y, rng, 0.3, 0.15, tail=False))


def mouse(name: str) -> np.ndarray:
    """Mouse: two single clicks and a double click (press + release clicks)."""
    rng = rng_for(name)
    y = silence(1.0)
    for at in (0.05, 0.45, 0.62):
        place(y, normalize_peak(click(rng, 0.02, 3800, 1.5, 0.0025) + 0.4 * click(rng, 0.02, 1500, 2.0, 0.003)), at)
        place(y, 0.5 * normalize_peak(click(rng, 0.015, 4500, 1.5, 0.002)), at + 0.07)
    return normalize_peak(room_reverb(y, rng, 0.3, 0.15, tail=False))


def knock(name: str) -> np.ndarray:
    """Door knock: three knuckle impacts on a wooden door - low thump (door panel mode ~90-140 Hz) + mid 'tock'."""
    rng = rng_for(name)
    y = silence(1.0)
    for i, at in enumerate((0.05, 0.27, 0.46)):
        t = t_axis(0.25)
        f = rng.uniform(95, 130)
        thump = np.sin(2 * math.pi * f * t * (1 - 0.15 * t)) * np.exp(-t / 0.035)
        thump += 0.5 * np.sin(2 * math.pi * 2.3 * f * t) * np.exp(-t / 0.018)
        tock = click(rng, 0.25, rng.uniform(600, 900), 3.0, 0.012)
        place(y, (normalize_peak(thump) + 0.5 * normalize_peak(tock)) * (1 - 0.1 * i), at)
    return normalize_peak(room_reverb(y, rng, 0.55, 0.3, tail=False))


# ----------------------------------------------------------------------------------------------------- music bases

def note_hz(midi: float) -> float:
    return 440.0 * 2 ** ((midi - 69) / 12)


def harmonic_tone(f: float, seconds: float, harmonics: int, rolloff: float, decay_s: float, rng: np.random.Generator,
                  attack_s: float = 0.005, inharm: float = 0.0) -> np.ndarray:
    t = t_axis(seconds)
    y = np.zeros_like(t)
    for h in range(1, harmonics + 1):
        fh = f * h * math.sqrt(1 + inharm * h * h)
        if fh >= SR / 2 - 500:
            break
        y += np.sin(2 * math.pi * fh * t + rng.uniform(0, 6.28)) / h ** rolloff * np.exp(-t * h / (decay_s * 4))
    return y * np.minimum(1, t / attack_s) * np.exp(-t / decay_s)


def kick(rng: np.random.Generator) -> np.ndarray:
    t = t_axis(0.3)
    f = 45 + 75 * np.exp(-t / 0.03)                       # pitched-down sine
    return np.sin(2 * math.pi * np.cumsum(f) / SR) * np.exp(-t / 0.12) + 0.3 * click(rng, 0.3, 3000, 1.0, 0.002)


def snare(rng: np.random.Generator) -> np.ndarray:
    t = t_axis(0.25)
    body = 0.5 * np.sin(2 * math.pi * 185 * t) * np.exp(-t / 0.04)
    noise = filt(rng.standard_normal(len(t)), "bp", 2500, 0.7) * np.exp(-t / 0.08)
    return body + 1.6 * noise


def hihat(rng: np.random.Generator) -> np.ndarray:
    t = t_axis(0.06)
    return filt(rng.standard_normal(len(t)), "hp", 7000, 0.7) * np.exp(-t / 0.012)


def drums(y: np.ndarray, rng: np.random.Generator, beat_s: float, start: float = 0.0, brushes: bool = False) -> None:
    beats = int((len(y) / SR - start) / beat_s)
    for b in range(beats):
        at = start + b * beat_s
        if not brushes:
            if b % 2 == 0:
                place(y, 0.9 * kick(rng), at)
            else:
                place(y, 0.45 * snare(rng), at)
        else:
            place(y, 0.25 * snare(rng) * 0.6, at) if b % 2 else place(y, 0.6 * kick(rng), at)
        place(y, 0.12 * hihat(rng), at)
        place(y, 0.08 * hihat(rng), at + beat_s / 2)


def music_band(name: str) -> np.ndarray:
    """Pop fragment, ~143 bpm: I-V-vi-IV pad chords (C G Am F), plucked bass line, kick/snare/hi-hat, room."""
    rng = rng_for(name)
    beat = 0.42
    y = silence(1.7)
    chords = [(60, 64, 67), (59, 62, 67), (57, 60, 64), (57, 60, 65)]
    bass = [36, 43, 45, 41]
    for i, (ch, bn) in enumerate(zip(chords, bass)):
        at = 0.05 + i * beat
        for m in ch:
            place(y, 0.22 * harmonic_tone(note_hz(m), beat + 0.2, 8, 1.2, 0.8, rng, attack_s=0.02), at)
        place(y, 0.55 * harmonic_tone(note_hz(bn), beat, 10, 1.0, 0.35, rng, attack_s=0.004), at)
        place(y, 0.35 * harmonic_tone(note_hz(bn + 12), beat / 2, 10, 1.0, 0.25, rng), at + beat / 2)
    drums(y, rng, beat, 0.05)
    return normalize_peak(room_reverb(y, rng, 0.5, 0.2, tail=False))


def music_piano(name: str) -> np.ndarray:
    """Piano-style fragment, 112 bpm: arpeggiated Dm7-G7-Cmaj7 with slightly inharmonic strings, walking bass, brushes."""
    rng = rng_for(name)
    beat = 60 / 112
    y = silence(1.7)
    arps = [(50, 57, 60, 65), (55, 59, 62, 65), (48, 55, 59, 64)]
    walk = [38, 41, 43, 47, 48]
    step = beat / 2
    k = 0
    for ch in arps:
        for m in ch:
            place(y, 0.3 * harmonic_tone(note_hz(m + 12), 1.2, 12, 1.4, 0.7, rng, attack_s=0.002, inharm=0.0004), 0.05 + k * step)
            k += 1
    for i, m in enumerate(walk):
        place(y, 0.5 * harmonic_tone(note_hz(m - 12), beat, 10, 1.1, 0.4, rng, attack_s=0.006), 0.05 + i * beat)
    drums(y, rng, beat, 0.05, brushes=True)
    return normalize_peak(room_reverb(y, rng, 0.6, 0.25, tail=False))


def sung_vowels(name: str) -> np.ndarray:
    """The known hard case: a synthetic singer (glottal pulses through vowel formants, vibrato) over pad + kick."""
    rng = rng_for(name)
    beat = 0.34
    melody = [(57, "a"), (60, "a"), (64, "o"), (62, "e"), (60, "a")]   # A3 C4 E4 D4 C4
    formants = {"a": (730, 1090, 2440), "o": (570, 840, 2410), "e": (530, 1840, 2480)}
    total = 0.1 + beat * len(melody) + 0.2
    voice = silence(total)
    for i, (m, v) in enumerate(melody):
        t = t_axis(beat)
        f0 = note_hz(m) * (1 + 0.03 * np.sin(2 * math.pi * 5.5 * t) * np.minimum(1, t / 0.15))
        phase = np.cumsum(f0) / SR
        # Rosenberg-like glottal pulse: smooth opening, sharp closure (rich harmonic ladder).
        p = phase % 1
        src = np.where(p < 0.6, 0.5 - 0.5 * np.cos(math.pi * p / 0.6), np.cos(math.pi * (p - 0.6) / 0.8))
        src = np.diff(src, prepend=0) * 50 + rng.standard_normal(len(t)) * 0.01   # breath
        out = np.zeros_like(t)
        for j, fc in enumerate(formants[v]):
            out += filt(src, "bp", fc, fc / 90, order=2) * (1.0, 0.5, 0.25)[j]
        env = np.minimum(1, t / 0.04) * np.minimum(1, (beat - t) / 0.05)
        place(voice, out * env, 0.1 + i * beat)
    y = normalize_peak(voice) * 0.9
    pad = silence(total)
    for m in (45, 52, 57, 60, 64):
        place(pad, 0.06 * harmonic_tone(note_hz(m), total, 6, 1.5, 3.0, rng, attack_s=0.2), 0.0)
    y += pad
    for b in range(int(total / (beat * 2))):
        place(y, 0.35 * kick(rng), 0.1 + b * beat * 2)
    return normalize_peak(room_reverb(y, rng, 0.5, 0.25, tail=False))


# ------------------------------------------------------------------------------------------------------ speech bases

SPEECH = [
    # name, language/voice (eSpeak NG built-in voices only), words/min, pitch 0-99, text
    ("speech_why", "en-us+m3", 165, 45, "Why?"),
    ("speech_howso", "en-gb+f3", 170, 60, "How so?"),
    ("speech_metrics", "en-us+f4", 175, 55, "And the metrics?"),
    ("speech_tellme", "en-gb+m1", 160, 40, "Tell me about yourself."),
    ("speech_ninetydays", "en-us+m7", 180, 50, "What would you do in your first ninety days?"),
    ("speech_de_warum", "de+m2", 160, 45, "Warum haben Sie sich bei uns beworben?"),
    ("speech_es_reto", "es+f2", 165, 55, "¿Cuál fue su mayor reto?"),
    # RC4 negative controls: short real follow-ups must stay answerable (falling and rising, one to three words), plus a
    # deliberately fast speaker for the speech-rate plausibility check.
    ("speech_why_falling", "en-gb+m3", 160, 35, "Why."),
    ("speech_really", "en-us+f2", 170, 60, "Really?"),
    ("speech_andthen", "en-gb+f1", 175, 55, "And then?"),
    ("speech_tellmemore", "en-us+m4", 170, 45, "Tell me more."),
    ("speech_whathappened", "en-gb+m6", 175, 50, "What happened?"),
    ("speech_metrics_oneword", "en-us+m2", 165, 50, "Metrics?"),
    ("speech_anythingelse", "en-us+f3", 170, 55, "Anything else?"),
    ("speech_fast_ninetydays", "en-us+m5", 240, 50, "What would you do in your first ninety days?"),
]


def espeak(name: str, voice: str, wpm: int, pitch: int, text: str, tmp: str) -> np.ndarray:
    raw, res = os.path.join(tmp, name + "_raw.wav"), os.path.join(tmp, name + "_24k.wav")
    run(["espeak-ng", "-v", voice, "-s", str(wpm), "-p", str(pitch), "-w", raw, text])
    run(["ffmpeg", "-y", "-v", "error", "-i", raw, "-ar", str(SR), "-ac", "1", "-c:a", "pcm_s16le", "-bitexact", res])
    x = read_wav(res)
    # Trim eSpeak's leading/trailing silence to 80 ms, the remote talker's room: short, mostly dry reverb.
    idx = np.nonzero(np.abs(x) > 0.003)[0]
    x = x[max(0, idx[0] - int(0.08 * SR)): idx[-1] + int(0.08 * SR)]
    return normalize_peak(room_reverb(x, rng_for(name), 0.3, 0.12, tail=False))


# ------------------------------------------------------------------------------------------------------- the chains

def call_path(x: np.ndarray, name: str, noise: str, noise_dbfs: float, comp: tuple[float, float], tmp: str) -> np.ndarray:
    rng = rng_for(name + "/noise")
    x = normalize_peak(x, db(-6))                              # reference level of the remote signal
    n = (pink_noise if noise == "pink" else fan_noise)(len(x), rng) * db(noise_dbfs)
    y = x + n
    y = filt(y, "hp", 100, order=4)                            # meeting codec band limit
    y = filt(y, "lp", 7000, order=4)
    y = opus_roundtrip(y, tmp)
    y = compress(y, comp[0], comp[1])                          # AGC / dynamics of the client
    return y


VARIANTS = {
    "direct": dict(desc="local loopback, no codec/noise, -12 dBFS peak"),
    "callq": dict(desc="call path: pink noise -45 dBFS, HP100/LP7k, Opus 16k VOIP 20 kbps, AGC 2:1 @-20 dB"),
    "calln": dict(desc="call path: fan noise -35 dBFS, HP100/LP7k, Opus 16k VOIP 20 kbps, AGC 4:1 @-24 dB"),
}

QUIET_LEVELS = [-18, -24, -30]
LOUD_LEVELS = [-6, -8, -10]


def render_variant(base: np.ndarray, base_name: str, variant: str, index: int, tmp: str) -> tuple[np.ndarray, dict]:
    name = f"{base_name}__{variant}"
    if variant == "direct":
        y, info = normalize_peak(base, db(-12)), dict(peak_dbfs=-12, noise=None, clipped=False)
    elif variant == "callq":
        level = QUIET_LEVELS[index % len(QUIET_LEVELS)]
        y = normalize_peak(call_path(base, name, "pink", -45, (-20, 2), tmp), db(level))
        info = dict(peak_dbfs=level, noise="pink -45 dBFS RMS @ -6 dBFS ref", clipped=False)
    else:
        level = LOUD_LEVELS[index % len(LOUD_LEVELS)]
        y = normalize_peak(call_path(base, name, "fan", -35, (-24, 4), tmp), db(level))
        clipped = index % 2 == 0
        if clipped:   # playback/client gain pushed too far: hard clip ~1 % of samples
            limit = float(np.quantile(np.abs(y), 0.99))
            y = np.clip(y, -limit, limit) * (db(level) / limit)
        info = dict(peak_dbfs=level, noise="fan -35 dBFS RMS @ -6 dBFS ref", clipped=clipped)
    info["variant"] = VARIANTS[variant]["desc"]
    return y, info


def bases():
    """(base name, class, kind, variants, builder, description)."""
    yield ("chime_glock_dingdong", "nonspeech", "chime", ("direct", "callq", "calln"),
           lambda tmp: chime("chime_glock_dingdong", (1046.5, 784.0), 0.32, 0.55, GLOCK, 0.4, 1.1),
           "2-note descending notification chime C6-G5, glockenspiel partials 1/2.76/5.40/8.93, RT60 0.4 s")
    yield ("chime_glock_arpeggio", "nonspeech", "chime", ("direct", "callq", "calln"),
           lambda tmp: chime("chime_glock_arpeggio", (880.0, 1108.7, 1318.5), 0.12, 0.45, GLOCK, 0.5, 1.0),
           "3-note ascending A5-C#6-E6 chime, glockenspiel partials, RT60 0.5 s")
    yield ("chime_marimba_low", "nonspeech", "chime", ("direct", "callq", "calln"),
           lambda tmp: chime("chime_marimba_low", (587.3, 440.0, 659.3), 0.16, 0.35, MARIMBA, 0.3, 1.0),
           "3-note marimba-style chime D5-A4-E5, partials 1/3.93/9.24, RT60 0.3 s")
    yield ("error_critical_stop", "nonspeech", "error", ("direct", "callq", "calln"),
           lambda tmp: error_tone("error_critical_stop", (659.3, 440.0), 0.18, 0.9),
           "descending E5-A4 two-tone, 5 harmonics, 5.5 Hz +/-0.6 % vibrato, RT60 0.35 s")
    yield ("error_low_buzz", "nonspeech", "error", ("direct", "callq", "calln"),
           lambda tmp: error_tone("error_low_buzz", (392.0, 293.7), 0.16, 0.8, harmonics=(1, 0.7, 0.5, 0.35, 0.25, 0.15, 0.1)),
           "low descending G4-D4 error tone, 7 harmonics, vibrato (pitch inside the human voice range)")
    yield ("shutter_camera", "nonspeech", "shutter", ("direct", "callq", "calln"),
           lambda tmp: shutter("shutter_camera"),
           "screenshot/camera shutter: 2 band-passed noise clicks (15-20 ms) + spring rattle + RT60 0.3 s")
    yield ("click_keyboard", "nonspeech", "click", ("callq", "calln"),
           lambda tmp: keyboard("click_keyboard"),
           "laptop typing burst: 9 keystrokes (key-down + key-up clicks), irregular timing")
    yield ("click_mouse", "nonspeech", "click", ("callq", "calln"),
           lambda tmp: mouse("click_mouse"),
           "mouse: single, single, double click")
    yield ("knock_door", "nonspeech", "knock", ("callq", "calln"),
           lambda tmp: knock("knock_door"),
           "door knock: 3 low-frequency thumps (95-130 Hz panel mode) + mid 'tock', RT60 0.55 s")
    yield ("music_band", "nonspeech", "music", ("callq", "calln"),
           lambda tmp: music_band("music_band"),
           "instrumental pop: C-G-Am-F pad, plucked bass line, kick (pitched-down sine) / snare (filtered noise) / hi-hat")
    yield ("music_piano", "nonspeech", "music", ("callq", "calln"),
           lambda tmp: music_piano("music_piano"),
           "instrumental: arpeggiated Dm7-G7-Cmaj7 piano (inharmonic strings), walking bass, brush percussion")
    yield ("music_sung", "nonspeech", "music-sung", ("callq", "calln"),
           lambda tmp: sung_vowels("music_sung"),
           "HARD CASE: sung vowel melody A3-C4-E4-D4-C4 (glottal pulses, formants, 5.5 Hz vibrato) over pad + kick")
    for name, voice, wpm, pitch, text in SPEECH:
        yield (name, "speech", "speech", ("callq", "calln"),
               (lambda tmp, a=(name, voice, wpm, pitch, text): espeak(*a, tmp)),
               f'eSpeak NG voice {voice}, {wpm} wpm, pitch {pitch}: "{text}"')


def tool_versions() -> dict:
    def first_line(cmd):
        try:
            return subprocess.run(cmd, capture_output=True, text=True).stdout.splitlines()[0].strip()
        except Exception:  # noqa: BLE001
            return "unavailable"
    return {"python": sys.version.split()[0], "numpy": np.__version__,
            "espeak-ng": first_line(["espeak-ng", "--version"]).split("  Data at")[0],
            "ffmpeg": first_line(["ffmpeg", "-version"]).split(" Copyright")[0]}


def generate(out_dir: str) -> list[dict]:
    os.makedirs(out_dir, exist_ok=True)
    entries = []
    with tempfile.TemporaryDirectory() as tmp:
        for base_name, cls, kind, variants, build, desc in bases():
            base = build(tmp)
            for i, v in enumerate(variants):
                y, info = render_variant(base, base_name, v, zlib.crc32(base_name.encode()) % 6 + i, tmp)
                file = f"{base_name}__{v}.wav"
                write_wav(os.path.join(out_dir, file), y)
                with open(os.path.join(out_dir, file), "rb") as fh:
                    sha = hashlib.sha256(fh.read()).hexdigest()
                text = next((s[4] for s in SPEECH if s[0] == base_name), None)
                entries.append(dict(file=file, cls=cls, kind=kind, text=text, base=desc, duration_ms=round(len(y) * 1000 / SR),
                                    bytes=os.path.getsize(os.path.join(out_dir, file)), sha256=sha, **info))
                print(f"{file:44s} {cls:9s} {kind:10s} {len(y) / SR:5.2f} s", flush=True)
    with open(os.path.join(out_dir, "fixtures.json"), "w", encoding="utf-8") as fh:
        json.dump({"generator": "scripts/generate-audio-fixtures.py", "sample_rate": SR, "tools": tool_versions(),
                   "fixtures": entries}, fh, indent=1, ensure_ascii=False)
        fh.write("\n")
    return entries


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=OUT_DIR)
    ap.add_argument("--check", action="store_true", help="regenerate into a temp dir and compare with the committed files")
    args = ap.parse_args()
    for tool in ("espeak-ng", "ffmpeg"):
        if shutil.which(tool) is None:
            print(f"{tool} is required", file=sys.stderr)
            return 2
    if not args.check:
        for f in os.listdir(args.out) if os.path.isdir(args.out) else []:
            if f.endswith(".wav"):
                os.remove(os.path.join(args.out, f))
        entries = generate(args.out)
        total = sum(e["bytes"] for e in entries)
        print(f"{len(entries)} fixtures, {total / 1024:.0f} KiB")
        return 0
    with tempfile.TemporaryDirectory() as d:
        entries = generate(d)
        bad = []
        for e in entries:
            p = os.path.join(args.out, e["file"])
            if not os.path.exists(p) or hashlib.sha256(open(p, "rb").read()).hexdigest() != e["sha256"]:
                bad.append(e["file"])
        print("identical" if not bad else "differs: " + ", ".join(bad))
        return 0 if not bad else 1


if __name__ == "__main__":
    sys.exit(main())
