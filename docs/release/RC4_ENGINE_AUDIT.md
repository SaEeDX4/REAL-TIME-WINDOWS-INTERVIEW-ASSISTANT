# 2.1.0-rc.4 — engine audit

Scope: everything between the WASAPI loopback capture and the answer/history/report: acoustic analysis, the
partial-transcript trust boundary, turn detection, the utterance gate, question extraction, personal-answer
grounding, session memory and the report. Build under audit: 2.1.0-rc.4 (this branch). Baseline: 2.1.0-rc.3
(`dc67e80`).

## 1. The P0: a Windows sound became an answered question

### What happened in rc.3

A Windows notification sound was segmented by the server VAD, and the recognizer, primed with the interview's
vocabulary prompt, returned "What is your experience with FinTech products?". In rc.3:

1. The acoustic analyser reported the sound as *voiced*: alert tones and chord pads have a periodic waveform inside
   the human pitch range. Only narrow-band (tonal) sound and sustained identical spectra were rejected; a tone whose
   pitch simply *holds* was not.
2. `UtteranceGate` had an override: a well-formed question of six or more words was accepted even when the audio
   carried weak or no speech ("the recognizer would not invent a whole question"). Recognizers primed with a prompt
   do exactly that.
3. The live UI showed partial words ("Job interview") and switched to "Question detected" as soon as the VAD
   reported speech, before anything was known about the audio.
4. Nothing downstream re-checked: the turn became a history item, an AI request, SessionMemory, report question Q3 and
   report statistics.

### rc.4 design (defence in depth, no transcript lists)

| Layer | What it checks | Where |
|---|---|---|
| Acoustic features | per 20 ms hop: voiced / tonal / sustained; **pitch pairs** between consecutive in-range periodic hops exactly one hop apart, classed steady (<0.6 % change) or jumping (>2.5 %) | `Audio/AcousticAnalyzer.cs` |
| Voice-likeness | `SpeechEvidence.VoiceLike` = ≥100 ms voiced, not dominated by tonal sound (only judged with ≥100 ms tonal), and — once ≥10 pitch pairs exist — a pitch that moves (steady share < 65 %) | `SpeechEvidence` |
| Plausibility | the words must fit the audio: a syllable lower bound vs. voiced and active time (`Implausible`: < 35 ms voiced per syllable or > 9 syllables per active second; `Weak` below 70 ms voiced per syllable; `Strong` above). Abstains when the audio window was not observed (reconnect, ring overflow) | `Intelligence/SpeechPlausibility.cs` |
| Context hallucination | a sentence built from the interview's own vocabulary over too little speech (`Weak` support and overlap with the prompt terms) | `PromptEchoDetector.ContextOverlap`, `UtteranceGate` |
| Gate order | artefact → not voice-like (`ShortAcousticEvent` / `NonSpeech`) → `ImplausibleTranscript` → `ContextHallucination` → prompt echo → sentence classification. The six-word override is removed | `Intelligence/UtteranceGate.cs` |
| Partial trust boundary | a live partial is shown only when the audio so far has a judged pitch track (≥10 pairs), is voice-like and gives the words `Strong` support; short partials are re-checked on every delta, so a sentence that outgrows its sound loses trust immediately | `InterviewEngine.TrustPartial`, `TurnDetector.OnPartial` |
| Neutral status | `HearingAudio` ("Hearing audio") on VAD speech start until trusted words exist; `Finalizing` only when the gate would answer the current turn | `InterviewEngine.OnTurnState` |
| Report | every heard question keeps its acoustic provenance (`HeardEvidence`: sizes and ratios only); the report re-checks it and lists failures under "Possibly not interviewer speech (not counted)", excluded from every statistic | `Live/SessionMemory.cs`, `Reports/InterviewReport.cs` |

Nothing in this design looks at *which* words were produced: there is no list of fake strings, no word-count floor,
no question-mark requirement and no global timeout change.

### Short real turns stay answerable

"Why?", "Why.", "How so?", "Really?", "And then?", "Tell me more.", "What happened?", "Metrics?", "Anything else?"
are all answered from real (synthesized) speech over two call paths. Changes needed for that:

- `TurnDetector.IsNonQuestion` no longer requires a literal "?" for a one-word turn: an interrogative written with a
  falling period ("Why.") counts; acknowledgements and fillers ("Okay?", "Hm?", "Sorry?") do not.
- `UtteranceGate.IsQuestionSentence` recognises elliptical follow-ups ("How so.", "What happened.", "And then.") as
  a closed set of English interrogative forms; exclamations ("What a day.") are not questions.
- Tonal share is judged only with ≥100 ms of tonal sound (a half-second "And then?" over a call had 60 ms).
- Pitch steadiness is judged only with ≥10 pitch pairs (a "Why?" has 4–6).

## 2. Acceptance results (real audio)

`tests/fixtures/audio/` holds 60 CC0 fixtures generated in the repository (`scripts/generate-audio-fixtures.py`):
chimes, alert tones (one deliberately inside the voice range), screenshot shutter, keyboard and mouse clicks, a door
knock, three music fragments (one sung), and 15 speech phrases (English, German, Spanish; 1 to 9 words; 160–240
wpm), each through a local loopback path and two call paths (Opus, band limit, AGC, noise, clipping). All 16 speech
fixtures added in rc.4 are short follow-ups and a fast talker.

