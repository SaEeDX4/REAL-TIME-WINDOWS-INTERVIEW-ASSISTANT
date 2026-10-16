# 2.1.0-rc.4 — root causes

Every rc.3 manual-test finding addressed in rc.4, with the cause found in the code (not the symptom), the fix and
the test that now guards it. rc.3 (`dc67e80`) artifacts are unchanged; rc.4 is a new version.

## P0 — a Windows sound became an answered, recorded, reported question

| Symptom (rc.3 manual) | Root cause | rc.4 fix | Guard |
|---|---|---|---|
| A system sound produced "What is your experience with FinTech products?"; history 2/2 → 3/3, an AI request, report Q3, analytics changed | (1) The acoustic analyser accepted in-range alert tones and chord pads as voiced: it rejected narrow-band and spectrally frozen sound, but not a periodic sound whose pitch simply *holds*. (2) `UtteranceGate` accepted any well-formed question of ≥ 6 words regardless of acoustic support ("a recognizer would not invent a whole question") — a recognizer primed with the interview vocabulary does exactly that. (3) Nothing compared the number of words with the amount of speech heard. (4) No later stage re-checked | Pitch-pair steadiness (a held pitch is not a voice); `SpeechPlausibility` (syllables vs. voiced/active time); `ContextHallucination` (interview vocabulary over too little speech); override removed; report re-validation of recorded acoustic provenance | `RecordedStyleAudioTests` (60 CC0 fixtures × hallucinated prompt / short question / context-derived question), `Rc4ReportDefenseTests` |
| Other sounds flashed "Question detected" and partial text "Job interview" | Partials were displayed as they arrived; `SpeechDetected` was set on the VAD's speech-start event; `Finalizing` was set whenever the turn settled | Partial trust boundary (shown only with a judged, voice-like pitch track that supports the words); neutral "Hearing audio" until then; `Finalizing` only when the gate would answer | `AssertNothingShown` in the fixture suite; `Engine_SpeechFixture_ShowsLiveWordsBeforeTheTurnEnds` (no lag for real speech) |

### Found while fixing the P0 (not in the rc.3 report)

| Defect | Root cause | Fix | Guard |
|---|---|---|---|
| A falling "Why." from the interviewer was never answered | `TurnDetector.IsNonQuestion` required a literal "?" for one-word turns (a hard-coded list) | Interrogatives count with any punctuation; acknowledgements/fillers excluded | `ShortTurnTests` |
| Long real speech continued after held context could be dropped as "implausible" | A continued turn kept the merged text but only the newest segment's audio evidence | Continued turns carry both turns' evidence | `LongContextRegressionTests` (30 s / 60 s / 2 min) |
| A background statement "…hard to explain in the app." was answered | Imperative verbs (explain/describe/…) matched anywhere in a sentence | Only in imperative position | `LongContextRegressionTests`, existing gate tests |
| Partial-handler latency spikes during long monologues | The full live text (up to 8,000 characters) was rebuilt on every delta | Built only to the length the UI shows | `Rc2EngineTimingTests` (p99 2 ms) |
| The app did not exit on WM_CLOSE (Windows CI smoke test, rc.4 development build) | The Settings page's slider coerced its value during `InitializeComponent`, marking the page dirty at birth; closing waited on an unseen unsaved-changes question, and stored settings were never loaded into the page | Control construction runs with dirty guards suspended | Windows self-test: fresh Home has no unsaved edits; smoke test |

## P1

| Symptom | Root cause | Fix | Guard |
|---|---|---|---|
| The question also appeared as its own context line | Context sentences were never compared with the extracted ask (the interviewer repeating the question, or held context that already contained it) | `QuestionExtractor.RestatesAsk` drops restatements from in-turn and held context | `DuplicateContextTests` |
| Hobby "Hiking and photography" answered with invented traits/benefits | The answer structure for personal questions asked for "why it matters to you → link to the role" | Structure "confirmed facts, rephrased — nothing added"; explicit prohibitions; English bullets checked word-by-word against the facts before display, fallback to the confirmed words | `PersonalGroundingTests` |
| Missing motivation looked like an ordinary answer | Guidance (no model call) was rendered with the answer template | Distinct amber "Not in your profile yet" card with speaking structure and "Add to profile" | `UiPagesTests`, self-test |
| 44 → 45 confirmed facts after adding a hobby | Experience list excluded interview-profile answers; save toast, Start page and profile list included them | `FactStats.ConfirmedProfileAnswers`; every count names both parts | `FactCountSemanticsTests` |
| Password-reset copy differed for known/unknown e-mail | The client showed "We sent a reset link to …" only on success; "user not found" produced an error | One neutral sentence; "not found" responses treated as accepted; rate limits and network errors still reported | `CloudAuthTests` (known/unknown, three "not found" shapes) |
| Account page "That took too long" with no clue what failed | The devices request hit the 30 s client timeout and its error replaced the whole page status, although plan and quota had loaded | Independent sections (plan & usage, devices, billing) with their own loading/error/stale state and retry; correlation ids logged only | `UiFoundationTests`, `ErrorStateTests` |
| Persian Help/About labels in English | About rows built in code with English labels | Localized XAML labels; values unchanged | self-test Persian scan |
| Native MessageBox | Windows message boxes (OS-language buttons, no theme, no RTL) | In-app dialog system (`AppDialog`) | source test: no `MessageBox` in the App |
| Changing language asked to discard edits | A language change rebuilt the profile list; clearing the selection ran "switch profile" and its discard question | Language change re-renders text only; programmatic list updates are not user selections | `UiFoundationTests` |
| Settings in a separate window | Historical design | Settings is a Home page with sections; the Live gear deep-links to it | self-test |
| Placeholder text overlapped the caret | Placeholders were separate overlays positioned over the text box | Watermark drawn inside the control template's content area, hidden on focus or content | self-test, source test (no overlay placeholders) |
| Signed-out Live history showed "0 / 1" | Sign-out reset the position while the old engine still held history; the rebuilt engine never refreshed the label | `HistoryCounter`: hidden when nothing is shown; rebuild resets the position | `UiPagesTests` |
| Pre-interview "NOT READY" on warnings | Any warning failed the verdict; the audio check gave up before capture had started | Only failures block; warnings give "READY — WITH WARNINGS"; 3 s capture grace, passes on audio or a successful transcription test | `UiFoundationTests` |
| No feedback on network loss | Connection state changes were silent | Debounced "Connection lost — reconnecting…" / "Connection restored" toasts (3 s threshold, ≤ 1 pair per 10 s, no "restored" without "lost") | `UiPagesTests` (fake clock) |
| "résumé" in English UI | Mixed spelling | English UI and docs say "resume" | `UiFoundationTests` |
