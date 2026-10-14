# 2.1.0-rc.1 — manual regression (≈ 12 minutes, Windows)

Use the **InterviewAssistant-Setup-v2.1.0-rc.1.exe** (or the portable ZIP) from the CI artifact
`InterviewAssistant-v2.1.0-rc.1`. Verify the SHA-256 first:
`Get-FileHash .\InterviewAssistant-Setup-v2.1.0-rc.1.exe -Algorithm SHA256` must equal the `.sha256` file.
Have a YouTube interview video (or a second device in the Meet call) ready to play the interviewer's voice.

| # | Min | Step | Pass when |
|---|---|---|---|
| 1 | 1 | Run Setup.exe (SmartScreen: *More info → Run anyway* — the RC is unsigned). | Installs without admin prompt; Start Menu + Desktop shortcut; app opens. Help & about shows **Version 2.1.0-rc.1 · Release Candidate · Build <commit>** and "Unsigned build". |
| 2 | 1 | Home → Account: sign in with your existing test account. | Plan card shows Pro; no errors. |
| 3 | 1 | Home language picker; Interviews → open an interview → *Interview language* and *Answer language* pickers. | Readable names only ("English", "Persian — فارسی", "Detect automatically", "Same as interview language") — no `LangItem {…}` / `LanguageInfo {…}`. |
| 4 | 1 | Profiles → your profile → *To review* / *Confirmed* filters; press **Confirm all…** then **Cancel**. | Filters work; a confirmation dialog appears; Cancel changes nothing. |
| 5 | 1 | Interviews → select the prepared interview → **Open Live Assistant**. | Toast "Live Assistant opened · …". First time: short "Before you start" card; **Got it** closes it; the **?** button reopens it. Button reads **Start listening**; status **Not listening**. |
| 6 | 1 | Press **Start listening**. Stay silent ~10 s. | "Listening" + "Listening to system audio · microphone off"; after silence "Listening · quiet" (not a red error). Minutes show e.g. "585/600 min" (hover: billing period + today). |
| 7 | 1.5 | Play: *"How would you increase XAB utility and user retention?"* — pause ~0.5–1 s — *"And which metrics would you track to prove that the strategy is working?"* | **One** question card containing both parts, **one** answer, history shows **1** entry for it (not two). |
| 8 | 1 | Play: *"XAB, Abloxx, Teroxx, MiCAR"* in a sentence. | Transcript shows the terms (XAB not "app"). |
| 9 | 1 | Play: *"Have you personally built a closed-loop internal token ledger?"* | First bullet is an honest bridge ("I haven't owned…"); no "I built/I led" claims. |
| 10 | 1 | Play: *"How would you design a closed-loop internal token ledger?"* | Confident "I'd…" answer, no disclaimer, 3 bullets visible without scrolling. |
| 11 | 1.5 | Press **Pause**. Type the long *"Imagine XAB suddenly has strong token distribution…"* question in the box → Enter. | Toast "Listening paused — no live minutes are being used"; status "Paused" + "Live minutes are not being used"; an AI answer appears (no "AI unavailable (Unknown)"); the minutes number does not drop while paused. |
| 12 | 1 | Press **Resume**, play one more question. | Answered once; status cycles Transcribing → Question detected → Preparing answer → Answer ready. |
| 13 | 0.5 | Turn Wi-Fi off, type a new question; turn Wi-Fi on. | A specific message (e.g. "No connection to the service. Prepared answers still work.") — never "Unknown". |
| 14 | 0.5 | Stop (square icon). Home → Reports → Open. | Report opens; toast on export. |
| 15 | 0.5 | Home: maximize, restore, minimize; close and reopen the app. | Maximize keeps the taskbar visible; size/position restored; still signed in. |
| 16 | 0.5 | Windows Settings → Apps → Interview Assistant → Uninstall. | Removed cleanly (your settings in %APPDATA% are kept for a reinstall). |

Record PASS/FAIL per row with a note. The build that passes is the one to promote: same commit,
same SHA-256, same CI run.
