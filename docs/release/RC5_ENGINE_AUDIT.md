# 2.1.0-rc.5 — engine audit

Scope: capture → acoustic analysis → partial trust boundary → turn detection → utterance gate → question extraction →
answer / history / SessionMemory / report. Build under audit: 2.1.0-rc.5 (this branch). Baseline: 2.1.0-rc.4
(`a0bc946`).

## 1. The P0: a real "Tell me more." was dropped (rc.4 regression)

### Root cause

Three independent layers each dropped part of the class "candidate-directed imperative follow-up". None of them was
a missing phrase; the fix therefore changes the rules, not a list.

| Layer | rc.4 behaviour | Why it dropped real follow-ups |
|---|---|---|
| Acoustic voice-likeness (`SpeechEvidence.VoiceLike`) | once ≥ 10 pitch pairs were measured, a pitch that changed < 0.6 % between adjacent 20 ms hops in ≥ 65 % of the pairs was a **held tone** → `ShortAcousticEvent` | Level, falling declarative intonation — how most people say "Tell me more.", "Go on.", "Elaborate." — changes less than 0.6 % per hop while still drifting several percent over a word. Short rising asks ("Why?", "How so?") have fewer than 10 pairs and escaped the test, which is why the RC4 fixtures passed. Measured: eSpeak NG with SSML `prosody range="x-low"` reaches 61–73 % adjacent-hop steadiness; "Go on." 69 % → rejected by the rc.4 rule. |
| Turn detection (`TurnDetector.Finalize`) | speech that resumed within `ContinuationWindowMs` (2.5 s) of the previous turn's speech stop reopened that turn and merged the text | "How so? Tell me more." became one revised turn: the follow-up replaced the previous answer instead of getting its own, and no new history item appeared. |
| Text (`TurnDetector.IsNonQuestion`, `UtteranceGate.IsQuestionSentence`) | one-word turns needed "?" or an interrogative; imperatives were recognised by a short verb regex | "Continue." was dropped as a one-word non-question; "Go on.", "Keep going.", "Carry on." matched no rule. |

### rc.5 design

**Acoustic — pitch runs instead of hop-to-hop steadiness.** `PitchRunTracker` follows the pitch across analysis
windows and frames. A run continues while consecutive windows are periodic and the pitch moves ≤ 2.5 % per hop
(scaled by the hop gap); it ends at an unvoiced window or a jump. A closed run of ≥ 5 windows is *flat* when its whole
span stays within 2 %. Speech drifts over a run (declination, accents); a tone, a chord or a held sung/played note
stays inside 2 % for the whole run.

`HeldTone` (when runs were tracked) = ≥ 10 run windows AND (flat-run share ≥ 75 % OR (≥ 10 pitch pairs AND
adjacent-hop steadiness ≥ 65 % AND flat-run share ≥ 50 %)). Evidence recorded by rc.4 (no run tracking, e.g. in
saved reports) keeps the rc.4 rule, so old reports are re-validated exactly as before. The partial trust boundary
also counts the run still open at the current frame (`PitchRunTracker.OpenRun`), so a sustained tone cannot look
voice-like halfway through.

Measured on the fixture set (`tests/fixtures/audio/MANIFEST.md`, classification table):

| | flat-run share | adjacent-hop steadiness | rc.5 verdict |
|---|---|---|---|
| 44 speech fixtures (incl. 14 new flat-intonation follow-ups) | 0–62 % (max: "Walk me through the process." 62 % with 53 % steady) | up to 75 % ("How so?", 8 pairs, not judged); "Go on." 69 % with 0 % flat | all voice-like |
| in-range error buzz ×3 | 100 % | 76–82 % | held pitch |
| instrumental music ×4 | 74–100 % | 81–94 % | held pitch (band over a noisy call: 74 % flat + 81 % steady → combined rule) |
| chimes, critical-stop tone | 100 % | — | already rejected as tonal / no voiced sound |
| clicks, shutter, keyboard, noisy knock | no runs | — | no voiced sound / tonal |
| known look-alikes (quiet knock, sung melody ×2) | 0 % | 18–38 % | voice-like (unchanged from rc.4, documented residual) |

The non-speech outcome is identical to rc.4 for every fixture: the same 27 of 30 are not voice-like, and the same
three documented look-alikes remain.

**Turn — standalone follow-ups are new turns.** In `Finalize`, a reopened turn is not merged when the new text is a
standalone follow-up (`FollowUpIntent.IsStandaloneFollowUp`: ≤ 12 words, an elliptical ask or an imperative
elicitation that points back at the answer — "that/it/more/further/another…" — or ends on the ask itself) AND the
previous turn was a complete ask (terminal punctuation, no dangling conjunction/article). An unfinished question
("…and" + "go on about the reconciliation") still merges. Counter `follow_up_new_turn`.

