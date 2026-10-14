# Manual end-to-end test — Paddle sandbox

Run on a clean Windows 10/11 x64 PC with speakers or a headset. Use a **new** e-mail address. Record the result
(✅/❌ + note) next to each step. Backend: `https://real-time-windows-interview-assistant-production.up.railway.app`
(below: `$API`).

## 0. Configuration (one time, before testing)
| # | Where | Set | Check |
|---|---|---|---|
| 0.1 | Railway variables | `Auth__PublishableKey` = Supabase publishable (anon) key | `$API/api/v1/config` → `auth.available: true` |
| 0.2 | Railway variables | `Product__PublicBaseUrl` = `$API` | `$API/api/v1/config` → `auth.emailConfirmedRedirectUrl` = `$API/auth/confirmed` |
| 0.3 | Railway variables | `Paddle__ClientToken` = sandbox client-side token (`test_…`, Paddle → Developer tools → Authentication) | `$API/pay` does **not** say "Checkout unavailable" |
| 0.4 | Paddle sandbox → Checkout → Checkout settings | Default payment link = `$API/pay` | — |
| 0.5 | Supabase → Authentication → URL configuration | Site URL `$API/auth/confirmed`; Redirect URLs add `http://127.0.0.1:*/**` and `$API/auth/confirmed` | — |
| 0.6 | Supabase → Authentication → Providers → Email | Enabled; "Confirm email" on (recommended) | — |
| 0.7 | Railway variables (before public launch) | `Product__PrivacyPolicyUrl`, `Product__TermsUrl`, `Product__BillingTermsUrl`, `Product__SupportEmail` | links appear on the Account page |
| 0.8 | GitHub Actions → production-smoke | latest run green, no configuration warnings | — |

Railway redeploys after variable changes; wait for `$API/health/ready` → `{"status":"ready"}`.

## 1. Install and first launch
1. Download `InterviewAssistant-Setup-unsigned` (windows-release artifact) or the release `Setup.exe`. Run it.
   *Expected:* SmartScreen warns about an unknown publisher (build is unsigned) → "More info → Run anyway". The app installs per-user and starts.
2. *Expected:* Home → "Get started" opens. No API-key field and no "Developer build" button exist anywhere (Settings included).
3. Open Home → Account. *Expected:* the sign-in form (Sign in / Create account) appears. No error banner.

## 2. Account creation and trial
4. Choose **Create account**, enter the new e-mail and a password (≥ 8 chars), confirm it, submit.
   *Expected:* a message asking you to confirm via the e-mail link (if confirmation is on). Wrong confirm password → a readable message, no request sent.
5. Click the link in the confirmation e-mail. *Expected:* browser shows "E-mail confirmed — return to Interview Assistant".
6. In the app, **Sign in** with the e-mail and password. *Expected:* plan card shows "Trial · 30 min left", devices list shows this PC.
7. Sign in with a wrong password (sign out first) → "The e-mail or password is not correct." Then sign in correctly again.
8. **Forgot password?** → browser opens the Supabase reset link from the e-mail → returns to `127.0.0.1` "You can close this tab" → app prompts for a new password → sign in with the new password works.

## 3. Live session on the trial (server-managed OpenAI)
9. Create a profile (Profiles → Import résumé…) and an interview (Interviews → New interview, paste a job description) → **Save & prepare**. *Expected:* preparation completes; usage shows 1 of 2 preparation runs used.
10. Open the live assistant. Play a YouTube interview question video (e.g. "Tell me about a time you handled conflict") through the speakers/headset. Press **Start** (Ctrl+Alt+L).
    *Expected:* the audio level bar moves; transcript appears within ~1–2 s; question is detected; an answer streams in. The microphone is **not** used (Windows privacy indicator shows no mic access).
11. Ask a hypothetical question ("How would you design…") → answer phrased "I would…". Ask "Have you personally done X?" for something not in the profile → short honest bridge then approach; no invented history.
12. Pause, then Stop. *Expected:* report saved; Account shows trial minutes reduced by roughly the session length (not more than ~1 min extra).

## 4. Upgrade via Paddle sandbox
13. Account → **Upgrade to Pro (USD 19.99 / month)**. *Expected:* browser opens `$API/pay?_ptxn=txn_…`, Paddle overlay shows "Interview Assistant Pro", sandbox banner. App shows "Waiting for payment confirmation…".
14. Pay with card `4242 4242 4242 4242`, any future expiry, CVC `100`, any name/postcode. *Expected:* "Payment received — return to the app".
15. Within ~30 s the app shows plan **Pro** with 600 live minutes for the period. (Paddle → Notifications → log shows `transaction.completed`, `subscription.created`, `subscription.activated` delivered with HTTP 200.)
16. Click **Upgrade** again (button should be hidden; if called via API it returns 409 `already_subscribed`).

## 5. Pro session and failure handling
17. Start a live session. *Expected:* works as in step 10; Account usage counts against the 600 Pro minutes.
18. While listening, disconnect Wi-Fi for ~20 s, reconnect. *Expected:* status shows the connection problem and reconnecting; listening resumes automatically without a second lease; no crash.
19. While listening, sign in on a second PC (or a second Windows user) with the same account and start a session there. *Expected:* second PC: "An interview session is already running for your account on another PC. Stop it there first." (limit 1). First PC keeps working.
20. Close the app mid-session (tray → Exit). Reopen. *Expected:* still signed in, plan Pro, profiles/interviews/reports intact, no orphan lease (a new session starts immediately).

## 6. Billing management
21. Account → **Manage billing**. *Expected:* Paddle customer portal opens in the browser (sandbox), showing the subscription and invoice.
22. In the portal, cancel the subscription (at period end). *Expected:* app (Refresh) shows "Pro · ends <date>"; Pro features continue until then.
23. Paddle sandbox → Subscriptions → the subscription → simulate **past_due** (or use Notifications → Simulate `subscription.past_due` against this subscription). *Expected:* plan card shows "Pro · payment problem" with a notice to update the payment method; access continues during the grace period (7 days).
24. Simulate `subscription.canceled` (or end the subscription now in Paddle). *Expected:* plan card shows "Subscription ended" (or "Trial used up"); the overlay banner points to Home → Account; starting a session shows a readable upgrade message.

## 7. Account lifecycle
25. Account → **Export my data…** → JSON saved; contains profile/usage but no tokens or keys.
26. Devices → remove the second PC. *Expected:* that PC can no longer start sessions and is told its device was removed; it must sign in again to re-register (subject to the device limit).
27. **Delete account…** (confirm). With an active subscription this is refused with "Cancel your subscription in the billing portal before deleting the account." (step 24 already ended it). *Expected:* service data is deleted and the app signs out; signing in again with the same login shows "This account was deleted."; local profiles/reports on this PC are untouched (the dialog says so).

## Evidence to keep
Screenshots of steps 6, 10, 15, 21, 24; Paddle notification log; Railway logs for the test window (JSON lines with
`webhook outcome`, `session started/ended`); `%LOCALAPPDATA%\InterviewAssistant\logs\app-*.log` from the PC.
