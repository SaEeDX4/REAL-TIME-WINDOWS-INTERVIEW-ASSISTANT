# Recording-style audio fixtures

Fixtures for the non-speech gate (`AcousticAnalyzer` → `SpeechEvidence` → `UtteranceGate`), used by
`tests/InterviewAssistant.Tests/RecordedStyleAudioTests.cs`. They replace "a pure sine is a beep" with sounds built
the way real notification sounds, clicks, knocks, music and call audio are built, and pass each one through the path
it takes to the WASAPI loopback capture.

All files are **24 kHz, mono, 16-bit PCM WAV** — the exact format the engine analyses — and are streamed by the tests
in 100 ms frames (4 800 bytes, the size `AudioCaptureService`/`AudioFramer` deliver).

## Provenance and licence

- **Every file is generated in this repository** by [`scripts/generate-audio-fixtures.py`](../../../scripts/generate-audio-fixtures.py).
  Nothing was recorded, downloaded or copied: no Windows/macOS system sounds, no music, no speech corpora, no
  third-party samples. The non-speech sounds and music are synthesized from first principles (sines, filtered noise,
  glottal pulses, convolution reverb with a synthetic impulse response) in the script itself.
- **Speech** is the run-time output of the [eSpeak NG](https://github.com/espeak-ng/espeak-ng) formant synthesizer
  (GPL-3.0 program) with its built-in voices only; the MBROLA voices, which carry their own non-commercial licence, are
  not used. Under the GPL, output produced by running the program is not covered by the program's licence; the
  repository contains no eSpeak NG code or data, only the generated waveforms (further processed by the script). The
  words are short generic interview phrases written for these tests.
- **Licence of the fixtures: CC0 1.0** (public-domain dedication) — they may be used and redistributed without
  restriction. No third-party content is included.
- Tools used for the committed files: Python 3.13.16, numpy 2.5.3, eSpeak NG 1.51, ffmpeg 6.1.1 (libopus) — recorded
  in `fixtures.json` together with a SHA-256 per file.

## Regenerating

```sh
python3 scripts/generate-audio-fixtures.py           # rewrites tests/fixtures/audio/*.wav and fixtures.json
python3 scripts/generate-audio-fixtures.py --check   # regenerates into a temp dir and compares checksums
```

Requires Python 3.10+, numpy, `espeak-ng` and `ffmpeg` with libopus. All randomness is numpy PCG64 seeded from a CRC32
of the fixture name, so runs are bit-identical with the same tool versions (verified with `--check`). Other eSpeak NG or
libopus versions can change speech/codec samples slightly; then commit the regenerated files and re-run the
classification test (its table is reproduced below).

## How the sounds are made (base sounds)

| Base | Class / kind | Generation (all parameters are in the script) |
|---|---|---|
| `chime_glock_dingdong` | non-speech / chime | 2-note descending notification chime C6→G5 (1046.5 → 784 Hz, 320 ms apart); each note = free-bar (glockenspiel) partials at ratios 1 / 2.76 / 5.40 / 8.93 with ±0.2 % detune, 1.2 ms attack, exponential decay (τ 0.55 s, higher partials faster); room reverb RT60 0.4 s |
| `chime_glock_arpeggio` | non-speech / chime | 3-note ascending A5–C♯6–E6 (120 ms apart), same partials, τ 0.45 s, RT60 0.5 s |
| `chime_marimba_low` | non-speech / chime | 3-note D5–A4–E5 marimba-bar chime (partials 1 / 3.93 / 9.24), τ 0.35 s, RT60 0.3 s |
| `error_critical_stop` | non-speech / error | "critical stop" style descending two-tone E5→A4 (180 ms each), 5 harmonics (1, .55, .3, .18, .1), 5.5 Hz ±0.6 % vibrato, soft release, RT60 0.35 s |
| `error_low_buzz` | non-speech / error | low descending G4→D4 (392 → 294 Hz) error tone, 7 harmonics, same vibrato — deliberately *inside* the human pitch range |
| `shutter_camera` | non-speech / shutter | screenshot/camera shutter: first-curtain click (band-passed noise 2.8 kHz + 0.9 kHz, 20 ms, τ 3–6 ms), 90 ms spring rattle (140 Hz-gated noise at 4.2 kHz), second-curtain click 120 ms later, RT60 0.3 s |
| `click_keyboard` | non-speech / click | laptop typing: 9 keystrokes 70–160 ms apart, each key-down click (1.8–3.5 kHz band-passed noise) + low "thock" (250–450 Hz) + key-up click |
| `click_mouse` | non-speech / click | single, single, double mouse click (3.8 kHz press + 4.5 kHz release clicks) |
| `knock_door` | non-speech / knock | 3 knuckle knocks: damped 95–130 Hz panel mode (τ 35 ms, slight downward glide) + 2.3× partial + 600–900 Hz "tock"; room RT60 0.55 s |
| `music_band` | non-speech / music | ~143 bpm pop fragment: C–G–Am–F pad chords (8 harmonics), plucked bass line (C2 G2 A2 F2 + octave), kick = pitched-down sine 120→45 Hz, snare = 185 Hz body + band-passed noise, hi-hat = high-passed noise; RT60 0.5 s |
| `music_piano` | non-speech / music | 112 bpm arpeggiated Dm7–G7–Cmaj7 (12 harmonics, string inharmonicity B = 0.0004), walking bass, brush percussion; RT60 0.6 s |
| `music_sung` | non-speech / music-sung | **known hard case**: synthetic singer — Rosenberg-style glottal pulses through vowel formants (/a/ /o/ /e/), melody A3 C4 E4 D4 C4, 5.5 Hz ±3 % vibrato, breath noise — over a soft pad and kick |
| `speech_why` | speech | eSpeak NG `en-us+m3`, 165 wpm, pitch 45: "Why?" |
| `speech_howso` | speech | `en-gb+f3`, 170 wpm, pitch 60: "How so?" |
| `speech_metrics` | speech | `en-us+f4`, 175 wpm, pitch 55: "And the metrics?" |
| `speech_tellme` | speech | `en-gb+m1`, 160 wpm, pitch 40: "Tell me about yourself." |
| `speech_ninetydays` | speech | `en-us+m7`, 180 wpm, pitch 50: "What would you do in your first ninety days?" |
| `speech_de_warum` | speech | `de+m2`, 160 wpm, pitch 45: "Warum haben Sie sich bei uns beworben?" (German) |
| `speech_es_reto` | speech | `es+f2`, 165 wpm, pitch 55: "¿Cuál fue su mayor reto?" (Spanish) |
| `speech_why_falling` | speech | `en-gb+m3`, 160 wpm, pitch 35: "Why." (falling, no question mark — RC4) |
| `speech_really` | speech | `en-us+f2`, 170 wpm, pitch 60: "Really?" (RC4) |
| `speech_andthen` | speech | `en-gb+f1`, 175 wpm, pitch 55: "And then?" (RC4) |
| `speech_tellmemore` | speech | `en-us+m4`, 170 wpm, pitch 45: "Tell me more." (RC4) |
| `speech_whathappened` | speech | `en-gb+m6`, 175 wpm, pitch 50: "What happened?" (RC4) |
| `speech_metrics_oneword` | speech | `en-us+m2`, 165 wpm, pitch 50: "Metrics?" (RC4) |
| `speech_anythingelse` | speech | `en-us+f3`, 170 wpm, pitch 55: "Anything else?" (RC4) |
| `speech_fast_ninetydays` | speech | `en-us+m5`, **240 wpm**, pitch 50: "What would you do in your first ninety days?" — a deliberately fast talker for the speech-rate check (RC4) |

eSpeak output (22.05 kHz) is resampled to 24 kHz with ffmpeg, trimmed to 80 ms of lead/tail silence and given a short,
mostly dry room (RT60 0.3 s, the remote talker's room).

## Call-path variants (degradations)

Each base is rendered in 2–3 variants (file suffix):

| Suffix | Path | Processing |
|---|---|---|
| `direct` | a local system sound captured by loopback (chimes, error tones, shutter only) | no codec, no noise; peak −12 dBFS |
| `callq` | remote participant, quiet room | pink noise at −45 dBFS RMS (relative to a −6 dBFS signal peak) → meeting band limit (4th-order high-pass 100 Hz, low-pass 7 kHz) → Opus round trip (16 kHz, VOIP mode, 20 kbps, 20 ms frames) → AGC/compressor 2:1 above −20 dB (5 ms attack, 120 ms release) → playback volume: peak −18 / −24 / −30 dBFS |
| `calln` | remote participant, noisy/loud | fan noise (steeper-than-pink + 450 Hz bump + 0.7 Hz swell) at −35 dBFS RMS → same band limit and Opus → compressor 4:1 above −24 dB → peak −6 / −8 / −10 dBFS; every second base is also driven into hard clipping (top 1 % of samples) |

Background noise is added *before* the volume stage on purpose: on a real call the remote room noise is scaled together
with the remote voice by the local playback volume, so a quiet variant also has quieter noise. Clipped files:
`error_critical_stop__calln`, `click_mouse__calln`, `speech_why__calln`, `speech_howso__calln`, `speech_metrics__calln`, `speech_tellme__calln`, `speech_ninetydays__calln`, `speech_why_falling__calln`, `speech_really__calln`, `speech_fast_ninetydays__calln`.

## Files

| File | Class | Kind | Duration | Size | Variant | Peak dBFS | Background noise | Clipped | Words |
|---|---|---|---:|---:|---|---:|---|---|---|
| `chime_glock_dingdong__direct.wav` | nonspeech | chime | 1100 ms | 52 KiB | direct | -12 | — | no |  |
| `chime_glock_dingdong__callq.wav` | nonspeech | chime | 1100 ms | 52 KiB | callq | -24 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `chime_glock_dingdong__calln.wav` | nonspeech | chime | 1100 ms | 52 KiB | calln | -10 | fan -35 dBFS RMS @ -6 dBFS ref | no |  |
| `chime_glock_arpeggio__direct.wav` | nonspeech | chime | 1000 ms | 47 KiB | direct | -12 | — | no |  |
| `chime_glock_arpeggio__callq.wav` | nonspeech | chime | 1000 ms | 47 KiB | callq | -24 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `chime_glock_arpeggio__calln.wav` | nonspeech | chime | 1000 ms | 47 KiB | calln | -10 | fan -35 dBFS RMS @ -6 dBFS ref | no |  |
| `chime_marimba_low__direct.wav` | nonspeech | chime | 1000 ms | 47 KiB | direct | -12 | — | no |  |
| `chime_marimba_low__callq.wav` | nonspeech | chime | 1000 ms | 47 KiB | callq | -18 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `chime_marimba_low__calln.wav` | nonspeech | chime | 1000 ms | 47 KiB | calln | -8 | fan -35 dBFS RMS @ -6 dBFS ref | no |  |
| `error_critical_stop__direct.wav` | nonspeech | error | 900 ms | 42 KiB | direct | -12 | — | no |  |
| `error_critical_stop__callq.wav` | nonspeech | error | 900 ms | 42 KiB | callq | -24 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `error_critical_stop__calln.wav` | nonspeech | error | 900 ms | 42 KiB | calln | -10 | fan -35 dBFS RMS @ -6 dBFS ref | yes |  |
| `error_low_buzz__direct.wav` | nonspeech | error | 800 ms | 38 KiB | direct | -12 | — | no |  |
| `error_low_buzz__callq.wav` | nonspeech | error | 800 ms | 38 KiB | callq | -24 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `error_low_buzz__calln.wav` | nonspeech | error | 800 ms | 38 KiB | calln | -10 | fan -35 dBFS RMS @ -6 dBFS ref | no |  |
| `shutter_camera__direct.wav` | nonspeech | shutter | 650 ms | 31 KiB | direct | -12 | — | no |  |
| `shutter_camera__callq.wav` | nonspeech | shutter | 650 ms | 31 KiB | callq | -18 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `shutter_camera__calln.wav` | nonspeech | shutter | 650 ms | 31 KiB | calln | -8 | fan -35 dBFS RMS @ -6 dBFS ref | no |  |
| `click_keyboard__callq.wav` | nonspeech | click | 1150 ms | 54 KiB | callq | -24 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `click_keyboard__calln.wav` | nonspeech | click | 1150 ms | 54 KiB | calln | -10 | fan -35 dBFS RMS @ -6 dBFS ref | no |  |
| `click_mouse__callq.wav` | nonspeech | click | 1000 ms | 47 KiB | callq | -24 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `click_mouse__calln.wav` | nonspeech | click | 1000 ms | 47 KiB | calln | -10 | fan -35 dBFS RMS @ -6 dBFS ref | yes |  |
| `knock_door__callq.wav` | nonspeech | knock | 1000 ms | 47 KiB | callq | -30 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `knock_door__calln.wav` | nonspeech | knock | 1000 ms | 47 KiB | calln | -6 | fan -35 dBFS RMS @ -6 dBFS ref | no |  |
| `music_band__callq.wav` | nonspeech | music | 1700 ms | 80 KiB | callq | -24 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `music_band__calln.wav` | nonspeech | music | 1700 ms | 80 KiB | calln | -10 | fan -35 dBFS RMS @ -6 dBFS ref | no |  |
| `music_piano__callq.wav` | nonspeech | music | 1700 ms | 80 KiB | callq | -30 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `music_piano__calln.wav` | nonspeech | music | 1700 ms | 80 KiB | calln | -6 | fan -35 dBFS RMS @ -6 dBFS ref | no |  |
| `music_sung__callq.wav` | nonspeech | music-sung | 2000 ms | 94 KiB | callq | -30 | pink -45 dBFS RMS @ -6 dBFS ref | no |  |
| `music_sung__calln.wav` | nonspeech | music-sung | 2000 ms | 94 KiB | calln | -6 | fan -35 dBFS RMS @ -6 dBFS ref | no |  |
| `speech_why__callq.wav` | speech | speech | 443 ms | 21 KiB | callq | -24 | pink -45 dBFS RMS @ -6 dBFS ref | no | Why? |
| `speech_why__calln.wav` | speech | speech | 443 ms | 21 KiB | calln | -10 | fan -35 dBFS RMS @ -6 dBFS ref | yes | Why? |
| `speech_howso__callq.wav` | speech | speech | 621 ms | 29 KiB | callq | -18 | pink -45 dBFS RMS @ -6 dBFS ref | no | How so? |
| `speech_howso__calln.wav` | speech | speech | 621 ms | 29 KiB | calln | -8 | fan -35 dBFS RMS @ -6 dBFS ref | yes | How so? |
| `speech_metrics__callq.wav` | speech | speech | 1147 ms | 54 KiB | callq | -24 | pink -45 dBFS RMS @ -6 dBFS ref | no | And the metrics? |
| `speech_metrics__calln.wav` | speech | speech | 1147 ms | 54 KiB | calln | -10 | fan -35 dBFS RMS @ -6 dBFS ref | yes | And the metrics? |
| `speech_tellme__callq.wav` | speech | speech | 1482 ms | 70 KiB | callq | -24 | pink -45 dBFS RMS @ -6 dBFS ref | no | Tell me about yourself. |
| `speech_tellme__calln.wav` | speech | speech | 1482 ms | 70 KiB | calln | -10 | fan -35 dBFS RMS @ -6 dBFS ref | yes | Tell me about yourself. |
| `speech_ninetydays__callq.wav` | speech | speech | 2076 ms | 97 KiB | callq | -30 | pink -45 dBFS RMS @ -6 dBFS ref | no | What would you do in your first ninety days? |
| `speech_ninetydays__calln.wav` | speech | speech | 2076 ms | 97 KiB | calln | -6 | fan -35 dBFS RMS @ -6 dBFS ref | yes | What would you do in your first ninety days? |
| `speech_de_warum__callq.wav` | speech | speech | 2220 ms | 104 KiB | callq | -18 | pink -45 dBFS RMS @ -6 dBFS ref | no | Warum haben Sie sich bei uns beworben? |
| `speech_de_warum__calln.wav` | speech | speech | 2220 ms | 104 KiB | calln | -8 | fan -35 dBFS RMS @ -6 dBFS ref | no | Warum haben Sie sich bei uns beworben? |
| `speech_es_reto__callq.wav` | speech | speech | 1743 ms | 82 KiB | callq | -30 | pink -45 dBFS RMS @ -6 dBFS ref | no | ¿Cuál fue su mayor reto? |
| `speech_es_reto__calln.wav` | speech | speech | 1743 ms | 82 KiB | calln | -6 | fan -35 dBFS RMS @ -6 dBFS ref | no | ¿Cuál fue su mayor reto? |
| `speech_why_falling__callq.wav` | speech | speech | 460 ms | 22 KiB | callq | -18 | pink -45 dBFS RMS @ -6 dBFS ref | no | Why. |
| `speech_why_falling__calln.wav` | speech | speech | 460 ms | 22 KiB | calln | -8 | fan -35 dBFS RMS @ -6 dBFS ref | yes | Why. |
| `speech_really__callq.wav` | speech | speech | 639 ms | 30 KiB | callq | -30 | pink -45 dBFS RMS @ -6 dBFS ref | no | Really? |
| `speech_really__calln.wav` | speech | speech | 639 ms | 30 KiB | calln | -6 | fan -35 dBFS RMS @ -6 dBFS ref | yes | Really? |
| `speech_andthen__callq.wav` | speech | speech | 637 ms | 30 KiB | callq | -30 | pink -45 dBFS RMS @ -6 dBFS ref | no | And then? |
| `speech_andthen__calln.wav` | speech | speech | 637 ms | 30 KiB | calln | -6 | fan -35 dBFS RMS @ -6 dBFS ref | no | And then? |
| `speech_tellmemore__callq.wav` | speech | speech | 962 ms | 45 KiB | callq | -30 | pink -45 dBFS RMS @ -6 dBFS ref | no | Tell me more. |
| `speech_tellmemore__calln.wav` | speech | speech | 962 ms | 45 KiB | calln | -6 | fan -35 dBFS RMS @ -6 dBFS ref | no | Tell me more. |
| `speech_whathappened__callq.wav` | speech | speech | 769 ms | 36 KiB | callq | -30 | pink -45 dBFS RMS @ -6 dBFS ref | no | What happened? |
| `speech_whathappened__calln.wav` | speech | speech | 769 ms | 36 KiB | calln | -6 | fan -35 dBFS RMS @ -6 dBFS ref | no | What happened? |
| `speech_metrics_oneword__callq.wav` | speech | speech | 834 ms | 39 KiB | callq | -30 | pink -45 dBFS RMS @ -6 dBFS ref | no | Metrics? |
| `speech_metrics_oneword__calln.wav` | speech | speech | 834 ms | 39 KiB | calln | -6 | fan -35 dBFS RMS @ -6 dBFS ref | no | Metrics? |
| `speech_anythingelse__callq.wav` | speech | speech | 906 ms | 42 KiB | callq | -18 | pink -45 dBFS RMS @ -6 dBFS ref | no | Anything else? |
| `speech_anythingelse__calln.wav` | speech | speech | 906 ms | 42 KiB | calln | -8 | fan -35 dBFS RMS @ -6 dBFS ref | no | Anything else? |
| `speech_fast_ninetydays__callq.wav` | speech | speech | 1640 ms | 77 KiB | callq | -24 | pink -45 dBFS RMS @ -6 dBFS ref | no | What would you do in your first ninety days? |
| `speech_fast_ninetydays__calln.wav` | speech | speech | 1640 ms | 77 KiB | calln | -10 | fan -35 dBFS RMS @ -6 dBFS ref | yes | What would you do in your first ninety days? |

Total: 60 files, 3,199,852 bytes (3125 KiB).

## Classification by the acoustic layer (RC4)

Printed by `RecordedStyleAudioTests.AcousticAnalyzer_ClassifiesEveryFixture` (ITestOutputHelper; run with
`--logger "console;verbosity=detailed"`). Each fixture is streamed in 100 ms frames (the last one padded with silence)
and summed like the engine's evidence timeline. *Voice-like* = `SpeechEvidence.VoiceLike`: ≥ 100 ms voiced, not
dominated by narrow-band (tonal) sound, and — once at least 10 pitch pairs were measured — a pitch that moves rather
than holds (`PitchSteadiness` < 65 %). *Pitch pairs* are consecutive in-voice-range periodic 20 ms hops; *steady* = the
pitch changed by less than 0.6 %, *jumps* = by more than 2.5 % (reported, not used: creaky real voices jump too).
The RC3 "before → after" voiced columns are kept in git history (commit dc67e80).

| Fixture | Class | Kind | Voiced ms | Tonal ms | Active ms | Duration ms | Pitch pairs | Steady | Jumps | Voice-like | Classification | Expected |
| chime_glock_dingdong__direct.wav | nonspeech | chime | 100 | 420 | 1080 | 1100 | 18 | 83 % | 11 % | no: tonal 81 % | short non-speech event (tonal 81 %) | not a voice |
| chime_glock_dingdong__callq.wav | nonspeech | chime | 100 | 440 | 1080 | 1100 | 18 | 67 % | 28 % | no: tonal 81 % | short non-speech event (tonal 81 %) | not a voice |
| chime_glock_dingdong__calln.wav | nonspeech | chime | 120 | 480 | 1100 | 1100 | 17 | 76 % | 18 % | no: tonal 80 % | short non-speech event (tonal 80 %) | not a voice |
| chime_glock_arpeggio__direct.wav | nonspeech | chime | 60 | 100 | 980 | 1000 | 25 | 100 % | 0 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| chime_glock_arpeggio__callq.wav | nonspeech | chime | 60 | 120 | 980 | 1000 | 24 | 92 % | 8 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| chime_glock_arpeggio__calln.wav | nonspeech | chime | 80 | 100 | 1000 | 1000 | 25 | 96 % | 4 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| chime_marimba_low__direct.wav | nonspeech | chime | 60 | 200 | 980 | 1000 | 23 | 48 % | 22 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| chime_marimba_low__callq.wav | nonspeech | chime | 60 | 380 | 980 | 1000 | 13 | 62 % | 15 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| chime_marimba_low__calln.wav | nonspeech | chime | 100 | 340 | 1000 | 1000 | 14 | 57 % | 29 % | no: tonal 77 % | short non-speech event (tonal 77 %) | not a voice |
| error_critical_stop__direct.wav | nonspeech | error | 140 | 440 | 640 | 900 | 6 | 50 % | 50 % | no: tonal 76 % | short non-speech event (tonal 76 %) | not a voice |
| error_critical_stop__callq.wav | nonspeech | error | 140 | 380 | 580 | 900 | 6 | 67 % | 33 % | no: tonal 73 % | short non-speech event (tonal 73 %) | not a voice |
| error_critical_stop__calln.wav | nonspeech | error | 180 | 360 | 900 | 900 | 6 | 50 % | 50 % | no: tonal 67 % | short non-speech event (tonal 67 %) | not a voice |
| error_low_buzz__direct.wav | nonspeech | error | 400 | 0 | 600 | 800 | 17 | 82 % | 12 % | no: steady pitch 82 % | short non-speech event (steady pitch 82 %) | not a voice |
| error_low_buzz__callq.wav | nonspeech | error | 420 | 0 | 580 | 800 | 17 | 76 % | 24 % | no: steady pitch 76 % | short non-speech event (steady pitch 76 %) | not a voice |
| error_low_buzz__calln.wav | nonspeech | error | 460 | 0 | 800 | 800 | 17 | 76 % | 24 % | no: steady pitch 76 % | short non-speech event (steady pitch 76 %) | not a voice |
| shutter_camera__direct.wav | nonspeech | shutter | 0 | 0 | 280 | 700 | 0 | 0 % | 0 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| shutter_camera__callq.wav | nonspeech | shutter | 0 | 0 | 240 | 700 | 0 | 0 % | 0 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| shutter_camera__calln.wav | nonspeech | shutter | 20 | 0 | 660 | 700 | 0 | 0 % | 0 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| click_keyboard__callq.wav | nonspeech | click | 0 | 0 | 860 | 1200 | 0 | 0 % | 0 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| click_keyboard__calln.wav | nonspeech | click | 0 | 0 | 1160 | 1200 | 0 | 0 % | 0 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| click_mouse__callq.wav | nonspeech | click | 0 | 0 | 320 | 1000 | 0 | 0 % | 0 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| click_mouse__calln.wav | nonspeech | click | 0 | 0 | 1000 | 1000 | 0 | 0 % | 0 % | no: no voiced sound | short non-speech event (no voiced sound) | not a voice |
| knock_door__callq.wav | nonspeech | knock | 360 | 40 | 620 | 1000 | 11 | 18 % | 55 % | yes | voice | known look-alike (voice-like) |
| knock_door__calln.wav | nonspeech | knock | 280 | 100 | 1000 | 1000 | 9 | 33 % | 44 % | no: tonal 26 % | short non-speech event (tonal 26 %) | not a voice |
| music_band__callq.wav | nonspeech | music | 800 | 0 | 1680 | 1700 | 34 | 88 % | 0 % | no: steady pitch 88 % | non-speech (steady pitch 88 %) | not a voice |
| music_band__calln.wav | nonspeech | music | 680 | 0 | 1700 | 1700 | 31 | 81 % | 0 % | no: steady pitch 81 % | non-speech (steady pitch 81 %) | not a voice |
| music_piano__callq.wav | nonspeech | music | 520 | 0 | 1680 | 1700 | 48 | 94 % | 6 % | no: steady pitch 94 % | non-speech (steady pitch 94 %) | not a voice |
| music_piano__calln.wav | nonspeech | music | 580 | 0 | 1700 | 1700 | 48 | 90 % | 10 % | no: steady pitch 90 % | non-speech (steady pitch 90 %) | not a voice |
| music_sung__callq.wav | nonspeech | music-sung | 1520 | 160 | 1800 | 2000 | 45 | 38 % | 13 % | yes | voice | known look-alike (voice-like) |
| music_sung__calln.wav | nonspeech | music-sung | 1640 | 100 | 2000 | 2000 | 49 | 37 % | 16 % | yes | voice | known look-alike (voice-like) |
| speech_why__callq.wav | speech | speech | 180 | 0 | 400 | 500 | 4 | 25 % | 0 % | yes | voice | voice |
| speech_why__calln.wav | speech | speech | 200 | 0 | 460 | 500 | 5 | 20 % | 60 % | yes | voice | voice |
| speech_howso__callq.wav | speech | speech | 420 | 0 | 620 | 700 | 10 | 50 % | 20 % | yes | voice | voice |
| speech_howso__calln.wav | speech | speech | 400 | 0 | 640 | 700 | 8 | 75 % | 0 % | yes | voice | voice |
| speech_metrics__callq.wav | speech | speech | 760 | 0 | 920 | 1200 | 22 | 45 % | 23 % | yes | voice | voice |
| speech_metrics__calln.wav | speech | speech | 700 | 0 | 1160 | 1200 | 20 | 35 % | 25 % | yes | voice | voice |
| speech_tellme__callq.wav | speech | speech | 860 | 20 | 1240 | 1500 | 24 | 38 % | 12 % | yes | voice | voice |
| speech_tellme__calln.wav | speech | speech | 860 | 20 | 1500 | 1500 | 21 | 43 % | 10 % | yes | voice | voice |
| speech_ninetydays__callq.wav | speech | speech | 1400 | 0 | 1660 | 2100 | 45 | 24 % | 29 % | yes | voice | voice |
| speech_ninetydays__calln.wav | speech | speech | 1620 | 0 | 2100 | 2100 | 48 | 29 % | 31 % | yes | voice | voice |
| speech_de_warum__callq.wav | speech | speech | 1640 | 0 | 2060 | 2300 | 49 | 51 % | 10 % | yes | voice | voice |
| speech_de_warum__calln.wav | speech | speech | 1540 | 40 | 2220 | 2300 | 41 | 44 % | 20 % | yes | voice | voice |
| speech_es_reto__callq.wav | speech | speech | 1140 | 0 | 1260 | 1800 | 32 | 28 % | 12 % | yes | voice | voice |
| speech_es_reto__calln.wav | speech | speech | 1300 | 20 | 1760 | 1800 | 37 | 27 % | 14 % | yes | voice | voice |
| speech_why_falling__callq.wav | speech | speech | 200 | 0 | 460 | 500 | 6 | 33 % | 0 % | yes | voice | voice |
| speech_why_falling__calln.wav | speech | speech | 240 | 0 | 460 | 500 | 6 | 33 % | 0 % | yes | voice | voice |
| speech_really__callq.wav | speech | speech | 360 | 0 | 440 | 700 | 13 | 15 % | 23 % | yes | voice | voice |
| speech_really__calln.wav | speech | speech | 420 | 0 | 640 | 700 | 14 | 0 % | 21 % | yes | voice | voice |
| speech_andthen__callq.wav | speech | speech | 240 | 60 | 420 | 700 | 7 | 57 % | 14 % | yes | voice | voice |
| speech_andthen__calln.wav | speech | speech | 460 | 0 | 640 | 700 | 11 | 45 % | 9 % | yes | voice | voice |
| speech_tellmemore__callq.wav | speech | speech | 540 | 40 | 840 | 1000 | 15 | 13 % | 13 % | yes | voice | voice |
| speech_tellmemore__calln.wav | speech | speech | 520 | 80 | 1000 | 1000 | 13 | 23 % | 0 % | yes | voice | voice |
| speech_whathappened__callq.wav | speech | speech | 300 | 0 | 440 | 800 | 7 | 0 % | 14 % | yes | voice | voice |
| speech_whathappened__calln.wav | speech | speech | 320 | 0 | 800 | 800 | 8 | 0 % | 25 % | yes | voice | voice |
| speech_metrics_oneword__callq.wav | speech | speech | 360 | 0 | 380 | 900 | 9 | 44 % | 33 % | yes | voice | voice |
| speech_metrics_oneword__calln.wav | speech | speech | 340 | 0 | 840 | 900 | 8 | 62 % | 38 % | yes | voice | voice |
| speech_anythingelse__callq.wav | speech | speech | 640 | 0 | 820 | 1000 | 18 | 6 % | 33 % | yes | voice | voice |
| speech_anythingelse__calln.wav | speech | speech | 640 | 0 | 920 | 1000 | 18 | 17 % | 33 % | yes | voice | voice |
| speech_fast_ninetydays__callq.wav | speech | speech | 1260 | 0 | 1520 | 1700 | 39 | 21 % | 23 % | yes | voice | voice |
| speech_fast_ninetydays__calln.wav | speech | speech | 1260 | 20 | 1640 | 1700 | 36 | 28 % | 22 % | yes | voice | voice |

Summary: all 30 speech fixtures are voice-like (minimum: "Why?" with 180 ms voiced and 4 pitch pairs). Of the
30 non-speech fixtures, 27 are not voice-like — every shutter, click, chime, alert tone (including the in-range
low buzz, by its steady pitch), the noisy knock and all instrumental music — and 3 remain **known look-alikes** with the
reasons listed in `RecordedStyleAudioTests.KnownVoiceLookalikes`: the quiet knock (`knock_door__callq`) and the sung
melody (`music_sung__*`). In RC3, 18 of 30 non-speech fixtures were look-alikes.

### Engine level (InterviewSimulator.SoundFromPcm, partial transcripts streamed word by word)

- Every non-speech fixture while the recognizer "hears" the transcription prompt, "Thank you." or nothing: no answer,
  no question recorded, no provider request; for every fixture that is not a look-alike, no live text and no
  "transcribing"/"finalizing" status either.
- Every non-speech fixture heard as a short real-sounding question ("How so?", "Why?", "Really?", "Tell me more.",
  "What happened?"): every non-look-alike is vetoed acoustically (`ShortAcousticEvent`/`NonSpeech`) with nothing shown.
  The look-alikes are the documented residual for *short* phrases.
- Every non-speech fixture heard as a well-formed interview question built from the prompt vocabulary (the RC3
  manual failure "What is your experience with FinTech products?" and two others): nothing answered, nothing in
  history, for every fixture except the sung melody — including the quiet knock, which is voice-like but far too short
  to have said the sentence (`ContextHallucination`). For the knock a few leading words may show for a moment, but the
  sentence loses its trust as it outgrows the sound and is never shown whole or finalized.
- Every speech fixture with its real words (English, German, Spanish; "Why?", a falling "Why.", "Really?", "And
  then?", "Metrics?", up to a 240 wpm talker): answered exactly once.
- The sung melody (`music_sung__*`) is voiced, non-tonal, moving-pitch audio — speech, as far as acoustics go. It is
  asserted as such, together with the fact that the gate still rejects what recognizers produce for music ("♪ ♪",
  "Thanks for watching!", "(music)", the prompt echo).

## What the fixtures revealed, and the analyser change

With the original analyser every chime and alert tone looked like speech (520–900 ms voiced per sound) — the purely
synthetic beeps in `SyntheticAudio` had hidden this, because a single sine is caught by the narrow-band "tonal" rule but
a real chime is not. Three causes, three changes in `src/InterviewAssistant.Core/Audio/AcousticAnalyzer.cs`:

1. **The last hop of every frame was analysed on a truncated 20 ms window.** A 100 ms frame holds five 20 ms hops but
   only four full 40 ms windows; the fifth used the remaining 20 ms, which smears narrow partials. On a sustained tone
   every fifth hop therefore stopped looking tonal and counted 20 ms "voiced" per frame. Fix: the last hop analyses the
   frame's last full 40 ms window.
2. **The pitch detector accepted multiples of a period above the voice range.** Autocorrelation was searched only over
   lags 2.5–12.5 ms (80–400 Hz); a 1 kHz bell partial repeats every 1 ms, so it also correlates perfectly at 2.5, 3.5,
   4.5 ms … and was reported as a "voice" at 400, 286, 222 Hz. Fix: the correlation is also computed down to lag
   0.42 ms (2.4 kHz); if a shorter lag is about as periodic (within 0.02) and the in-range peak is an integer multiple of
   it (±4 %), the fundamental is above 400 Hz and the hop counts as tonal (chime/alert), not voiced. Speech is barely
   affected: its short-lag correlations (formant ringing) are not integer sub-periods of the pitch period.
3. **Sustained partials looked voiced.** Overlapping bell notes create a common period inside the voice range (C6 + G5 →
   262 Hz) and harmonic alert tones and chords are genuinely periodic there. What separates them from speech is that
   their spectrum does not move: the partials ring at fixed frequencies. Fix: a periodic hop whose power spectrum
   (70 Hz – 4 kHz) has cosine similarity ≥ 0.95 with the *disjoint* windows 40 ms before and after it (those inside the
   frame that hold sound; at least one) is "sustained" — active, but neither voiced nor tonal. Voiced speech does not
   hold its harmonics and formants that still over 80–120 ms (intonation and articulation move them), so speech keeps
   its evidence: the eSpeak fixtures lose at most ~11 % of their voiced time (some gain), the synthetic `SyntheticAudio.Speech` 15–23 %
   (it has fixed formants per syllable, i.e. it is *more* stationary than real speech), and every existing test is
   unchanged.

Thresholds were chosen on the measured distributions (per-hop features of all fixtures plus the existing synthetic
signals), keeping the existing speech tests' margins: a lower similarity threshold or an "either neighbour" rule
removes more music but pushes the synthetic 210 Hz speech below its 1 000 ms test floor and "Why?" towards 100 ms. Cost:
≈ 25 extra autocorrelation lags per analysed hop and a ~170-bin dot product; `Analysis_IsCheapEnoughForTheCaptureThread`
still passes.

## Limitations — what these fixtures still cannot represent

- **Real voices.** eSpeak NG is a formant synthesizer: perfectly regular glottal pulses (no jitter/shimmer, no
  creak/breathiness), smooth rule-based intonation and no coarticulation variability. Real speech is *less* stationary
  and less periodic per window; real low-energy, creaky or whispered speech ("hm?", a mumbled "why") is not covered.
- **Real codecs and clients.** One Opus build/configuration; no Teams/Zoom/Meet-specific processing (their noise
  suppression would remove much of the noise and some music, their AGC and packet-loss concealment add artefacts,
  comfort noise, jitter-buffer stretching). No packet loss, no echo-canceller residue of the candidate's own voice.
- **Real system sounds.** The chimes are modelled on how notification sounds are built (struck-bar partials, decays,
  baked-in reverb) but are not the actual Windows/macOS/Teams sounds, which may use layered samples, synth pads, or
  pitch inside the voice range (the look-alike cases above show why that matters).
- **Real rooms and devices.** Synthetic exponential-noise reverb (no early-reflection pattern, no room modes),
  ideal mixing; no laptop-speaker → microphone path, no Bluetooth headset codecs (SBC/mSBC 8–16 kHz).
- **Overlap.** Each fixture is a single event; speech over music, a chime during a question, or two talkers are not
  represented.
- **Music** is three short fragments; real music (vocals, distortion guitars, synth bass, dense mixes) varies far more
  — expect most music with a bass line or vocals to look voiced to this layer, as the known look-alikes do.
