# Live Interview Memory & Adaptive Behaviour

`SessionMemory` (one per live session; serialisable; persisted encrypted with the session):
- per question: original wording, detected language, answer language, category, mode, **language-independent intent key** (bank id or category+fingerprint), entities, suggestions shown (bullets / coach keywords), stories used, metrics used, follow-up link, "references earlier answer", source, first-bullet latency
- counters: story use + recency, metric use, topic counts, company/product topics; interruption events
- bounded: last 12 records kept in detail, older ones compacted into a rolling summary; hard cap 400 records; prompt context < 3 KB regardless of interview length (test `MemoryStaysBoundedOverLongInterviewAndSerializes`)

Anti-repetition: evidence ranking subtracts a story penalty (use count + recency); a prepared answer whose stories were used in the last 2 answers is replaced by a fresh AI answer with an explicit "avoid these stories" instruction; "you already mentioned X" (10 languages) triggers "do NOT repeat — different story/angle" and marks quoted metrics as used. Follow-ups ("Why?", "And then?") are linked to the previous question and the prompt says so.

Language switches never reset memory: an English question and a German follow-up share the same memory (test `AnswersFollowTheInterviewerLanguageAndMemorySurvivesSwitches`).

## Interruption heuristic (honest)
Without the candidate microphone the app **cannot know** if the interviewer interrupted or simply asked quickly. `InterruptionTracker` estimates `confidence = 1 − elapsed/expected`, where expected = answer words ÷ 2.4 words/s + 1.5 s; repeated events within 5 min add +0.15. Below 0.35 → ignored. First event → **Concise** (3 bullets × 8–15 words); a second within 5 minutes → **Rapid** (2 bullets, direct answer first; cached answers trimmed to 2 bullets). Three calm answers step down one level. Manual reset (hotkey Ctrl+Alt+R) and lock supported. Events are labelled "likely interruption" (≥ 0.7) or "fast follow-up (uncertain)".

Candidate microphone: not implemented (OFF). Reports state that spoken answers are unknown.