| Check (`RecordedStyleAudioTests`) | rc.3 | rc.4 |
|---|---|---|
| Non-speech fixtures classified voice-like | 18 of 30 | **3 of 30** (quiet knock, sung melody ×2 — documented) |
| Speech fixtures classified voice-like | 14 of 14 | 30 of 30 |
| Non-speech heard as the prompt / "Thank you." / nothing → answered | 0 | 0 |
| Non-speech heard as a short question ("How so?" …) → answered | 18 | **0** except the 3 look-alikes |
| Non-speech heard as a context-derived question ("What is your experience with FinTech products?" and two more) → answered, history, memory | not tested | **0** for every fixture except the sung melody |
| Live text or "transcribing"/"finalizing" shown for a non-look-alike sound | not tested | **never** |
| Speech fixture answered exactly once | 14 of 14 | 30 of 30 |
| First live words for real speech (2–2.3 s phrases) | — | 560–740 ms after the sound starts |

For every non-speech fixture the suite asserts: 0 finalized questions, 0 answer requests, 0 SessionMemory entries,
0 history entries, 0 report questions, and (outside the documented look-alikes) 0 text flashed.

### Residual risks, stated plainly

- **Singing.** A sung vowel melody is voiced, non-tonal, moving-pitch audio: acoustically a voice. If a recognizer
  turns singing into a well-formed interview question, it can be answered. The text layer still drops what
  recognizers usually produce for music ("♪", "Thanks for watching!", the prompt echo).
- **A quiet knock** is voice-like (low panel resonance with a wobbling period). A short phrase over it can be
  answered; a full interview sentence cannot (it is far longer than the sound), and its first words can show for a
  moment before the sentence loses trust.
- **Real voices, codecs and system sounds** differ from generated fixtures. The fixtures are built the way real ones
  are, but they are not recordings of actual Windows/Teams sounds. Manual check C (RC4_MANUAL_REGRESSION) covers the
  real sounds.

## 3. Turn detection and long context

New regression tests (`LongContextRegressionTests`): 30 s, 60 s and 2 min of interviewer background in several
segments, followed by a two-part question, a mid-sentence rephrase, an interruption ("Sorry, one second…") or a quick
continuation. Each produces exactly one answer with the right core ask and the constraints in the prompt.

They found two real defects, fixed in rc.4:

1. **A continued turn was judged against only its newest audio.** When context held from an earlier turn was
   continued, the merged text was compared with the new segment's evidence alone, so long real speech looked
   implausible and was dropped. The continued turn now carries both turns' evidence (`TurnDetector.Finalize`).
2. **"…hard to explain in the app." was answered as a request.** Imperative verbs (explain, describe, share,
   elaborate, imagine, suppose, give me) now count only in imperative position: sentence start, after "please", "can
   you"/"could you", or "I want you to".

Also fixed: a sentence that only restates the ask is no longer shown as its own context line
(`QuestionExtractor.RestatesAsk`, in-turn and held context).

Existing coverage still passing: 5-minute monologue, 10 minutes of fragmented speech, multi-part questions across
pauses, restatements, reconnect right after a question, pause/resume continuity, variants on older items.

## 4. Panel interviews

Unchanged in design and honest about it: there is **no diarization**, and no voiceprint is computed or stored. A
sentence addressed by name to someone other than the candidate ("John, what do you think?") is not answered; a
sentence addressed to the candidate by name ("Shervin, how would you approach this?") is. Overlap and hand-overs
("Sorry, go ahead." / "No, you go.") are logistics; the answer waits for a coherent candidate-directed turn. Tests:
`Panel_*` in `Rc2EngineAcceptanceTests` and the soak test.

## 5. Personal answers

rc.3 sent confirmed facts with the instruction "use ONLY these confirmed facts", but the answer structure asked for
"one genuine point → why it matters to you → link to the role", which invited invented feelings and benefits.

rc.4:
- The structure for a personal question with confirmed facts is "the confirmed facts, rephrased in first person —
  nothing added", and the prompt forbids reasons, feelings, benefits, traits, frequencies, places, dates and role
  links.
- In English, every streamed bullet of such an answer is checked against the facts, the question and a small set of
  neutral framing words (`PersonalGrounding`) before display. A bullet that adds anything is dropped. If nothing
  remains, the candidate's confirmed words are shown verbatim. Other answer languages rely on the prompt rule (their
  wording cannot be compared word for word); this is a stated limitation.
- Without confirmed facts there is still no model call: the "Profile gap" guidance is shown (structure only).

Tests: `PersonalGroundingTests` (hobby "Hiking and photography": rephrasings pass; "keeps me grounded", "taught me
patience", "every weekend", "passionate about landscapes" fail; engine falls back to the confirmed words), and the
existing personal-question tests.

## 6. Latency

No LLM round trip was added. All new checks are local and cheap: the partial trust check runs per delta (p50
0.46 ms, p99 1.8 ms including the whole partial handler during a 5-minute monologue), the gate adds a syllable count
and an evidence sum per turn. Measured numbers before/after are in RC4_PERFORMANCE_AUDIT.

## 7. Privacy

Kept per question: frame counts, durations (ms) and pitch-pair counts — never audio, never spectra, never anything
that identifies a voice. Logs carry verdict kinds, reasons and sizes, never transcript text.
