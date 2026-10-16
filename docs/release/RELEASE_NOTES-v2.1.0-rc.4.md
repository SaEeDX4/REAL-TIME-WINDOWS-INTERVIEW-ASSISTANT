# Interview Assistant 2.1.0-rc.4 — Release Candidate

This is a **release candidate** for testing, not a final (Stable) release. It follows 2.1.0-rc.3, which stays
available unchanged. It is **not code-signed** yet, so Windows SmartScreen may warn during installation. Billing runs
in the Paddle **sandbox**. Your rc.3 installation upgrades in place; profiles, interviews, reports and settings are
kept.

## System sounds no longer become questions
- Notification chimes, error tones, screenshot sounds, clicks, knocks and music are recognised as "not a voice" — also
  when the transcription service invents a well-formed interview question from them. Such a sound now creates no
  question, no AI answer, no history entry and nothing in the report.
- While audio is being checked, the live window says **"Hearing audio"** instead of showing words or "Question
  detected"; real speech still shows its words while the interviewer is talking.
- Short real follow-ups keep working: "Why?", "Why.", "How so?", "Really?", "And then?", "Tell me more.", "What
  happened?", "Metrics?", "Anything else?".
- Reports list any question whose audio looked doubtful under **"Possibly not interviewer speech (not counted)"**
  instead of counting it.

## Honest answers
- Answers to personal questions use only what you confirmed in your interview profile — no invented traits,
  feelings or benefits. If something is missing, an amber **"Not in your profile yet"** card gives you a speaking
  structure and an "Add to profile" shortcut.
- A question repeated by the interviewer no longer also appears as its own context line.
- Long background before a question (up to minutes), multi-part questions, rephrasing and interruptions are handled
  more reliably.

## A more consistent app
- **Settings** is now a page in the main window (Audio, Answers, Listening, Window, Hotkeys, Privacy); the gear in the
  live window opens the right section.
- New in-app dialogs (dark theme, keyboard friendly, right-to-left aware, localized buttons) replace Windows message
  boxes.
- One style for every text box, password box and drop-down; placeholders never overlap the cursor.
- **Profiles**: a clear two-part switch (Experience & facts / Interview profile), "Import resume…" as the main action,
  Delete set apart and confirmed.
- **Reports**: list and preview side by side, with Open, Copy as text, Save as HTML and Delete, keyboard support and
  an empty state.
- Changing the app language keeps unsaved edits without asking; you are asked only before something would really be
  lost.
- Profile counts say what they count ("44 confirmed experience facts + 1 interview-profile answers").
- The Account page loads plan & usage, devices and billing separately; one slow part no longer turns the whole page
  into an error, and each part has its own "Try again".
- "Connection lost — reconnecting…" / "Connection restored" notices during an interview (only for real outages).
- The pre-interview check says **"READY — WITH WARNINGS"** when only optional improvements are missing.
- Password reset shows the same message whether or not an account exists for the address.
- Persian Help/About labels and all new text are translated in all 10 languages; the English app says "resume".

## Known limitations
- Unsigned build: SmartScreen warnings; automatic updates stay disabled until releases are code-signed.
- Singing (and, for very short phrases, a quiet knock) can still sound like a voice to the filter.
- Speaker identification is not available: a remark between interviewers without a name may still get a suggestion.
- The personal-answer word check runs for English answers; other languages rely on the model instruction.
- Translations have not yet had a professional native-speaker review.
