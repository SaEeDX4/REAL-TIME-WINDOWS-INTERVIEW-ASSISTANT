# Interview Assistant

An interview **preparation and practice coach** for Windows, with a live assist mode for interviews where assistance is permitted. It listens to the **interviewer's audio** from your headset (your meeting app stays completely normal), detects when a question is finished, and shows **3 short, speakable bullets** — or, in Coach Mode, **3 keywords and a one-line structure** — built only from facts you confirmed in your own profile.

> Use it only where you are allowed to. The app is a normal, visible window. It does not hide itself, evade screen capture, record your microphone, or store audio.

```
Interviewer speaks
 → WASAPI loopback capture of your headset (no browser extension, no tab sharing)
 → streaming transcription → end-of-question detection (multi-part questions merged)
 → classify (Verified / Hypothetical / Bridge) → match against your prepared question bank
 → prepared answer instantly, or a streamed AI answer → fact validation → bullets appear one at a time
```

## How it works
1. **Profile** — import your résumé (PDF/DOCX/TXT). Review the extracted facts; only facts you confirm are ever presented as your experience.
2. **Interview** — add the job title, company and job description, choose the interview and answer language, press **Prepare**. A 50–150 question bank with answers, stories and honest gap handling is built for that interview.
3. **Live** — start listening. Answers adapt when the interviewer interrupts (Concise/Rapid), avoid repeating the same story, and follow the interviewer's language (10 languages, right-to-left for Arabic and Persian).
4. **Report** — after Stop, an honest report of the questions asked and the suggestions shown (no scores, no hiring predictions).

## Install (Windows 10/11 x64)
- **Release build**: run `InterviewAssistant.Desktop-stable-Setup.exe` (per-user, no admin). Updates are delta-based and never install during an interview.
- **CI build**: GitHub → Actions → latest green **windows-release** run → artifact `InterviewAssistant-Setup-unsigned` or the portable `InterviewAssistant-Windows-x64` ZIP. Unsigned builds trigger SmartScreen → *More info → Run anyway*.

### Account vs developer mode
| | Account (default in release builds) | Developer mode |
|---|---|---|
| Sign-in | Browser sign-in (Google/Microsoft or e-mail link) | none |
| AI provider key | **Not needed** — held by the service | your own OpenAI key (DPAPI-encrypted) |
| Usage | Plan minutes, metered by the service | billed to your OpenAI account |

## Using it live
| Action | How |
|---|---|
| Start / pause listening | **Start** or `Ctrl+Alt+L` |
| Answer ↔ Coach Mode | `Ctrl+Alt+C` (or the mode pill) |
| Reset adaptive length | `Ctrl+Alt+R` |
| Show / hide window | `Ctrl+Alt+Space` |
| Type or paste a question | bottom box or `Ctrl+Alt+Q`, then Enter |
| Shorter / Technical / Example / Full answer | chips under the answer |
| Home (profiles, interviews, reports, account) | ⌂ icon |
| Pre-interview check | ✓ icon |

All bullets fit without scrolling (text scales down to a readable minimum when space is tight). Answers never rewrite under your eyes.

## Repository
```
src/InterviewAssistant.Core        engine: audio, turn detection, matching, memory, coach, languages, ingestion,
                                   preparation, reports (cross-platform)
src/InterviewAssistant.Client      desktop cloud client: PKCE sign-in, API client, session leases, update policy
src/InterviewAssistant.Contracts   /api/v1 DTOs
src/InterviewAssistant.Backend     ASP.NET Core + PostgreSQL: auth, entitlements, metering, Paddle billing, AI proxy
src/InterviewAssistant.App         WPF desktop app
knowledge/                         generic playbooks + company-neutral question library (no personal data)
samples/                           test fixtures only — never shipped
tests/                             core + backend integration tests
scripts/                           build, smoke test, self-test, installer, N→N+1 update test
```

## Build and test
```powershell
dotnet test tests/InterviewAssistant.Tests                                   # any OS
$env:IA_TEST_PG="Host=localhost;Username=ia_test;Password=ia_test"; dotnet test tests/InterviewAssistant.Backend.Tests   # needs PostgreSQL
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1            # Windows, .NET 10 SDK
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
powershell -ExecutionPolicy Bypass -File scripts\selftest.ps1
powershell -ExecutionPolicy Bypass -File scripts\update-test.ps1             # install N, update to N+1
```

## Documentation
Architecture and status: [docs/v2/ARCHITECTURE_V2.md](docs/v2/ARCHITECTURE_V2.md) · [docs/v2/STATUS_REPORT_V2.md](docs/v2/STATUS_REPORT_V2.md) · [docs/v2/TESTING_V2.md](docs/v2/TESTING_V2.md)
Security and privacy: [docs/v2/SECURITY_V2.md](docs/v2/SECURITY_V2.md) · [docs/v2/PRIVACY_IMPLEMENTATION.md](docs/v2/PRIVACY_IMPLEMENTATION.md)
Commercial: [docs/v2/BILLING.md](docs/v2/BILLING.md) · [docs/v2/ENTITLEMENTS.md](docs/v2/ENTITLEMENTS.md) · [docs/v2/COMMERCIALIZATION.md](docs/v2/COMMERCIALIZATION.md)
Operations: [docs/v2/DEPLOYMENT.md](docs/v2/DEPLOYMENT.md) · [docs/v2/OPERATIONS.md](docs/v2/OPERATIONS.md) · [docs/v2/INSTALLER_AND_UPDATES.md](docs/v2/INSTALLER_AND_UPDATES.md)
Product: [docs/v2/UX_SPEC_V2.md](docs/v2/UX_SPEC_V2.md) · [docs/v2/COACH_MODE.md](docs/v2/COACH_MODE.md) · [docs/v2/LIVE_MEMORY.md](docs/v2/LIVE_MEMORY.md) · [docs/v2/REPORTS.md](docs/v2/REPORTS.md) · [docs/v2/PREPARATION_PIPELINE.md](docs/v2/PREPARATION_PIPELINE.md)
Licences: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
