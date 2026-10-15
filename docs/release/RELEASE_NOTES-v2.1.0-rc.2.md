# Interview Assistant 2.1.0-rc.2 — Release Candidate

This is a **release candidate** for testing, not a final (Stable) release. It is **not code-signed** yet, so Windows
SmartScreen may warn during installation. Billing runs in the Paddle **sandbox**.

## More reliable during the interview
- **No more fake questions from computer sounds.** Notification chimes, error beeps, screenshot sounds and short
  music fragments are recognised as non-speech and ignored — including the case where the transcription service
  "hears" its own vocabulary list. Real short questions such as "Why?" still work.
- **Long questions stay smooth.** Several minutes of interviewer speech no longer slow the window down; the live
  transcript updates calmly.
- **The real question, not the preamble.** When the interviewer gives context first, the assistant answers the
  actual question at the end and shows the context in one short line. Context said before a question is kept and
  used for it.
- **Panel interviews.** Remarks addressed to another interviewer by name ("John, what do you think?") are not
  answered. The app does not identify speakers and never stores voice data.
- **Multi-part questions** with a natural pause stay one question with one answer.

## Answers you can trust
- Confirmed experience stays in the past tense; anything your profile does not confirm is phrased as "I'd…".
  Invented numbers are never shown as your results.
- Personal questions (hobbies, motivation, team preferences) use only what you wrote in your interview profile — if
  you have not filled it in, you get an instant, honest prompt instead of an invented answer.
- Answers stay three short bullets; repeated bullets are removed. Answer speed is unchanged.

## Profiles
- New **Interview profile** tab: hobbies, motivation, goals, working style, boundaries and answer preferences — all
  optional and in your own words.
- Add, edit and delete facts; every fact shows where it came from; one consistent "to review" count.
- Unsaved changes are no longer lost when switching profiles or closing the window.

## History, reports and sessions
- Shorter / Technical / Example / Full answer rework **the question you are looking at** — its place in the history
  stays the same.
- Pause and resume keep one continuous session.
- Stop saves the report and it appears in Reports immediately; closing the app during an interview also keeps it.
- Reports are clean to copy as text, show the candidate name once and say how many non-interview sounds and remarks
  were not counted.
- Reports are stored encrypted for your account only; the copy opened in your browser stays in your account's
  private folder (no longer in Documents).

## Languages and accessibility
- The whole app is translated into Spanish, French, German, Portuguese, Italian, Arabic, Persian, Chinese and Hindi;
  Persian and Arabic are fully right-to-left. The UI language changes instantly and is separate from the interview
  and answer languages.
- Better contrast, visible keyboard focus, keyboard-operable controls, screen-reader names and announced
  notifications. Settings and the Pre-interview check fit smaller and scaled screens.

## Account and connection
- Clear notifications for connection lost / restored, audio device changes, saves and reports; no more "Unknown"
  errors.
- Account shows minutes remaining this billing period and available today, your devices with their full version,
  and your update preference separately from this build's channel.
- Help & about shows version, channel, build, installed/portable and signing state.
- Sign out, sign in and switching accounts never show another account's data. Exporting data and deleting the
  account ask for your password again, and the service checks it too. After deleting your account you can also
  remove its data from this PC.

## Known limitations
- Unsigned build: SmartScreen warnings, and automatic updates are disabled until releases are code-signed.
- The sound filter was validated with simulated audio; please report any real sound that still creates a question.
- Speaker identification is not available: a remark between interviewers without a name may still get a suggestion.
- Translations have not yet had a professional native-speaker review.
- To remove your sign-in e-mail after deleting your account, contact support.
