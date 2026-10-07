# Multilingual Validation Status (per language)

Launch languages: en, es, fr, de, pt, it, ar (RTL), fa (RTL), zh (Simplified), hi.

| Capability | Status |
|---|---|
| Language registry, RTL flags, answer-language resolution (same-as-interviewer / override / locked fallback) | **TESTED** (unit) |
| Per-question language detection (script + function words + morphology) for all 10 | **TESTED**: 10 single-sentence cases + 60 fixture questions (`MultilingualLiveEvaluationTests.FixtureCovers…`) |
| Résumé/JD parsing with localised section headings | **TESTED** for en, de; headings implemented for all 10 — **NOT YET VALIDATED** with real CVs in es/fr/pt/it/ar/fa/zh/hi |
| Memory across language switches; "already mentioned" in 10 languages | **TESTED** (unit) |
| Prepared answers never shown in a different language than required | **TESTED** |
| Coach parser for Latin/Arabic/Persian/Han scripts | **TESTED** |
| Live AI answers per language (native, speakable, factual, Bridge on gaps) | **NOT YET VALIDATED** — harness `MultilingualLiveEvaluationTests.LiveAnswersInAllTenLanguages` runs 70 questions + 10 coach checks when `OPENAI_API_KEY` is set; naturalness needs native-speaker review |
| Realtime transcription per language (accents, noise, switching, proper nouns, boundary accuracy) | **NOT YET VALIDATED** — requires audio test sets + API key; procedure in TESTING_V2.md |
| UI localisation resources for 10 languages + RTL layout | see Phase G |

Do not market a language as production-ready until both NOT YET VALIDATED rows are signed off for it.
