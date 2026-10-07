# UX Research V2 (desk research)

Method: heuristic review of modern Windows productivity and AI tools (Windows 11 Fluent 2 guidance, Microsoft Teams live captions, PowerToys, Raycast/command palettes, Linear, Notion, Grammarly's floating assistant, Otter/Fireflies live notes) and of the validated prototype in real use. No external user study yet — **NOT YET VALIDATED with customers**.

Findings applied:
1. **Glanceability beats completeness during live use.** Live caption tools keep 1–3 lines visible with no scroll; answers must fit without scrolling → auto-fit text (prototype issue: 3rd bullet sometimes below the fold).
2. **Calm status, loud content.** Status as a small dot + word; the answer is the hero. Colour encodes state only.
3. **Progressive disclosure.** Setup complexity (profiles, materials, languages, models) lives in a dashboard, never in the overlay.
4. **Checklist onboarding** (Linear/Notion style) converts better than wizards with long forms: Account → Profile → Résumé verified → Job → Prepared → Hardware ready → Start.
5. **Review-before-trust** for AI-extracted data (Grammarly suggestions, Notion AI): show extracted facts with source snippets and require confirmation.
6. **Keyboard-first** for in-call control (Raycast/PowerToys): global hotkeys for mode switch, pause, type question.
7. **Never steal focus** during calls (Teams captions): overlay updates must not activate the window.
8. **Honest analytics** (Fireflies/Otter): summaries label what was observed vs inferred.
9. **RTL** needs mirrored layout, not just right-aligned text; mixed Latin terms inside RTL require bidi-safe runs.
10. **Fluent 2 tokens**: 4-px spacing grid, 8/12 px radii, Segoe UI Variable, layered surfaces, accent used sparingly.
