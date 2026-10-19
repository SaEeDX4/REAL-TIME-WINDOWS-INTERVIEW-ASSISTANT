# Interview Assistant 2.1.0-rc.5 — Release Candidate

This is a **release candidate** for testing, not a final (Stable) release. It follows 2.1.0-rc.4, which stays
available unchanged. It is **not code-signed** yet, so Windows SmartScreen may warn during installation ("More info" →
"Run anyway"). Billing runs in the Paddle **sandbox**. Run the rc.5 Setup over rc.4: it upgrades in place, and your
profiles, interviews, reports and settings are kept.

## Follow-up questions are answered again
- "Tell me more.", "Go on.", "Walk me through that.", "Explain further.", "Give me an example.", "Elaborate.",
  "Continue." and "Can you expand on that?" are answered as their own question, also when said flatly. The earlier
  answer stays in the history.
- Notification sounds, beeps, screenshot sounds, music and knocks still never become questions.
- A follow-up addressed to another interviewer by name ("John, tell me more.") is not answered; your own name is.

## Know when you are ready
- **Pre-interview check** is now a page in Home: eight checks with a fix button each, Test audio / transcription / AI,
  and one result — Ready, Ready with warnings or Not ready — shown the same way on Get started, the Interviews page,
  the Home title bar and the Live Assistant.
- The live transcription test is optional and no longer keeps the result amber. A result says **Out of date** after
  30 minutes or when you change settings, the profile, the interview or the audio device.
- **Get started** is your readiness dashboard (Home opens there): four steps and one button for the next thing to do.

## Settings, account and your data
- Settings has categories with descriptions, Updates (moved from Account), Reset this section and Restore all
  defaults — your account, profiles, interviews and reports are never touched.
- Account: plan and minutes, devices, security, Privacy & data and a separate Danger zone; an account menu in the
  title bar with Switch account; dedicated Forgot password and Create account screens; a clear delete dialog.
- **Export my data** as a readable report (HTML) or raw data (JSON). Profiles can be exported as a readable profile,
  a **backup** (`.iaprofile`) to restore or move to another PC — with a preview before import — or raw data. Exports
  never contain passwords, sign-in tokens or keys. Backups are not encrypted: store them safely.

## Live Assistant
- A calmer toolbar without diagnostics; **?** opens Quick help for what you are doing now; More holds Compact view,
  Interview Focus, Home, the last report and Settings.
- Maximize/Restore with a readable answer column; **Interview Focus** places a compact window near your camera (it
  does not hide the window from screen sharing).

## Accessibility and languages
- Screen readers now hear the readiness cards, check rows, steps and audio meters, and status changes are announced.
  Toggle states, focus after menu actions and the readiness indicator no longer rely on colour or icons alone.
- Ten languages reviewed for consistent terms; Arabic dates use the Gregorian calendar; export dates read naturally;
  versions and e-mail addresses stay readable in Persian and Arabic.

## Known limitations
- Follow-up recognition without a question mark ("Tell me more") works in English; in other languages the interviewer
  is understood when asking with a question mark.
- Unsigned build: no automatic updates; install the next version with its Setup.exe.
- Billing is sandbox only; no real payments are taken.
