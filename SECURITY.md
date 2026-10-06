# Security & Privacy

| Area | Implementation |
|---|---|
| API key storage | Windows **DPAPI**, CurrentUser scope + app entropy, `%APPDATA%\InterviewAssistant\apikey.dpapi`. Only your Windows account on this PC can decrypt it. Never in plaintext config. |
| Key in logs | Never logged; the logger additionally redacts any `sk-…` pattern. Settings shows only a mask (`sk-…abcd`). |
| Key in repo / package | No keys in source. `.gitignore` excludes `*.dpapi`, `settings.json`, `.env`. `build-release.ps1` refuses to package `*.dpapi`/`settings.json`/`.env`. A repository secret scan (regex for `sk-`, `api_key=`) was run before commit: clean. |
| Audio | Interviewer playback audio only (WASAPI loopback). **Microphone is never opened.** Audio is held only in a bounded ~3 s in-memory queue (drop-oldest), sent to OpenAI for transcription, never written to disk. Queue is cleared on Stop. |
| Transcripts | Not saved by default. Optional "save questions and answers when I press Stop" (Settings → Privacy) writes a Markdown file to Documents. |
| Network | TLS only (`wss://api.openai.com`, `https://api.openai.com`). No telemetry. |
| Logs | `%LOCALAPPDATA%\InterviewAssistant\logs` — status/latency lines; question text is logged only for duplicate-suppression events. Delete the folder any time. |
| Process | Runs as invoker (no admin). No hooks into Chrome, no injection, no hidden windows. |

Data sent to OpenAI: interviewer audio (transcription) and, for non-cached questions, the question plus a compact context (résumé facts, short notes). Review OpenAI's data-usage policy for your account.
