# 2.1.0-rc.2 — interview engine audit

Scope: everything between the realtime transcription events and the bullets on screen
(`src/InterviewAssistant.Core`: `Audio/`, `Turn/`, `Intelligence/`, `Orchestration/`, `Live/`, `Reports/`).
Root causes of the RC1 defects are in [RC2_ROOT_CAUSES.md](RC2_ROOT_CAUSES.md); this document describes the
resulting architecture, each finding with severity / root cause / fix / test / remaining risk, and what is still
not solved.

## 1. Pipeline after RC2

```
WASAPI loopback (playback only, never the microphone)
  └─ 24 kHz PCM frames ──► AcousticAnalyzer (20 ms hop / 40 ms window @ 12 kHz)
                            voiced = autocorrelation ≥ 0.5 at pitch lags 2.5–12.5 ms
                            tonal  = 1–2 strong spectral peaks holding ≥ 90–97 % of the energy
                           └─► SpeechEvidenceTimeline (ring buffer, 1 800 frames ≈ 36 s)
server VAD speech_started / speech_stopped ─► TurnDetector (continuation window, bounded turns)
                           evidence for [segment start − 1 s, speech_stopped] attached to every segment
  segment ─► UtteranceGate.EvaluateSegment  (drops echo / non-speech segments before they can merge)
  turn    ─► UtteranceGate.Evaluate          Question | ImplicitPrompt | Context | Logistics | OtherDirected
                                             | PromptEcho | NonSpeech | ShortAcousticEvent | Noise
             Context  ─► PendingContext (≤ 1 500 chars, ≤ 2 min) — attached to the next question
             rejected ─► counted (Metrics.Suppression), TurnDetector.Forget, never recorded
  question ─► QuestionExtractor (core ask, sub-questions, constraints, entities, bounded context)
           ─► classify · match prepared bank · retrieve evidence · AnswerPlanner (local, no model call)
           ─► prepared answer (instant) | personal guidance (instant, no model) | ONE streamed model call
  stream  ─► BulletStreamParser ─► per-bullet: LeakGuard · FactValidator · metric guard · AnswerQualityGate
           ─► bullets on screen (default 3) ─► SessionMemory record ─► report
```

There is still exactly **one** model call per generated answer. Every new layer is local and measured (§4).

## 2. Findings

