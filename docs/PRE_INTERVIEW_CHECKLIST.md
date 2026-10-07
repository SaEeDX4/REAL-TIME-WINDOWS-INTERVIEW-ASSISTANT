# Windows hardware/API test — 10–15 minutes

Do this once on your interview PC, then the 2-minute routine on interview day.
Install with `InterviewAssistant.Desktop-stable-Setup.exe`, or download the portable build: GitHub → Actions → latest green **windows-release** run → artifact **InterviewAssistant-Windows-x64** → unzip (e.g. to `C:\InterviewAssistant`).

| # | Test | Steps | PASS when |
|---|---|---|---|
| 1 | Launch | Start *Interview Assistant* (unsigned builds: SmartScreen *More info → Run anyway*) | Live window opens; *Home → Get started* appears on first run |
| 2 | Offline self-test (optional, 20 s) | In PowerShell in the folder: `.\InterviewAssistant.exe --selftest result.json; Start-Sleep 25; Get-Content result.json` | `"passed": true` |
| 3 | Account | *Home → Account* → sign in (or *developer mode*: Settings → paste your OpenAI key → **Test**) | Plan and minutes shown (or "✓ Answer service OK") |
| 3b | Preparation | *Home → Profiles* → import résumé → confirm facts; *Interviews* → add job → **Prepare** → **Start interview** | "prepared" with 50–150 questions; live window shows the interview name |
| 4 | Audio | *Settings* → device = your headset → Save. Pre-interview check → **Test audio**, play a YouTube talk in Chrome | Shows endpoint, sample rate, channels, peak/RMS moving, **✓ AUDIO DETECTED** |
| 5 | Transcription | **Test transcription**, keep the YouTube talk playing | Spoken words appear as text, **✓ TRANSCRIPTION WORKS** |
| 6 | AI | **Test AI** | 3 bullets for the test question + first-bullet latency (target < 2 s) |
| 7 | Verdict | **Run all checks** | **✓ READY FOR INTERVIEW** |
| 8 | Manual question | Close check → type `How would you improve user retention?` → Enter | 3 bullets appear (prepared/AI) |
| 9 | Live question | **Start**. In Chrome play/ask aloud via another device: *"How would you prioritize a product backlog?"* (pause 1 s mid-sentence once) | Question appears **once**, then 3 bullets; status returns to LISTENING |
| 10 | Google Meet | Join a test call (e.g. from your phone into a Meet), speak a question from the phone | Question captured from Meet audio |
| 11 | Pause / resume | `Ctrl+Alt+L` twice | PAUSED → LISTENING; nothing answered while paused |
| 12 | Always on top | Click into Chrome | Assistant stays visible above Chrome |
| 13 | Bluetooth (if used) | Turn headset off/on during listening | Status recovers within ~10 s, meter moves again |
| 14 | Diagnostics | Pulse icon in header | Latency lines, CPU < 5 %, memory stable |

## Interview day (2 minutes)
1. Headset on, same device in Windows and in Meet (*Meet → Settings → Audio → Speakers*).
2. Launch app → **Run all checks** → **READY FOR INTERVIEW** → **Start interview**.
3. Compact mode on, font Large, window near the camera.
4. If anything fails mid-call: `Ctrl+Alt+Q`, type the question, Enter — prepared answers work even offline.

## Questions to ask them
Your prepared interview includes suggested questions to ask, based on the job description. Good generic ones:
- What would success look like in this role after six months?
- What is the biggest problem you'd want this role to solve first?
- How do product, engineering and other teams work together on a new feature?
- Which metrics does leadership review regularly?
- What would make someone exceptional in this role after a year?

Re-read the live job posting before the call — company facts come only from the documents you added.
