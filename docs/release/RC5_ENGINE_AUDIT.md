# 2.1.0-rc.5 — engine audit

Scope: capture → acoustic analysis → partial trust boundary → turn detection → utterance gate → question extraction →
answer / history / SessionMemory / report. Build under audit: 2.1.0-rc.5 (this branch). Baseline: 2.1.0-rc.4
(`a0bc946`).

This document was revised after an independent engine audit of the rc.5 follow-up and non-speech changes. That audit
found one blocker (short buzzes and piano notes answered again, a non-speech regression against rc.4), several
false positives and wrong turn splits, and a number of claims in the first version of this document that did not hold.
Section 2 lists every finding, what changed, and the test that proves it; section 4 lists what is still not solved.

## 1. The P0: a real "Tell me more." was dropped (rc.4 regression)

### Root cause

Three independent layers each dropped part of the class "candidate-directed imperative follow-up". None of them was
a missing phrase; the fix therefore changes the rules, not a list.

| Layer | rc.4 behaviour | Why it dropped real follow-ups |
|---|---|---|
| Acoustic voice-likeness (`SpeechEvidence.VoiceLike`) | once ≥ 10 pitch pairs were measured, a pitch that changed < 0.6 % between adjacent 20 ms hops in ≥ 65 % of the pairs was a **held tone** → `ShortAcousticEvent` | Level, falling declarative intonation — how most people say "Tell me more.", "Go on.", "Elaborate." — changes less than 0.6 % per hop while still drifting several percent over a word. Measured: eSpeak NG with SSML `prosody range="x-low"` reaches 61–73 % adjacent-hop steadiness; "Go on." 69 % → rejected by the rc.4 rule. |
| Turn detection (`TurnDetector.Finalize`) | speech that resumed within `ContinuationWindowMs` (2.5 s) of the previous turn's speech stop reopened that turn and merged the text | A follow-up asked after the candidate had started answering replaced the previous answer instead of getting its own. |
| Text (`TurnDetector.IsNonQuestion`, `UtteranceGate.IsQuestionSentence`) | one-word turns needed "?" or an interrogative; imperatives were recognised by a short verb regex | "Continue." was dropped as a one-word non-question; "Go on.", "Keep going.", "Carry on." matched no rule. |

### Design (after the audit fixes)

**Acoustic — pitch runs, with the rc.4 rule as the fallback.** `PitchRunTracker` follows the pitch across analysis
windows and frames. A run continues while consecutive windows are periodic and the pitch moves ≤ 2.5 % per hop (scaled
by the hop gap); it ends at an unvoiced window or a jump. A closed run of ≥ 5 windows is *flat* when its whole span
stays within 2 %.

`HeldTone` =
- when runs were tracked AND ≥ 10 hops are in runs: flat-run share ≥ 75 % OR (≥ 10 pitch pairs AND adjacent-hop
  steadiness ≥ 65 % AND flat-run share ≥ 50 %);
- otherwise (fewer than 10 run hops, or evidence recorded before rc.5): the rc.4 rule, ≥ 10 pitch pairs AND ≥ 65 %
  steady.

The first version of rc.5 dropped the rc.4 rule entirely once runs were tracked, so a 300–400 ms buzz or piano note
(5–7 run hops, 100 % flat, 10–13 pairs, 70–85 % steady) became voice-like and was answered (audit finding 1). The
fallback restores rc.4 exactly where the runs cannot judge. The tracker also counts **voiced stretches**: hops in
continuous periodic stretches of ≥ 8 windows (160 ms), whatever the pitch does. A two-syllable word has one even when
a low, creaky voice's pitch estimate jumps between harmonics and forms no run; the quiet knock has none.

Measured on the fixture set (`RecordedStyleAudioTests.Evidence`, full-length clips):

