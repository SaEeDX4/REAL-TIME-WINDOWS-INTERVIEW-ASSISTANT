# 2.1.0-rc.5 — account and authentication audit

Scope: the rc.5 Account page and every sign-in state of Home. Code: `HomeWindow.Account.cs`, `UiState/AuthFormState.cs`,
`UiState/AccountMenu.cs`, `UiState/AccountView.cs`, `UiState/PendingReset.cs`, `Views/DeleteAccountDialog.cs`,
`Services/CloudService.cs`, `Client/Auth.cs`. Tests: `Rc5AccountTests`, `CloudAuthTests`, `SecurityAuditFixTests`.

## What rc.5 delivers

- **Account dashboard**: plan and minutes (this period, available today), devices with Remove, Security (change
  password, switch account), **Privacy & data** (export my data) and a separate **Danger zone** (delete account). Two
  responsive columns from 720 px of content (the default 1040 px window and 1366×768 @125 % show two columns; rc.5
  UX audit M3 moved the breakpoint from 900), one column below.
- **Header account menu**: avatar initial + e-mail; Manage account, Settings, Switch account, Sign out. Keyboard: Tab cycles in
  the menu, arrows wrap, Home/End, Escape closes and returns focus. The one Sign out lives here.
- **Switch account**: asks first, then the unsaved-edits guard; ends a running session; signs out quietly (no
  "Signed out" toast) and opens "Sign in with another account" naming the previous address.
- **Forgot password** and **Create account** are dedicated states of the auth form with one primary button each.
  Forgot password shows the same sentence for known and unknown addresses; Resend unlocks after 60 s (the auth
  server's per-address limit — rc.5 security audit M2); timeout and offline have their own copy with Retry.
- **Delete account dialog**: "Permanently delete your account?" with what is deleted and what stays (invoices at
  Paddle), re-authentication, then a separate question about the data on this PC. While a subscription can still
  charge, the dialog offers only Manage billing / Keep account (security audit L6).
- **First run**: Home opens on Get started; after the first sign-in it returns there (UX audit H2).

## Audit findings (account/auth)

| Finding | Outcome |
|---|---|
| A pending reset could replace the signed-in session (security H1) | Fixed: `PendingReset` tickets, code never exchanged while another account is signed in, stale tasks cannot touch the form |
| Resend at 30 s killed the working link (M2) | Fixed: 60 s |
| Old-server 429 text could reveal an account (L7) | Fixed |
| Focus lost after reset request/error (L8) | Fixed |
| Refresh cancelled or failing left "Connecting…" / escaped (error-state H1/H2) | Fixed in `CloudService.RefreshAsync`: cancellation restores the previous state; auth-server, JSON and transport errors become ServiceUnavailable; revoked tokens still sign out |
| Account dashboard one column at the default size (UX M3) | Fixed: breakpoint 720 |
| Local data deletion reported success after a failure (error-state M4) | Fixed: folder named in `del_local_failed` |
| Version and e-mail scrambled inside Persian/Arabic sentences (l10n) | Fixed: LTR isolation (U+2066/U+2069) for versions and addresses on the Account page |
| Arabic billing dates on the Hijri calendar (l10n) | Fixed: Gregorian formatting culture for Arabic everywhere |

## Verified unchanged

Re-authentication before export/delete/password change; a different returned user id signs out; tokens are stored with
DPAPI and never logged; signing out hides every profile, interview and report of that account; another account on
the same PC never sees them. The backend is unchanged in rc.5, so server-side rules (subscription blocks deletion,
identity deletion, refunds) are exactly those verified for rc.4.

## Not automated

Real e-mail delivery of reset links, Google/Microsoft/GitHub/Apple sign-in in a browser, and the delete flow against a
live sandbox account are manual rows L, M and N of `RC5_MANUAL_REGRESSION.md` (not yet run by a person for rc.5).
