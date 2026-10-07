# UX Spec V2

## Information architecture
```
Main window (Dashboard)
 ├─ Onboarding checklist (until complete)
 ├─ Profiles  → Profile editor (sources, extracted facts review, story bank, export/delete)
 ├─ Targets   → Target editor (job, company, JD, materials, type, languages) → Prepare → readiness checklist
 ├─ Sessions  → Report viewer (HTML export)
 ├─ Account   → plan, usage, renewal, manage subscription, upgrade, devices, sign out
 └─ Settings  → audio, answer mode, languages, font, opacity, hotkeys, privacy, telemetry, update channel, advanced/developer
Live overlay (separate always-on-top window, opened by "Start interview")
```

## Live overlay rules
- Header (24 px): status dot+word · `Profile · Target` (subtle) · `Adaptive: concise` chip when active · mode chip (Answer/Coach).
- Question: 1–3 lines, semibold. Answer: 3 bullets, auto-fit: font steps down (min 15 px) and spacing compresses until all bullets fit; never scroll by default.
- Coach Mode: one line of 3 uppercase keywords separated by `·`, one line structure with `→`. Max 2 lines.
- Hotkeys: Ctrl+Alt+Space show/hide · Ctrl+Alt+L start/pause · Ctrl+Alt+Q type · **Ctrl+Alt+C Answer/Coach** · Ctrl+Alt+R reset adaptive.
- No focus stealing; remembers position/size per monitor; DPI-aware; RTL mirrors layout for Arabic/Persian.

## Onboarding steps
Welcome + authorized-use acknowledgement → Sign in (browser) → Plan/trial → Profile (name, languages) → Upload résumé → Review facts (confirm) → Add target (paste JD) → Prepare (progress) → Audio test → Ready.

## Design tokens
Existing dark token set (Bg/Surface/Surface2/Border/Text/Accent/Listening/Thinking/Error) plus Light theme variants; radii 8/12; spacing 4/8/12/16/24; type ramp 11/13/15/17/20/24.
No production screen shows a hard-coded person or company name.