| # | Area | Severity | Root cause | Fix | Tests | Remaining risk |
|---|---|---|---|---|---|---|
| E1 | Fake questions from beeps / screenshot sounds | Critical | No speech evidence; recognizer echoes its bias prompt (ROOT_CAUSES #1) | Acoustic evidence + prompt-echo detector + utterance gate at segment and turn level; metrics `non_speech_suppressed`, `prompt_echo_suppressed`, `short_acoustic_event_suppressed`, `invalid_turn_suppressed` | `Rc2EngineAcceptanceTests` (8 noise scenarios), `AcousticAnalyzerTests`, RC2 soak (33 noise events → 0 records) | Validated on **synthetic** audio (harmonic speech model, sine beeps, noise-burst shutters, chords). Music with a singing voice can look voiced; the text layer (echo/noise/validity) is then the only defence. |
| E2 | Short real questions | High (regression risk of E1) | — | Gate never decides on length alone; one-word follow-ups with voiced speech pass; clear ≥ 6-word questions with weak evidence pass as `acoustic_override` | `VeryShortRealQuestions_AreAnswered` ("Why?", "How so?", "And the metrics?") | A very short question spoken over music could be vetoed. |
| E3 | Long speech freezing the UI | High | O(n²) re-joins, unbounded partials, one UI update per partial | Cached segment text, bounded buffers (partials 4 000 chars; turn 8 000 chars / 400 segments with middle elision), live text throttled to 120 ms and the last 360 chars | 5-min monologue (max handler 1.3–4.2 ms, 252 UI updates in 300 s), 10-min fragmented speech (+398 KB) | WPF rendering itself was not profiled on real hardware (see performance audit). |
| E4 | Answering the preamble of a monologue | High | Whole turn used as the question | `QuestionExtractor` (core ask ≤ 80 words, sub-questions, constraints most-recent-first, context ≤ 900 chars, summary ≤ 220) + held context | `LongContextEndingInTwoPartQuestion_ExtractsTheCoreAsk`, `ContextSplitAcrossPauses…`, `InterviewerRephrasesHalfway…` | Rule-based extraction; unusual phrasing ("Let's talk about X." with no ask) becomes context, not a question — by design. |
| E5 | Panel remarks answered | High | No addressee model; diarization not available | Conservative text directedness (vocatives with a name that is not the candidate's, hand-over phrases); no speaker identification, no voiceprints | 7 `Panel_*` scenarios, RC2 soak (10 remarks → 0 answers) | Remarks to another interviewer **without** a name or hand-over phrase are still answered (safe failure: an unneeded suggestion, never a missed question). |
| E6 | Multi-part questions split | High (RC1 fix, preserved) | — | Continuation window and re-answer of the same history entry kept | `TurnBoundaryAcceptanceTests.TwoPartQuestionWithNaturalPauseIsOneQuestionOneAnswer` (300/500/800/1000 ms), long soak (26 multi-part turns) | — |
| E7 | Variants on the wrong item / new history entries | High | `RequestVariantAsync` used `Current` and appended | Variant targets the displayed id, replaces in place, separate CTS | `VariantOnOlderItem_…`, soak (11 variants, history unchanged), self-test | — |
| E8 | Pause/resume reset the session | High | Settings dialog rebuilt the engine | No rebuild while a session exists; live-safe settings applied in place | `PauseResumeTwice_OneContinuousSession_SevenOfSeven` | — |
| E9 | Report missing / polluted | High | Refresh only on open; close path; analytics used every record | `ReportSaved` event, save on close (before network cleanup), report re-filters echo/empty records, suppressed turns disclosed as a count | `Rc2ReportTests`, soak report assertions | — |
| E10 | Over-softened confirmed claims | High | Bridge clause diluted support | Clause-scoped support | `DirectExperienceGap_KeepsConfirmedAdjacentHistoryInThePastTense`, evaluation suite (12/12 kept) | Support is lexical (topic overlap ≥ 0.5 with a confirmed fact); paraphrases with no shared topic words are softened (safe direction). |
| E11 | Fabrication | Critical (standing requirement) | — | Unsupported first-person past claims → "I'd…"; unconfirmed numbers stated as results → bullet dropped; personal questions use only confirmed profile answers, otherwise instant guidance without a model call | `AnswerQualityEvaluationTests` (0/36 unsupported, 0/36 fabricated metrics, 4/4 personal grounded, 4/4 honest guidance) | Third-person or non-numeric fabrications ("our team won an award") are not detectable lexically. |
| E12 | MiCA/MiCAR | Medium | Global rewrite | Target-scoped normalisation | `TargetScopedTerms_MiCAtoMiCAR…` | — |
| E13 | Rapid/Adaptive mode triggered by noise | Medium | Fed by every speech start and typed questions | Only accepted heard questions, measured from their own speech start | `LiveIntelligenceTests` (4) | — |
| E14 | Prompt injection via interviewer speech or documents | High | Untrusted text reaches the prompt | Interviewer text placed as quoted data with a SECURITY section; LeakGuard drops bullets with prompt internals/secrets; document injection guard | `InterviewerPromptInjection_IsTreatedAsDataAndLeaksAreNeverShown`, `UploadSecurityTests` | Document-derived text still reaches the system message (security audit L2). |
| E15 | Redundant bullets | Low | Model repeats itself | `AnswerQualityGate.IsRedundant` (Jaccard ≥ 0.6) while streaming | `RedundantBullets_AreDroppedWhileStreaming`, evaluation (0 duplicates) | — |

### Self-review of the RC2 diff (second pass, fixed in 796de96)

| # | Severity | Finding | Fix | Test |
|---|---|---|---|---|
| R1 | High | A variant cancelled by the next question, Stop or another variant replaced the original answer with an empty one in history and in the report (introduced while fixing E7) | The original returns to its history slot; the question record is untouched; the UI shows it with a note | `VariantCancelledByTheNextQuestion_KeepsTheOriginalAnswerInHistoryAndReport` |
| R2 | Medium | Speech evidence matched to segments in arrival order could drift after a dropped or out-of-order transcript, so a beep's evidence could veto a real short question | Evidence keyed by the realtime `item_id`; queue only as fallback; cleared on reconnect | `SpeechEvidence_FollowsItsOwnItem_EvenWhenTranscriptsArriveOutOfOrderOrAreDropped` |
| R3 | Medium | The acronym rule rewrote real words (USD→USDT, GDP→GDPR) for fintech vocabularies | Mixed-case terms only | `TargetScopedTerms_MiCAtoMiCAR_OnlyWhenTheTargetUsesMiCAR` |
| R4 | Low | A variant of a still-streaming answer skipped that question's memory update (story repetition) | Memory completed by the variant | engine tests |
| R5 | Low | Answer cancellation-source hand-back not atomic; cancelled cloud answers raised unobserved errors | `CompareExchange` hand-back; cancellation caught | engine tests |
| R6 | Low | The inserted honest bridge was English in German/Persian/… answers | Bridge text and cues per answer language | `HonestBridge_IsWrittenInTheAnswerLanguage` |

## 3. Answer planner and quality gate

* **Planner** (`AnswerPlanner.Plan`): from the extracted question, classification, prepared-bank match and retrieved
  evidence it builds an `AnswerPlan` — answer mode (verified / bridge / hypothetical / personal), the confirmed facts
  and stories the answer may use, constraints and sub-questions to cover, and an internal **confidence**
  (High/Medium/Low). It is rendered into the single prompt (`ANSWER PLAN` section). No extra model round trip.
* **Confidence policy**: High → prepared answer shown instantly; Medium → generated with the plan; Low → generated
  with explicit "ask a clarifying question or answer the core only" guidance. Personal questions without confirmed
  answers → `AnswerSource.Guidance` (no model call).
* **Quality gate** (`AnswerQualityGate.Assess` + per-bullet checks): leak guard, fact validator, metric guard,
  redundancy, bullet cap (3 default, 2 rapid, 4 technical), length flags (`QUALITY_*`) recorded on the question record
  for diagnostics.

## 4. Latency (virtual clock, same 106-question simulation before/after)

| Path | RC1 p50 / p95 / max | RC2 p50 / p95 / max |
|---|---|---|
| speech_stopped event → question finalized | 600 / 800 / 800 ms | 600 / 800 / 800 ms |
| prepared answer: finalized → first bullet | 0 / 0 / 0 ms | 0 / 0 / 0 ms |
| AI answer (700 ms simulated first token): finalized → first bullet | 1 020 / 1 020 / 2 480 ms | 1 020 / 1 020 / 2 460 ms |
| AI answer: finalized → complete | 1 580 / 1 580 / 3 040 ms | 1 560 / 1 580 / 3 000 ms |
| new local layers (gate + extract + classify + match + retrieve + plan), wall clock | — | p50 4.7–5.0 ms, p95 7.8–10.2 ms, max 20–48 ms (CI noise) |

No measurable regression: the new layers cost ≈ 5 ms against a ≈ 1 s model first token. Network and model time are
simulated; real first-token latency depends on the provider (≈ 0.7 s typical for the configured model).

## 5. Speaker attribution — limitations (stated plainly)

* The product does **not** know who is speaking. Realtime diarization was evaluated and is not reliably available for
  this transcription path (sources in ROOT_CAUSES). No speaker embeddings or voiceprints are computed or stored.
* "Directed at someone else" is inferred only from the text: a leading/trailing name that is not the candidate's
  (and not a known technology/product term), or hand-over phrases. A remark between interviewers without such cues is
  treated as a question to the candidate.
* Overlapping speech arrives as one transcript; the extractor keeps the most recent complete ask.

## 6. Verified by

`tests/InterviewAssistant.Tests`: 331 tests (engine acceptance 39, RC2 soak 61.7 virtual minutes with 115 questions,
long soak 43.7 virtual minutes with 106 questions, answer-quality evaluation 2, matcher evaluation, localization).

## rc.3 — recording-style audio validation

`tests/fixtures/audio` (44 CC0 fixtures generated by `scripts/generate-audio-fixtures.py`, provenance in its
`MANIFEST.md`) replaces "sine waves only" with sounds built like real ones — glockenspiel/marimba notification
chimes, critical-stop and buzz error tones, shutter, keyboard/mouse clicks, door knock, instrumental and sung music,
eSpeak NG speech in three languages — each played direct, through a quiet call and through a noisy call (noise,
100 Hz–7 kHz band limit, Opus 16 kHz round trip, compression, clipping).

| Group | Voiced ms before → after the rc.3 analyser fixes | Outcome |
|---|---|---|
| Arpeggio chime ×3, marimba (direct, quiet call), shutter ×3, clicks ×4 | up to 900 → ≤ 80 | rejected as short acoustic events |
| Two-note chime ×3, marimba (noisy call) | 660–860 → 100–120 | still classed as speech |
| Critical stop ×3, low buzz ×3 | 520–620 → 140–460 | still classed as speech |
| Knock ×2, band ×2, piano ×2 | 500–1 640 → 280–800 | still classed as speech |
| Sung melody ×2 | ~1 700 → ~1 600 | speech (inherently hard) |
| Speech ×14 | 200–1 820 → 180–1 640 | speech (lowest: "Why?") |

Engine-level results (`RecordedStyleAudioTests`): all 14 speech fixtures answered exactly once; all 30 non-speech
fixtures create nothing when the recogniser returns what it typically returns for them (prompt echo, "Thank you.",
empty, "♪", "(music)"). If the recogniser turned one of the 18 still-speech-like sounds into a question-like sentence
it would be answered — asserted per fixture so that improvements and regressions show up. Limitations: synthetic
voice (regular pitch), one codec, no real Teams/Zoom processing, no overlapped speech; the real-PC check (manual
checklist G) remains required. Engine suite: 426 tests (rc.2: 331).
