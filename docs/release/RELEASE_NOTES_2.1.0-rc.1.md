# Interview Assistant 2.1.0-rc.1 — Release Candidate

This is a **release candidate** for testing, not a final release. It is **not code-signed** yet
(Windows SmartScreen will warn). Billing runs in the Paddle **sandbox**.

## Major fixes
- **Multi-part questions stay one question.** A natural pause (≈0.3–1 s, and longer continuations
  such as "…and which metrics would you track?") no longer splits a question into two answers.
- **Typed questions work while paused** and never use live minutes.
- **Interview vocabulary reaches transcription** (company, product and domain names), including
  after reconnects.
- **Clear errors** instead of "AI unavailable (Unknown)": session ended, limit reached, sign in
  again, device removed, service unavailable.
- **Language pickers** show readable names.

## Answer quality and honesty
- Default answers are exactly three short bullets that fit without scrolling.
- "Have you personally…?" questions are checked against your confirmed facts; without evidence the
  answer starts with an honest bridge. Unsupported "I built / I led" claims become "I'd…", and
  unconfirmed numbers stated as past results are removed.
- One user's profile data can never reach another user's prompts.
- Prepared answers are used instantly only when the match is confident and on-topic.

## Experience
- Live status in plain words (Not listening, Listening, Transcribing, Question detected,
  Preparing answer, Answer ready, Paused — live minutes are not being used).
- Short first-use introduction; Help & about page with a user guide and build information.
- Non-intrusive notifications for imports, saves, preparation, billing and devices.
- Home window: minimize / maximize / restore, remembers its position (never off-screen).
- Minutes show the billing-period allowance and today's limit separately.
- "Confirm all" asks first; facts can be filtered (to review / confirmed).

## Known limitations
- Unsigned build; automatic updates are disabled until releases are code-signed.
- Transcription quality depends on the meeting audio and network.
- Live AI latency depends on the network and the AI service.

## Test status
See the CI run linked to this build for the exact counts (unit, engine simulation, backend
integration, Windows self-test, installer and update test).
