# Troubleshooting

| Symptom | Fix |
|---|---|
| Level bar flat / `NO AUDIO` | Settings → pick the device Meet plays to (not "System default" if Meet uses a specific headset). Bluetooth: choose the *Headset/Hands-Free* endpoint if Meet uses it. Unmute Meet. |
| `API ERROR` / "API key rejected" | Settings → paste key → Test. Check billing/quota on the OpenAI account. |
| `RECONNECTING` | Network blip; the app retries with backoff (1→2→4→…30 s). Keep going with typed questions; prepared answers still work offline. |
| Transcription check fails with model error | Settings → Transcription model → try `gpt-4o-mini-transcribe`; Realtime protocol → `beta` (or `ga`). |
| Answers appear before the interviewer finishes | Settings → End-of-question wait → move toward "Waits longer". |
| Answers feel slow | Diagnostics shows where time goes. Try answer model `gpt-4.1-mini` (default) and wired internet. Prepared answers are instant regardless. |
| Wrong question matched | Press **Regenerate** (AI answer for the exact wording). |
| Hotkey doesn't work | Another app owns it — change it in Settings (e.g. `Ctrl+Alt+F9`). |
| Window off-screen after monitor change | Delete `%APPDATA%\InterviewAssistant\settings.json` (position resets). |
| SmartScreen blocks EXE | More info → Run anyway (unsigned build). |
| Logs | `%LOCALAPPDATA%\InterviewAssistant\logs\app-YYYYMMDD.log` |
