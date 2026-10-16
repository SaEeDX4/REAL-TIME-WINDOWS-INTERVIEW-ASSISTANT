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
`error_critical_stop__calln`, `click_mouse__calln`, `speech_why__calln`, `speech_howso__calln`,
`speech_metrics__calln`, `speech_tellme__calln`, `speech_ninetydays__calln`.

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

Total: 44 files, 2,541,892 bytes (2482 KiB).

## Classification by the acoustic layer

Printed by `RecordedStyleAudioTests.AcousticAnalyzer_ClassifiesEveryFixture` (ITestOutputHelper; run with
`--logger "console;verbosity=detailed"`). Each fixture is streamed in 100 ms frames (the last one padded with silence)
and summed like the engine's evidence timeline. *Speech* = `SpeechEvidence.HasSpeech` (≥ 100 ms voiced); *short
non-speech event* = `IsShortNonSpeechEvent`. "Before" is the analyser at the parent commit, "after" the current one.

| Fixture | Expected | Voiced ms (before → after) | Tonal ms (after) | Active ms | Duration ms | Classification (after) |
|---|---|---:|---:|---:|---:|---|
| `chime_glock_dingdong__direct.wav` | known look-alike (voiced) | 700 → 100 | 420 | 1080 | 1100 | speech |
| `chime_glock_dingdong__callq.wav` | known look-alike (voiced) | 700 → 100 | 440 | 1080 | 1100 | speech |
| `chime_glock_dingdong__calln.wav` | known look-alike (voiced) | 660 → 120 | 480 | 1100 | 1100 | speech |
| `chime_glock_arpeggio__direct.wav` | not speech | 900 → 60 | 100 | 980 | 1000 | short non-speech event |
| `chime_glock_arpeggio__callq.wav` | not speech | 880 → 60 | 120 | 980 | 1000 | short non-speech event |
| `chime_glock_arpeggio__calln.wav` | not speech | 900 → 80 | 100 | 1000 | 1000 | short non-speech event |
| `chime_marimba_low__direct.wav` | not speech | 860 → 60 | 200 | 980 | 1000 | short non-speech event |
| `chime_marimba_low__callq.wav` | not speech | 860 → 60 | 380 | 980 | 1000 | short non-speech event |
| `chime_marimba_low__calln.wav` | known look-alike (voiced) | 860 → 100 | 340 | 1000 | 1000 | speech |
| `error_critical_stop__direct.wav` | known look-alike (voiced) | 580 → 140 | 440 | 640 | 900 | speech |
| `error_critical_stop__callq.wav` | known look-alike (voiced) | 520 → 140 | 380 | 580 | 900 | speech |
| `error_critical_stop__calln.wav` | known look-alike (voiced) | 580 → 180 | 360 | 900 | 900 | speech |
| `error_low_buzz__direct.wav` | known look-alike (voiced) | 600 → 400 | 0 | 600 | 800 | speech |
| `error_low_buzz__callq.wav` | known look-alike (voiced) | 580 → 420 | 0 | 580 | 800 | speech |
| `error_low_buzz__calln.wav` | known look-alike (voiced) | 620 → 460 | 0 | 800 | 800 | speech |
| `shutter_camera__direct.wav` | not speech | 0 → 0 | 0 | 280 | 700 | short non-speech event |
| `shutter_camera__callq.wav` | not speech | 0 → 0 | 0 | 240 | 700 | short non-speech event |
| `shutter_camera__calln.wav` | not speech | 20 → 20 | 0 | 660 | 700 | short non-speech event |
| `click_keyboard__callq.wav` | not speech | 0 → 0 | 0 | 860 | 1200 | short non-speech event |
| `click_keyboard__calln.wav` | not speech | 0 → 0 | 0 | 1160 | 1200 | short non-speech event |
| `click_mouse__callq.wav` | not speech | 0 → 0 | 0 | 320 | 1000 | short non-speech event |
| `click_mouse__calln.wav` | not speech | 40 → 0 | 0 | 1000 | 1000 | short non-speech event |
| `knock_door__callq.wav` | known look-alike (voiced) | 500 → 360 | 40 | 620 | 1000 | speech |
| `knock_door__calln.wav` | known look-alike (voiced) | 520 → 280 | 100 | 1000 | 1000 | speech |
| `music_band__callq.wav` | known look-alike (voiced) | 1200 → 800 | 0 | 1680 | 1700 | speech |
| `music_band__calln.wav` | known look-alike (voiced) | 1260 → 680 | 0 | 1700 | 1700 | speech |
| `music_piano__callq.wav` | known look-alike (voiced) | 1620 → 520 | 0 | 1680 | 1700 | speech |
| `music_piano__calln.wav` | known look-alike (voiced) | 1640 → 580 | 0 | 1700 | 1700 | speech |
| `music_sung__callq.wav` | known look-alike (voiced) | 1600 → 1520 | 160 | 1800 | 2000 | speech |
| `music_sung__calln.wav` | known look-alike (voiced) | 1780 → 1640 | 100 | 2000 | 2000 | speech |
| `speech_why__callq.wav` | speech | 200 → 180 | 0 | 400 | 500 | speech |
| `speech_why__calln.wav` | speech | 220 → 200 | 0 | 460 | 500 | speech |
| `speech_howso__callq.wav` | speech | 420 → 420 | 0 | 620 | 700 | speech |
| `speech_howso__calln.wav` | speech | 400 → 400 | 0 | 640 | 700 | speech |
| `speech_metrics__callq.wav` | speech | 740 → 760 | 0 | 920 | 1200 | speech |
| `speech_metrics__calln.wav` | speech | 720 → 700 | 0 | 1160 | 1200 | speech |
| `speech_tellme__callq.wav` | speech | 900 → 860 | 20 | 1240 | 1500 | speech |
| `speech_tellme__calln.wav` | speech | 880 → 860 | 20 | 1500 | 1500 | speech |
| `speech_ninetydays__callq.wav` | speech | 1580 → 1400 | 0 | 1660 | 2100 | speech |
| `speech_ninetydays__calln.wav` | speech | 1760 → 1620 | 0 | 2100 | 2100 | speech |
| `speech_de_warum__callq.wav` | speech | 1820 → 1640 | 0 | 2060 | 2300 | speech |
| `speech_de_warum__calln.wav` | speech | 1700 → 1540 | 40 | 2220 | 2300 | speech |
| `speech_es_reto__callq.wav` | speech | 1220 → 1140 | 0 | 1260 | 1800 | speech |
| `speech_es_reto__calln.wav` | speech | 1400 → 1300 | 20 | 1760 | 1800 | speech |

