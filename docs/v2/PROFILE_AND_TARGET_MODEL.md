# Profile & Target Model

`Account → CandidateProfileRecord → InterviewTarget → PreparedPack → Sessions/Reports`

| Entity | Key fields |
|---|---|
| CandidateProfileRecord | name, preferred name, headline, location, time zone, interview/answer language, speaking style, answer length, seniority, notes, LinkedIn text, portfolio links, documents, **facts**, stories |
| SourceDocument | file name (sanitised), kind (Resume/JD/CompanyMaterial/LinkedIn/Portfolio/Notes), detected language, text (injection-guarded), SHA-256, NeedsOcr, security flags |
| ProfileFact | kind (Role/Achievement/Skill/Education/Language/Certification/Project/Summary), text, company, title, location, period, metrics, **provenance (doc id, line range, snippet)**, **status**, confidence, flags |
| FactStatus | `Source` (extracted, not a claim) · `UserConfirmed` (verified claim) · `AiInference` (never a claim until confirmed) · `GeneratedKnowledge` (never a claim) · `Rejected` |
| Story | built only from **confirmed** achievements; keeps fact ids (provenance chain) |
| InterviewTarget | job title, company, JD text/URL/files, interview type, date, expected + answer language, role notes, company URLs, materials, preparation state |
| PreparedPack | per target: TargetContext, engine profile (confirmed facts only), stories (ranked), 50–150 questions, snippets, match map, gaps, terminology, difficult questions, questions to ask, truth policy, stage log, warnings |

Truth boundary: `ProfileService.ToEngineProfile` exports **only `UserConfirmed` facts** to the live engine; the validator's verified numbers/entities come from that export. Tests: `UnconfirmedFactsNeverReachTheEngineProfile`, `PartiallyConfirmedProfileNeverLeaksUnconfirmedMetrics`.

Storage (desktop): `%APPDATA%\InterviewAssistant\workspace\{profiles,targets,packs,sessions}\<id>.bin`, each file DPAPI-encrypted JSON; ids are validated (no path traversal). Export = ZIP of JSON. Delete profile cascades to targets, packs, sessions.

Formats: PDF (PdfPig), DOCX (built-in OpenXML parsing), TXT/MD (UTF-8), pasted text. Image-only PDFs are detected and the user is asked for text (no OCR dependency).

Sample/test fixture: `samples/shervin-teroxx/` (+ `tests/fixtures/resumes/shervin_fallahdoust.txt`). It is never loaded unless explicitly requested (self-test, "Load sample").
