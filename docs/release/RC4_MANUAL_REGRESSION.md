# 2.1.0-rc.4 — manual regression checklist

Run on a real Windows 10/11 PC with the rc.4 installer from CI, signed in to a Paddle **sandbox** account. "Automated"
names what CI already proves; the manual step checks what automation cannot (real audio devices, real meeting apps,
visual quality, real network). Record pass/fail and notes per row. Status column: not yet run by a person for
rc.4 — this file is the checklist, not a claim.

| # | Area | Steps | Expected | Automated coverage |
|---|---|---|---|---|
| A | Upgrade rc.3 → rc.4 | Install rc.3, sign in, create a profile and an interview, run a short session, then run the rc.4 Setup (or let the updater apply it) | Upgrade in place; profile, interview, reports and settings kept; signed in; rc.3 report opens | CI: install N, self-update to N+1, uninstall |
| B | About | Help → About in English and Persian | Version 2.1.0-rc.4, build hash, "Release candidate", installed path, signing status, service; labels translated, values unchanged | self-test Persian scan |
| C | **Real system sounds** | During a live session with no one speaking, trigger: Windows notification, Teams/Zoom/Meet chat sound, screenshot (Win+Shift+S), error sound, a door knock, music from YouTube | No question, no AI request, history count unchanged, nothing in the report; status shows at most "Hearing audio" then "Listening"; no words flash | `RecordedStyleAudioTests` (60 generated fixtures) |
| D | Short real questions | Interviewer says "Why?", "Why." (falling), "How so?", "Really?", "And then?", "Tell me more.", "What happened?", "Metrics?", "Anything else?" | Each answered once | fixture suite, `ShortTurnTests` |
| E | Long context | Interviewer talks 1–2 minutes of background, then asks a two-part question; also with an interruption ("sorry, one second") and a rephrase | One answer, right ask, constraints used; no duplicate context line | `LongContextRegressionTests` |
| F | Hobby | Interview profile hobby "Hiking and photography"; ask "What are your hobbies?" | Says hiking and photography; no invented traits, feelings, benefits, places or frequency | `PersonalGroundingTests` |
| G | Blank motivation | Leave motivation empty; ask "What motivates you?" | Amber "Not in your profile yet" card with a speaking structure, no fabricated motivation, "Add to profile" opens the Interview profile tab | `UiPagesTests`, self-test |
| H | Persian | Switch UI to فارسی | Everything right-to-left and translated (Help, About, Settings, dialogs, Reports); technical values left-to-right | self-test Persian scan |
| I | Language switch with a draft | Edit a profile field (don't save), switch UI language | No "discard" question; draft, unsaved marker, selection and scroll kept | `UiFoundationTests`, self-test |
| J | Auth form | Sign-in/sign-up: tab order, Enter, empty/invalid fields, double-click on submit | Same control style as other fields, inline errors, one request only | `UiFoundationTests`, self-test |
| K | Reset copy | "Forgot password" with an existing and with an unknown e-mail | Same sentence: "If an account exists for this e-mail, a password reset link has been sent." | `CloudAuthTests` |
| L | Settings in-app | Open Settings from Home and from the Live gear; change a hotkey to a duplicate; Discard; Save | Opens as a Home page on the right section; duplicate rejected; Discard restores; Save applies | self-test |
| M | Profile tabs and actions | Profiles page | 50/50 segmented control; Import resume primary; Delete separated and confirmed (Cancel default) | self-test, `UiPagesTests` |
| N | Reports | Reports page at 1366×768 and maximized; keyboard Up/Down/Enter/Delete; Open, Copy as text, Save as HTML, Delete | List ~1/3, preview ~2/3; actions work; empty state when none | self-test, `UiPagesTests` |
| O | Placeholder/caret | Click into every empty field (incl. password and the Live question box) | Placeholder disappears; caret never overlaps text | self-test |
| P | Pause | Pause during a session, ask a typed question, speak | Typed question answered; speech ignored; paused time not billed | engine + billing tests |
| Q | Resume | Resume | History continues; numbering continues | engine tests |
| R | Variant | Ask for a shorter/longer variant of an older item | That item changes; no new history entry | engine tests |
| S | Wi-Fi | Turn Wi-Fi off for 2 s, then for 10 s, during a session | No toast for 2 s; "Connection lost — reconnecting…" then "Connection restored" for 10 s; transcription resumes | `UiPagesTests` (debounce), engine reconnect tests |
| T | Stop | Stop the session | Report created and selected on Reports; minutes billed for the session | report + billing tests |
| U | Sign out | Sign out during and after a session | Live history empty (no "0 / 1"); Home data hidden; sign-in restores it | `UiPagesTests` |
| V | Preflight | Run the pre-interview check with and without audio playing | Warnings give "READY — WITH WARNINGS"; with audio "Test complete · transcription test passed" and "READY FOR INTERVIEW" | `UiFoundationTests` (verdict) |
| W | DPI | 125 %, 150 %, 200 % on 1366×768, 1920×1080, 4K; English, German, Persian | No clipped labels, Reports panes and toolbar readable, dialogs centred | — |
| X | Maximize | Maximize/restore Home and Live windows, with a dialog open | Dialog backdrop covers the window with matching corners; layout stretches | — |

## Additionally

- Screen reader (Narrator, NVDA): dialogs announced with title and message; status changes announced politely.
- Windows high contrast: form controls, segmented control and report rows readable.
- Panel interview: "John, what do you think?" → no answer; "Shervin, how would you approach this?" → answer.
