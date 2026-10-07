# Desktop client (V2)

## Modes
| Mode | When | AI path | Key handling |
|---|---|---|---|
| **Account** (`Settings.Mode = "cloud"`, default) | `cloud.json` present with valid https endpoints | Server: ephemeral Realtime secret for transcription, `/answers/stream` proxy for answers | No provider key on the PC. Supabase tokens DPAPI-encrypted (`session.dpapi`) |
| **Developer** | No service configured, or chosen in onboarding | Direct OpenAI with the user's own key (the validated V1 path, unchanged) | DPAPI `apikey.dpapi` |

`cloud.json` (next to the EXE, per release channel; `%APPDATA%\InterviewAssistant\cloud.json` overrides for staging) holds **public values only**:
```json
{ "apiBaseUrl": "https://api.example.com", "supabaseUrl": "https://<project>.supabase.co", "supabaseAnonKey": "<publishable anon key>", "oauthProviders": ["google", "azure"] }
```

## Components (`src/InterviewAssistant.Client`, cross-platform, tested on Linux CI)
- `Pkce` (RFC 7636 S256, tested against the RFC vector), `LoopbackRedirect` (127.0.0.1, random port, state-checked, one-shot, `no-store`).
- `SupabaseAuth`: OAuth via system browser or e-mail magic link (PKCE), token exchange, refresh 60 s before expiry, revoked refresh → local sign-out.
- `BackendClient`: typed `/api/v1` client, `X-Client-Version`, correlation ids, one forced-refresh retry on 401, typed `BackendException` (status/code/correlation id).
- `CloudSession`: lease start (idempotency key reused across retries), heartbeats, credential renewal < 2 min before expiry, server-ended leases surfaced; pause ends the lease (paused time is not billed).
- `CloudAnswerProvider`: `IAnswerProvider` over the SSE proxy; the engine is unchanged.
- `InstallationIdentity`: random id, no hardware fingerprint.

## UI
- **Home** (`HomeWindow`): onboarding checklist (authorized-use acknowledgement, account or developer mode, profile, interview, readiness), Profiles (résumé import with security flags, fact review — only confirmed facts are used), Interviews (title/company/languages/JD, Prepare with stage progress, use for live), Reports (open/export/delete), Account (sign-in, plan & minutes, upgrade/portal via browser, devices, export, delete, sign out), UI language.
- **Live overlay**: Coach card (3 keywords + structure, Ctrl+Alt+C), adaptive badge (Concise/Rapid, Ctrl+Alt+R resets), minutes left, auto-fit (font shrinks to ≥70 % so all bullets are visible without scrolling), answer text right-to-left for Arabic/Persian answers, UI right-to-left for ar/fa UI language, report button after Stop.
- **Localisation**: `UiStrings` (40 keys × 10 languages; machine drafts pending native review).

## Offline / degraded policy (intentional)
| Situation | Behaviour |
|---|---|
| Service or internet down | App launches normally. Prepared answers for the active interview (local, user-owned data) and the manual question box keep working. Live transcription and AI answers are unavailable and the overlay/Account page say so — nothing pretends cloud AI is running. |
| Lease ends mid-session (minutes used up, max duration, device removed, account disabled) | Listening pauses with a specific note; prepared answers stay available. |
| Maintenance switch on | New sessions refused with the maintenance message; account/billing still reachable. |
| Client below minimum version | Every API call returns 426 → "please update" message; local features keep working. |
| Signed out / token revoked | Local sign-out; prompted to sign in for live features. |

Prepared answers are not gated offline because they are the user's own locally stored preparation; the costly parts (transcription, generation) are always server-metered.

## Validation status
| Item | Status |
|---|---|
| Client ↔ real backend + PostgreSQL (session, heartbeats, renewal, SSE answers, errors, token refresh, sync) | TESTED (`ClientEndToEndTests`) |
| PKCE, loopback, token store, refresh/revocation, magic-link flow (stubbed Supabase) | TESTED (`CloudAuthTests`) |
| WPF windows, Coach toggle, auto-fit, RTL switch, local preparation, report on Stop | PASSED — self-test inside the real EXE on windows-latest (28/28) |
| Real Supabase sign-in, real OpenAI ephemeral transcription via the server | NOT YET VALIDATED — needs Supabase project + server OpenAI key |
| Visual quality of new screens | NOT YET VALIDATED by a human on Windows |
