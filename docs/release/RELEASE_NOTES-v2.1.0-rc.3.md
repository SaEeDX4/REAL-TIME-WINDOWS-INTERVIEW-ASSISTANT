# Interview Assistant 2.1.0-rc.3 — Release Candidate

This is a **release candidate** for testing, not a final (Stable) release. It replaces 2.1.0-rc.2 (build `8d82b20`),
which stays available unchanged. It is **not code-signed** yet, so Windows SmartScreen may warn during installation.
Billing runs in the Paddle **sandbox**.

## Live transcription goes through the service
- Interview audio is now streamed through the Interview Assistant service instead of directly to the transcription
  provider. Your PC never holds a provider key or credential.
- Live transcription stops exactly when your live minutes stop: on Pause, Stop, when the plan's minutes or today's
  limit run out, when a device is removed from the account, or when the connection to the service is lost for longer
  than the billing timeout.
- **Update required:** versions before 2.1.0-rc.3 can no longer start live listening against this service version
  and are asked to update. Typed questions, preparation and reports are unaffected.
- Pause and Resume reconnect transcription reliably (an earlier build could stay silent after Resume), and a live
  session that is renewed automatically after a short network loss reconnects by itself.

## Deleting your account deletes your sign-in too
- Delete account now removes your account data on the service **and your sign-in** (e-mail address, password and
  linked Google/Microsoft/GitHub/Apple sign-in). If the sign-in service is briefly unreachable, the removal is
  finished automatically within minutes — no support request needed.
- Invoices stay with Paddle, our payment provider, as tax law requires.

## Refunds and chargebacks
- If the payment for the current period is fully refunded or charged back, Pro access ends and the Account page says
  why ("Pro ended — payment refunded"). A new payment or a reversed chargeback restores it. Partial refunds and
  credits never change your access.

## Other improvements
- Better recognition of notification chimes, error tones, clicks and shutter sounds: the sound filter was retuned
  using recording-style test sounds that pass through a simulated call (noise, band limit, Opus codec, compression).
- Deleting an interview or profile also deletes the interview's vocabulary (names and terms used to improve
  recognition) from the service.
- Log files older than 14 days are deleted automatically.
- Full license texts for every bundled component now ship in the `licenses` folder next to
  `THIRD_PARTY_NOTICES.md`.

## Known limitations
- Unsigned build: SmartScreen warnings, and automatic updates are disabled until releases are code-signed.
- Some sounds still look like speech to the sound filter — low buzzing alert tones, two-note chimes, knocks and music
  (especially singing). They only become a question if the transcription service turns them into words that read as
  a question; please report any real sound that still creates one.
- Transcription now takes one extra network hop through the service; the added delay depends on where the service is
  hosted and was not yet measured on a consumer connection.
- Speaker identification is not available: a remark between interviewers without a name may still get a suggestion.
- Translations have not yet had a professional native-speaker review.