**Text — turn intent by grammar.** `FollowUpIntent` recognises an elicitation verb in imperative position: after
discourse markers ("okay, so"), an optional vocative ("Shervin, …") and an optional politeness frame ("please",
"can/could/would you", "I'd like you to", "I want you to"), the sentence starts with *tell me/us, walk/talk/take/run
me through, go on, carry on, keep going, continue, go deeper, explain, elaborate, expand, describe, clarify, unpack,
illustrate, spell it out, break it down, give me/us…, give an/another example, share, show me, help me understand,
be specific, go into detail*; or it is a bare request noun ("Example.", "More detail."). The same verb elsewhere in a
statement ("…hard to explain in the app.", "I can explain that later.", "We will continue after the break.") is not an
ask. Turn intents now distinguished by the gate:

| Intent | Kind | Answered |
|---|---|---|
| explicit question | `Question` | yes |
| candidate-directed imperative follow-up ("Tell me more.") | `FollowUp` (new) | yes |
| full imperative question ("Describe a time you…") | `Question` | yes |
| interviewer-to-interviewer ("John, tell me more.", "Go on, Maria.") | `OtherDirected` | no |
| logistics / pleasantry ("Give me a second.", "Tell me when you're ready.", "Continue sharing your screen.") | `Logistics` / non-question | no |
| suspicious hallucination (vocabulary over too little speech) | `ContextHallucination` / `ImplausibleTranscript` | no |
| non-speech | `ShortAcousticEvent` / `NonSpeech` | no |
| partial, unstable | not shown until trusted (unchanged) | — |

Text and audio must agree: a follow-up transcript over a sound that is not a voice is still vetoed acoustically
before the text is looked at (gate order unchanged).

### Why beep suppression is still safe

1. The gate order is unchanged: artefact → not voice-like → implausible → weak+context → echo → sentence. A new text
   rule cannot make a non-voice answerable.
2. The acoustic change only narrows *one* veto (held tone) and only for evidence whose pitch was tracked; every
   other veto (tonal share, no voiced sound, plausibility) is untouched.
3. Every non-speech fixture heard as each of the 18 positive phrases (14 imperatives + "Why?", "Why.", "How so?",
   "Really?") creates 0 questions, 0 history items, 0 AI requests, 0 answers, no live text and no follow-up count
   (`Rc5FollowUpTests.SamePhrasesOverNonSpeech_CreateNothing`, 27 fixtures × 18 phrases). The rc.4 suites
   (`RecordedStyleAudioTests`, hallucinated prompt / short question / context-derived question) pass unchanged.
4. Residual risk (documented): a very short, monotone utterance whose pitch sits entirely in flat runs totalling ≥ 10
   windows would still be called a held tone; no fixture or probe (42 eSpeak probes, 3 voices × 3 ranges) does this.
   The sung-melody and quiet-knock look-alikes from rc.4 are unchanged.

## 2. Acceptance results

| Test | Result |
|---|---|
| Each positive ("Tell me more.", "Walk me through that.", "Explain further.", "Go on.", "Give me an example.", "Elaborate.", "Continue.", "Talk me through it.", "Can you expand on that?", "Give me more detail.", "Take me through your thinking.", "Walk me through the process.", "Explain how you did that.", "Give me another example.", "Why?", "Why.", "How so?", "Really?") is a candidate-directed ask | pass (text) |
| Each of the 14 new flat-intonation follow-up fixtures + both rc.4 "Tell me more." fixtures: exactly 1 question, 1 history item, 1 answer (model or prepared), 1 answer started, 0 suppressions | pass (real audio, call paths) |
| Same phrases over every non-look-alike non-speech fixture: 0 / 0 / 0 / 0, no live text | pass |
| "Go on." measured: rejected by the rc.4 rule, voice-like under rc.5 | pass (root-cause regression test) |
| Quick follow-up 1.5 s after a complete question → 2 questions, 2 history items, `follow_up_new_turn` = 1 | pass |
| Unfinished question + continuation → still 1 merged question | pass |
| Panel: "John, tell me more." → OtherDirected, nothing answered; "Shervin, tell me more." → answered | pass |
| "Why?" / "How so?" stay `Question`; "Describe a time you disagreed…" stays `Question` (not a follow-up) | pass |
| All rc.4 engine suites (non-speech, short turns, long context, panel, variants, soak) | pass unchanged |

## 3. Observability

New diagnostics counters (no transcript content): `follow_up_accepted`, `follow_up_new_turn` next to the rc.4
suppression counters. Log lines: "Turn accepted: candidate-directed follow-up (<support> acoustic support)" and
"Turn accepted: <kind> follow-up after a complete question - new turn, not merged". The acoustic reason for a
rejection now reads "held pitch NN %" (tracked) or "steady pitch NN %" (rc.4 evidence).

## 4. Latency

No model call added. Analyser cost with run tracking: 1.3–1.7 ms per 100 ms frame (budget 10 ms; same as without).
Turn finalization and answer latency are unchanged — see RC5_PERFORMANCE_AUDIT.

## 5. Not changed

Honesty rules (honest bridge, verified past tense, confident hypothetical, no invented personal facts), variant
behaviour, pause/resume, network recovery, report defence, speaker handling (name-based addressing only, no speaker
identification, no voiceprints) are untouched and covered by their rc.4 tests.