| | run hops | flat-run share | adjacent-hop steadiness | verdict |
|---|---|---|---|---|
| speech fixtures with ≥ 10 run hops (30 of 44) | 10–62 | 0–62 % (max "Walk me through the process." 62 %) | 13–69 % | voice-like (run rule) |
| speech fixtures with < 10 run hops (14 of 44: "Why?" ×4, "How so?" ×2, "Really?" ×2, "And then?" callq, "What happened?" ×2, "Metrics?" ×2, "Continue.") | 0–8 | up to 100 % ("How so?" ×2, "And then?" callq, "Metrics?" ×2) | 0–75 %, but ≥ 10 pairs only for "How so?" callq (50 %), "Really?" (0–15 %) and "Continue." (33 %) | voice-like (rc.4 fallback) |
| in-range error buzz ×3, instrumental music ×4 | 12–55 | 74–100 % | 76–94 % | held pitch |
| first 300 / 360 / 400 ms of the buzzes and the piano | 5–7 | 100 % | 70–85 % | steady pitch (fallback) |
| buzzes and band pulsing 70 ms on / 30 ms off | 5–18 | 0–100 % | 65–92 % | held or steady pitch |
| chimes, critical-stop tone, clicks, shutter, noisy knock | — | — | — | tonal / no voiced sound (unchanged) |
| known look-alikes: quiet knock, sung melody ×2 | 0 / 42–49 | 0 % | 18–38 % | voice-like (see the very-short rule below) |

The five 100 %-flat short speech fixtures pass only because the run rule does not judge them and their steadiness or
pair count stays under the rc.4 thresholds; "How so?" (callq) is 15 points under the 65 % threshold with exactly 10
pairs. This is the same margin rc.4 had for these fixtures.