Summary (after): all 14 speech fixtures are speech (minimum: "Why?" with 180 ms voiced); 12 of 30 non-speech fixtures
are rejected (every shutter and click, 5 of 9 chimes, all as short non-speech events); 18 are **known look-alikes**
that still carry ≥ 100 ms of voiced-looking audio (listed with reasons in
`RecordedStyleAudioTests.KnownVoicedLookalikes`). Before the analyser change only the 7 shutter/click fixtures were
rejected and every chime and error tone produced 520–900 ms of "speech".

### Engine level (InterviewSimulator.SoundFromPcm)

- Every non-speech fixture, played through the engine while the recognizer "hears" the transcription prompt, "Thank
  you." or nothing: **no answer, no question recorded, no provider request** (all 30, including the look-alikes — the
  prompt-echo and text layers catch those transcripts).
- Every non-speech fixture heard as a plausible short question ("How so?"): the 12 rejected fixtures are vetoed
  acoustically (`ShortAcousticEvent`); the 18 look-alikes **are answered**. This is the residual risk: a recognizer that
  turns a doorbell-like two-note chime, an alert tone, a knock or music into a short question will still trigger an
  answer. The test asserts this split so any change is visible.
- Every speech fixture with its real words (English, German, Spanish; down to "Why?"): answered exactly once.
- The sung melody (`music_sung__*`) is voiced, non-tonal, pitch-in-range audio — speech, as far as acoustics go. It is
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
