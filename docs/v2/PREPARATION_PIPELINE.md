# Preparation Pipeline

`PreparationPipeline.RunAsync(profile, target, enricher?, progress)` — 15 reported stages:
1 verify profile (requires ≥1 confirmed fact; unconfirmed count → warning) · 2 JD extraction (multilingual headings, injection lines removed) · 3 candidate↔role match map (token overlap per requirement) · 4 gap analysis · 5 terminology (acronyms, CamelCase, mid-sentence capitalised terms; preserved verbatim across languages) · 6 company/product context **from user-provided sources only** (labelled) + domain playbooks · 7 story ranking by role relevance · 8 likely questions · 9 fast answers · 10 Coach keywords/structure · 11 playbook · 12 truth policy · 13 difficult questions · 14 questions to ask (≥10) · 15 runtime cache.

Question bank (adaptive 50–150): core (intro, motivation, why hire, strengths, weakness, 90 days, questions to ask, 5 years), behavioural (one story per topic, least-used first), per-role experience, per-requirement verified / bridge (for gaps) / approach, per-responsibility approach, and the company-neutral expert library filtered by detected domains (general/product/fintech/regulated/technical).

Confidence: verified/library answers 0.75–0.85 are shown instantly; template answers (< 0.7) are passed to the AI as **reference**, not shown instantly (`EngineOptions.MinCacheConfidence`).

Deterministic & offline by design (fast, free, testable). `IPreparationEnricher` is the hook for backend AI enrichment (target-language answers, better hypothetical answers); enrichment failures keep the local pack. Non-English targets: local answers are English reference; native-language answers come from live AI / enrichment (**NOT YET VALIDATED** with real AI per language).

Live engine never receives whole documents — only the compact pack.