**Turn — standalone follow-ups split only after a candidate-reply allowance.** In `Finalize`, a reopened turn is
*not* merged when the new text is a standalone follow-up (`FollowUpIntent.IsStandaloneFollowUp`: ≤ 12 words, an
elliptical ask or an imperative with no topic of its own), the previous turn was a complete ask, AND the new speech
started at least `FollowUpReplyAllowanceMs` (1.8 s from the `speech_stopped` event, about 2 s of real silence) after
the previous speech stopped. Sooner, it is part of one multi-part question and merges as in rc.4 ("What is your
biggest weakness? … Explain."). Elliptical asks split the same way whatever their punctuation: "Why?", "Why.",
"How so?", "What happened?", "Really?". Whether the previous answer was already on screen is not used: a prepared
answer appears at once, long before the candidate can have said anything. Counter `follow_up_new_turn`.

**Text — turn intent by grammar.** `FollowUpIntent` recognises an elicitation verb in imperative position: after
discourse markers ("okay, so"), an optional vocative ("Shervin, …") and an optional politeness frame ("please",
"can/could/would you", "I'd like you to", "I want you to"), the sentence starts with *tell me/us, walk/talk/take/run
me through, go deeper, explain, elaborate, expand, describe, clarify, unpack, illustrate, spell it out, break it down,
give me/us…, give an/another example, help me understand, be specific, go into detail*; or *show me/us* followed by a
how/what clause ("Show me how you would do it."); or a bare request noun ("Example.", "More detail.", not a lone
"More."). Complement constraints:

- the movement verbs *go on, continue, carry on, keep going* are an ask only with nothing after them, "please"/"then",
  an addressee, a separate clause after a comma ("Go on, I'm listening."), or a speaking complement ("…with your
  answer", "…about the migration", "…where you left off"). "Go on mute.", "Continue to the next slide.", "Okay, go on
  to the next question.", "Keep going with the exercise.", "Carry on with your code.", "Continue coding." are not;
- an ask deferred to later or to another channel ("Clarify that in the follow-up email.") is not an ask for now;
- "share" and the "feel free to" frame are no longer part of this grammar (rc.4's request rule still treats "Share
  your thoughts." as a question); showing a screen, "give us a second", "tell me when you're done" and "tell me when
  you can see my screen" are logistics.

A follow-up is standalone when it points back at the answer ("that", "it", "more", "further", "another", "your
thinking", "the process"), ends on the ask itself ("Elaborate.", "Give me an example."), or is a bare movement ask
("Go on."). The movement verbs themselves are no longer treated as anaphoric, so "Go on about the reconciliation." is
a question with its own topic, not a standalone follow-up.

`QuestionClassifier.IsFollowUp` now agrees with this intent, so these asks get the "This is a FOLLOW-UP" prompt line
and `FollowUpOf`. A follow-up with no topic of its own keeps a matched prepared answer only when that answer is itself
a FOLLOW_UP-category entry; before, "Walk me through the process." was grounded on "Tell me about yourself.".

**Very short imperative follow-ups need clear speech.** A one- or two-word imperative ("Continue.", "Go on.",
"Elaborate.", "Okay, keep going.") is what a recognizer can guess for almost any short sound. When the audio was
analysed (complete, pitch-tracked evidence), such a turn needs Strong acoustic support, a voiced stretch ≥ 160 ms or a
judged pitch track (≥ 10 run hops), and at most 350 ms of voicing per syllable. Its live text waits for the final
segment. The quiet knock fails the stretch test (0 stretch hops, 0 run hops); the 2 s sung melody fails the voicing
bound (1,520–1,640 ms voiced for at most 4 syllables). The real one- and two-word fixtures pass: "Go on." 22 stretch
hops / 500 ms voiced, "Continue." 12 / 340 ms, "Elaborate." 10 / 200 ms. Forms asked with "?" ("Elaborate?") keep their
rc.4 behaviour and are not subject to this rule.

**Panel safety.** A follow-up with no topic of its own takes the addressee of the turn it is in: "Over to you, Maria.
Go on." and "John, do you want to take this? Go on." are `OtherDirected` unless the candidate is named for it ("Over to
you, Maria. Shervin, go on."). "I want to hear from Sarah." hands over like "over to you". A capitalised name right
after a complete follow-up is a vocative even without the comma recognizers often drop ("Go on John.", "Continue
Sarah.", "Tell me more Maria."); after a transitive verb it stays the topic ("Explain Snowflake."). The candidate's own
name is answered either way. No names are hard-coded: "other" means a capitalised name that is not the candidate's,
not a known term from the knowledge base and not a common sentence opener.

**Duplicates.** A follow-up's duplicate entry is scoped by the last question whose answer had completed when it was
asked. "Tell me more." asked again after the first "Tell me more." was answered is a new ask; the same transcript
echoed before anything new was answered is still suppressed. Other questions keep the unscoped 30 s rule.

Turn intents distinguished by the gate:

| Intent | Kind | Answered |
|---|---|---|
| explicit question | `Question` | yes |
| candidate-directed imperative follow-up ("Tell me more.", "Go on.") | `FollowUp` | yes |
| full imperative question ("Describe a time you…") | `Question` | yes |
| interviewer-to-interviewer ("John, tell me more.", "Go on, Maria.", "Go on John.", "Over to you, Maria. Go on.") | `OtherDirected` | no |
| logistics / pleasantry ("Give me a second.", "Show me your screen.", "Continue sharing your screen.") | `Logistics` / non-question | no |
| instruction that is not an ask to speak ("Go on mute.", "Continue to the next slide.") | `Context` | no |
| suspicious hallucination (vocabulary over too little speech) | `ContextHallucination` / `ImplausibleTranscript` | no |
| non-speech, incl. a one- or two-word follow-up over sound that is not clearly speech | `ShortAcousticEvent` / `NonSpeech` | no |
| partial, unstable | not shown until trusted | — |

### Why beep suppression is safe again

1. The gate order is unchanged: artefact → not voice-like → implausible → weak+context → echo → (new) very-short
   follow-up check → sentence. A text rule cannot make a non-voice answerable.
2. With the fallback, a sound with fewer than 10 run hops is judged exactly as in rc.4. The run rule can accept what
   rc.4 rejected only for a sound with ≥ 10 run hops whose runs mostly drift by more than 2 % (under 75 % flat, and under 50 % when the pitch is hop-to-hop steady) —
   the "Tell me more." / "Go on." fix itself. On the fixture set that is only speech; a slowly gliding, steady tone
   (a siren sweep) would be the kind of sound to watch for, and no fixture has one.
3. Tests: every non-look-alike non-speech fixture heard as each of the 18 positive phrases creates 0 questions, 0
   history items, 0 AI requests, 0 answers, no live text and no follow-up count
   (`Rc5FollowUpTests.SamePhrasesOverNonSpeech_CreateNothing`, 27 fixtures × 18 phrases); the same for the
   truncated (300–400 ms) buzz and piano clips and the pulsing 70/30 tones
   (`Rc5FollowUpTests.SamePhrasesOverShortNonSpeech_CreateNothing`, 12 clips × 18 phrases,
   `ShortAndPulsingHeldTones_AreNotVoiceLike`); and the three look-alikes heard as 15 one- and two-word follow-ups
   (`Rc5EngineAuditTests.LookalikeFixtures_OneWordFollowUps_CreateNothing`).

## 2. Independent audit findings and fixes

| # | Finding | Status | Proof |
|---|---|---|---|
| 1 | BLOCKER: 300–400 ms buzz / piano clips voice-like and answered (rc.4 rejected them); pulsing band 70/30 too | fixed: rc.4 rule as fallback below 10 run hops | `Rc5FollowUpTests.ShortAndPulsingHeldTones_AreNotVoiceLike`, `SamePhrasesOverShortNonSpeech_CreateNothing` |
| 2 | one-word follow-ups answered over the knock and the sung melody | fixed for one- and two-word imperatives without "?": "More" dropped from the bare-request rule, Strong support + voiced stretch or judged pitch + voicing bound required | `Rc5EngineAuditTests.LookalikeFixtures_OneWordFollowUps_CreateNothing`, `KnockAndSungMelody_FailTheVeryShortRule`, `RealOneWordFollowUps_AreClearlySpeech`, `Rc5FollowUpTests.FollowUpFixture_CreatesExactlyOneTurnHistoryItemAndRequest` |
| 3 | a repeated real follow-up within 30 s suppressed as a duplicate | fixed: follow-ups scoped by the last answered question | `Rc5EngineAuditTests.RepeatedFollowUp_AfterItsAnswer_IsAnsweredAgain`, `DuplicateGuard_ScopesFollowUpsByTheAnsweredQuestion` |
| 4 | "Over to you, Maria. Go on." answered | fixed | `Rc5EngineAuditTests.HandOff_ThenBareFollowUp_IsOtherDirected`, `HandOff_ThenBareFollowUp_CreatesNothing` |
| 5 | statements, logistics and coding instructions answered ("Go on mute please.", "Continue to the next slide.", "Show me your screen.", "Feel free to share any questions…") | fixed: complement constraints, verbs out of the anaphor rule, screen showing in logistics, "share" / "feel free to" out of the grammar | `Rc5EngineAuditTests.Instruction_IsNotAnswered`, `Instruction_CreatesNothing`, `AskToKeepTalking_IsAFollowUp` |
| 6 | "Go on John.", "Continue Sarah.", "Tell me more Maria." answered | fixed (also the rc.4 cases "Tell me more Maria." and "I want to hear from Sarah. Sarah, go on.") | `Rc5EngineAuditTests.TrailingOtherNameWithoutComma_IsOtherDirected`, `HandOff_ThenBareFollowUp_IsOtherDirected` |
| 7 | the follow-up split broke quick multi-part questions | fixed: split only after the 1.8 s reply allowance | `Rc5EngineAuditTests.QuickMultiPartAsk_StillMerges`, `FollowUpAfterTheReplyAllowance_IsItsOwnTurn` |
| 8 | "Why?" / "How so?" / "What happened?" / "Really?" did not split like "Why." | fixed: same rule, same timing | `Rc5EngineAuditTests.EllipticalAsk_IsStandaloneHoweverPunctuated`, `FollowUpAfterTheReplyAllowance_IsItsOwnTurn` |
| 9 | classifier and follow-up intent disagreed; follow-ups grounded on unrelated prepared answers | fixed | `Rc5EngineAuditTests.FollowUpIntent_AndClassifier_Agree`, `FollowUp_GetsTheFollowUpPrompt_AndNoUnrelatedPreparedAnswer` |
| 10 | follow-ups in other languages not recognised | documented (section 4) | — |
| 11 | runs credited to the frame where they close; final evidence ignored the open run | partially fixed: the final segment evidence now includes the run (and stretch) still open, as the partial check already did; the crediting itself is documented (section 4) | covered by the fixture suites |
| 12 | `lock(this)` on a public type; wrong "capture thread only" comment | fixed: private sync object, one lock per frame, comment corrected | code review |
| 13 | live text later for some short asks | documented (section 4) | `Engine_SpeechFixture_ShowsLiveWordsBeforeTheTurnEnds` still passes |

### Before / after on the auditor's repro cases

Simulator and fixture runs on this branch; "before" is the audited rc.5 build (`46ca360`), and the auditor's rc.4
numbers are given where they differ.

| Case | rc.4 | rc.5 before | rc.5 after |
|---|---|---|---|
| first 300 / 360 / 400 ms of the buzz ×3, first 360 / 400 ms of the piano, × 18 positive phrases | 0 answered | 12 of 18 answered, live text shown | 0 answered, no live text |
| buzz ×3 and band, pulsing 70/30 | band and two buzzes not voice-like, buzz (noisy call) voice-like | the three buzzes not voice-like, band voice-like | all four not voice-like |
| knock / sung ×2 × "Continue.", "More.", "Expand.", "Clarify.", "Details.", "Illustrate.", "Unpack.", "Go on.", "Keep going.", "Be specific.", "Say more." | 0 answered | 11 of 11 answered on each | 0 answered |
| "Tell me more." / "Give me an example." repeated 10 s or 20 s after its answer | 2nd suppressed | 2nd suppressed | answered (3 history items) |
| "Over to you, Maria. Go on." / "John, do you want to take this? Go on." | OtherDirected | answered | OtherDirected |
| "Go on mute please.", "Continue to the next slide.", "Keep going with the exercise.", "Carry on with your code.", "Show me your screen.", "Feel free to share any questions…" | not answered | answered | not answered |
| "Go on John.", "Continue Sarah.", "Go on Maria" | not answered | answered | OtherDirected |
| "Tell me more Maria.", "I want to hear from Sarah. Sarah, go on." | answered | answered | OtherDirected |
| "Tell me about a time you led a team." +1.5 s "Walk me through it."; "How did you scale the ledger?" +1.2 s "Give me an example."; "Tell me about the ledger migration." +1.5 s "Go deeper on the reconciliation part." | merged | split (2 cards, 2 requests) | merged |
| "What was your biggest failure at Teroxx?" +1.2 s "Why?" / "Why."; "Describe a conflict…" +1.2 s "What happened?" | merged | "?" merged, "." split | merged |
| the same with 2.2 s of silence | merged | "?" merged, "." split | split, both punctuations |
| "Walk me through the process.", "Take me through your thinking." after a question | Question | Question, no FOLLOW-UP line, grounded on Q001 "Tell me about yourself." | FollowUp, FOLLOW-UP prompt line, no unrelated grounding |

One simulator caveat: the simulator's synthetic voice (an impulse train with 60 % pitch jumps and 140 ms voiced
syllables) fails the very-short rule, so "Explain." or "Go on." spoken by it is dropped; the recorded-style speech
fixtures pass. Engine tests for one- and two-word asks therefore use the speech fixtures.

## 3. Acceptance results

| Test | Result |
|---|---|
| Each positive ("Tell me more.", "Walk me through that.", "Explain further.", "Go on.", "Give me an example.", "Elaborate.", "Continue.", "Talk me through it.", "Can you expand on that?", "Give me more detail.", "Take me through your thinking.", "Walk me through the process.", "Explain how you did that.", "Give me another example.", "Why?", "Why.", "How so?", "Really?") is a candidate-directed ask | pass (text); the 14 imperatives are `FollowUp`, the 4 elliptical asks `Question` |
| Each of the 14 flat-intonation follow-up fixtures + both rc.4 "Tell me more." fixtures: exactly 1 question, 1 history item, 1 answer, 0 suppressions | pass (recorded-style audio) |
| Same phrases over every non-look-alike non-speech fixture, and over the truncated and pulsing clips: nothing created, no live text | pass |
| One- and two-word follow-ups over the three look-alikes: nothing created | pass |
| Follow-up after the reply allowance → own turn; within it → one merged question | pass |
| Repeated follow-up after its answer → answered again | pass |
| Panel: other names (with or without comma, after a hand-off) → not answered; the candidate's name → answered | pass |
| All rc.4 engine suites (non-speech, short turns, long context, panel, variants, soak) | pass |

## 4. Residual risks and known limitations

- **Look-alikes with longer text.** The quiet knock and the sung melody are still voice-like. A transcript of three or
  more words over them ("Tell me more.") is judged as in rc.4 by syllable plausibility only, and a one-word ask with
  "?" ("Continue?") keeps rc.4 behaviour. A short sung fragment (under ~1 s) heard as a one-word follow-up can pass
  the voicing bound; the sung fixture fails it by 120 ms ("Elaborate.": 1,520 ms voiced against 1,400 allowed).
- **Very short asks from unusual voices.** A one- or two-word imperative needs a 160 ms voiced stretch or ≥ 10 run
  hops. "Continue." passes with 12 stretch hops (threshold 8) and "Elaborate." with 10 run hops (threshold 10). A very
  fast or very creaky "Unpack." or "Be specific." could fail and is then not answered; recognizer-dropped fillers
  ("hmm, go on" transcribed "Go on.") add voicing towards the 350 ms per syllable bound.
- **Split timing.** The reply allowance is fixed (1.8 s from `speech_stopped`). A follow-up asked 1.5 s after a
  complete question merges into it, even if the interviewer meant it as a separate ask; one asked 2–2.7 s after it is
  its own turn.
- **Duplicate echoes after completion.** A re-delivered follow-up transcript that arrives after its own answer
  completed is answered a second time (scoped by the answered question).
- **Panel names.** "Maria? Go on." (a name-only sentence) is still answered: without a name list, "Maria?" cannot be
  told from a one-word topic ask such as "Metrics?". A lower-case comma-less candidate name ("go on shervin") is not
  answered (rc.4 behaviour).
- **Other languages (finding 10).** The imperative grammar is English only. "Erzählen Sie mehr.", "Cuéntame más.",
  "Continúe.", "ادامه بده.", "Racontez-moi davantage." are `Context` and not answered; "Continuez.", "Weiter.",
  "Expliquez." are dropped as one-word non-questions. Only forms asked with a question mark ("Warum?", "¿Por qué?",
  "چرا؟") are answered. Per-language imperative frames are not built in rc.5.
- **Run crediting (finding 11).** A pitch run is credited to the frame where it closes: a run that started before a
  segment's evidence window counts in full in that window, and a run is assumed to continue across a dropped capture
  frame. The final segment evidence now includes the open run, so the live and final decisions see the same run.
- **Live text for short asks (finding 13).** Live words are shown only once ≥ 10 hops are in pitch runs, and one- and
  two-word imperatives only when their segment is final. "Really?", "How so?" and "Continue." were shown mid-turn by
  rc.4 at 400–700 ms and now appear when the turn ends. The independent audit measured live text opening earlier than
  in rc.4 on average (16 fixtures earlier, mean −32 ms), so "no visible lag" holds on average, not for every short
  phrase.
- **Extra answers.** No model call is added per question, but each split follow-up is a separate answer with its own
  model request, as intended for a new ask.

## 5. Observability

Diagnostics counters (no transcript content): `follow_up_accepted`, `follow_up_new_turn` next to the rc.4 suppression
counters. Log lines carry the kind, the acoustic support and sizes only: "Turn accepted: candidate-directed follow-up
(<support> acoustic support)", "Turn accepted: <kind> follow-up after a complete question - new turn, not merged",
and for the very-short rule "one- or two-word follow-up over sound that is not clearly speech (<reason>)". The acoustic
reason for a rejection reads "held pitch NN %" (run rule) or "steady pitch NN %" (rc.4 rule, including the fallback).
`HeardEvidence` keeps the voiced-stretch count; records without it (-1) skip the stretch check on report re-validation.

## 6. Latency

No model call is added per question. Analyser cost with run and stretch tracking: 2.5–2.6 ms per 100 ms frame on this
container (2.0–2.3 ms without tracking; budget 10 ms). Turn finalization and answer latency are unchanged; the soak
numbers are virtual-clock results that vary by about 20 ms between runs — see RC5_PERFORMANCE_AUDIT.

## 7. Not changed

Honesty rules (honest bridge, verified past tense, confident hypothetical, no invented personal facts), variant
behaviour, pause/resume, network recovery, report defence, speaker handling (name-based addressing only, no speaker
identification, no voiceprints) are untouched and covered by their rc.4 tests.
