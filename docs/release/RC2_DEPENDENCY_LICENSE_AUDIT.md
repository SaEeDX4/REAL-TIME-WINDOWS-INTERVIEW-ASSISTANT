# 2.1.0-rc.2 / rc.3 / rc.4 — dependency & license audit

rc.3: no new shipped dependencies (the relay uses only ASP.NET Core and System.Net.WebSockets on the backend). The
test-only audio fixtures are generated in the repository and dedicated to the public domain (CC0); they are not
shipped. The full license texts in `licenses/` ship with the rc.3 package.

Scope: everything redistributed to customers in the Windows desktop package (portable ZIP and the Velopack
installer), plus the backend and build/test tooling for completeness. Question answered per component: may it be
shipped inside a closed-source, commercially sold product, what does its license require, and how is that met.

Method (evidence, not memory):

* NuGet closure — `dotnet list <project> package --include-transitive` for App, Core, Client, Contracts and the
  `net10.0-windows/win-x64` target of `src/InterviewAssistant.App/obj/project.assets.json`. Result: NAudio.Core 2.2.1,
  NAudio.Wasapi 2.2.1, Velopack 1.2.161, PdfPig 0.1.16; nothing else (none of them has dependencies on net10.0).
* Runtime — a self-contained restore (`-r win-x64 -p:SelfContained=true`) resolves `Microsoft.NETCore.App.Runtime.win-x64`
  and `Microsoft.WindowsDesktop.App.Runtime.win-x64` **10.0.13** (SDK 10.0.402), both built from dotnet/dotnet commit
  4099f83977f4d29604a9aeb971fb0a7bc1d5f699. The WindowsDesktop `RuntimeList.xml` shows the WPF profile also ships the
  native `wpfgfx_cor3`, `PresentationNative_cor3`, `PenImc_cor3`, `D3DCompiler_47_cor3` and `vcruntime140_cor3` DLLs.
* Velopack native binaries — vpk 1.2.161 downloaded from nuget.org; `vendor/` holds the Windows `setup*.exe`,
  `update*.exe` and `stub*.exe` that `vpk pack` places into `InterviewAssistant-Setup.exe` and the install folder.
  Statically linked Rust crates were read from the crates.io source paths inside those executables (83 confirmed) and
  completed from Velopack's `Cargo.lock` at the package commit (Windows targets, normal dependencies, no proc-macros:
  153 more, a superset).
* License texts — taken from the packages when present (.NET packs); otherwise from the upstream repository at the
  exact commit/tag recorded in the package (.nuspec `<repository commit>`; NAudio tag v2.2.1), from data embedded in the
  shipped assembly (PdfPig's AFM read-me and glyph-list headers, extracted from `UglyToad.PdfPig.Fonts.dll` manifest
  resources), and from the published `.crate` archives on crates.io. Where no text exists anywhere, the standard text
  was written with the holder from the package metadata and labelled as such.

Deliverables: `licenses/` (one file per component), `THIRD_PARTY_NOTICES.md` (index), `scripts/build-release.ps1`
copies both into the package and fails without them, `scripts/package-rc.ps1` re-checks them before zipping,
`LicenseComplianceTests` guards the index in CI.

## Distributed components

| Component | Version | License | Closed-source commercial distribution | Obligations | How they are met |
|---|---|---|---|---|---|
| .NET runtime (Microsoft.NETCore.App runtime pack + single-file host) | 10.0.13 (verify per build) | MIT + third-party notices (MIT, BSD-style, zlib, Unicode, Apache-2.0, public domain, …) | **Yes** — permissive; no GPL/LGPL/MPL/EPL text appears in the notices | Keep copyright + permission notice; pass on third-party notices | `licenses/dotnet-runtime-LICENSE.txt` and `licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt`, byte-for-byte copies from the runtime pack |
| Windows Desktop runtime (WPF profile, incl. DPAPI ProtectedData and native WPF/D3D DLLs) | 10.0.13 (verify per build) | MIT (pack license expression); WPF/WinForms notices: zlib, MIT (Json.NET), BSD-3-Clause (Ookii.Dialogs) | **Yes** — Microsoft publishes the pack under MIT for redistribution in self-contained apps | Keep notices | `licenses/windowsdesktop-runtime-LICENSE.txt` (verbatim from pack) and `licenses/windowsdesktop-runtime-THIRD-PARTY-NOTICES.txt` (verbatim WPF + WinForms notices from the pack's source commit; the pack contains none) |
| NAudio.Core | 2.2.1 | MIT | **Yes** | Keep notice | `licenses/NAudio.txt` (license.txt at tag v2.2.1; package has no file) |
| NAudio.Wasapi | 2.2.1 | MIT | **Yes** | Keep notice | `licenses/NAudio.txt` |
| PdfPig | 0.1.16 | Apache-2.0 (+ BSD-style PDFBox/FontBox, Adobe CMaps BSD-3-Clause) | **Yes** — Apache-2.0 has no copyleft | Provide license copy; keep attributions; NOTICE if the Work has one; mark modifications | `licenses/PdfPig.txt`: full LICENSE at commit a7bb356; the project has no NOTICE (package and repo checked); binaries unmodified |
| — Adobe Core-14 AFM font metrics (embedded in UglyToad.PdfPig.Fonts.dll) | 1997 AFM set | Adobe AFM terms | **Yes** — "for any purpose and without charge" | Keep copyright notices; never ship the AFM files without the read-me; mark modifications | Shipped unmodified inside the assembly with the embedded read-me; terms and every copyright/`Notice` line reproduced in `licenses/PdfPig.txt` |
| — Adobe Glyph List / Zapf Dingbats list (embedded) | AGL 2.0 | BSD-3-Clause | **Yes** | Reproduce notice + disclaimer in documentation; no endorsement | Headers reproduced verbatim in `licenses/PdfPig.txt` |
| — Additional glyph mappings from Apache PDFBox (embedded) | — | Apache-2.0 | **Yes** | Include NOTICE of the Work | Apache PDFBox NOTICE reproduced (current trunk; revision **verify**) |
| Velopack library (Velopack.dll) | 1.2.161 | MIT | **Yes** | Keep notice | `licenses/Velopack.txt` (LICENSE at commit 92d6a1c; package has no file) |
| Velopack native binaries (Setup stub, Update.exe, launcher stub) | vpk 1.2.161 | MIT | **Yes** | Keep notice | `licenses/Velopack.txt` |
| Rust crates statically linked into those binaries | 236 crates (83 confirmed + 153 graph superset) | MIT / Apache-2.0 / ISC / BSD-2-Clause / BSD-3-Clause / Unicode-3.0 / Zlib / CDLA-Permissive-2.0; multi-licensed crates elected to MIT | **Yes** — no copyleft relied upon; the only GPL options (zstd: BSD OR GPL-2.0, self_cell: Apache-2.0 OR GPL-2.0-only) are not elected | Keep each notice; Apache-2.0: license copy; CDLA-Permissive-2.0 (webpki-roots data): license text | `licenses/Velopack-native-THIRD-PARTY-NOTICES.txt`: per crate declared license, elected license, authors, repository and the license files from the crate archive, plus the full Apache-2.0 text |
| Rust standard library (in the native binaries) | toolchain used by Velopack | MIT OR Apache-2.0 | **Yes** | Keep notice | MIT elected; Rust project MIT text reproduced (normpath entry) |

Not redistributed: Segoe UI / Segoe Fluent Icons (referenced by name, provided by Windows), WASAPI and DPAPI system
services. `knowledge\`, `cloud.json`, `User Guide.txt` and `app.ico` are first-party content (their authorship was
not part of this audit).

Copyleft summary: **no GPL/LGPL/AGPL/MPL/EPL code is shipped or relied upon.** The WiX fork in vpk (MS-RL) is used
only for MSI output, which this project does not build, and is never copied into the package.

## Backend and tooling (not distributed)

The backend runs as a hosted service; its packages (ASP.NET Core JwtBearer 10.0.12, EF Core 10.0.12 + design-time
Roslyn/Humanizer/Mono.TextTemplating, Npgsql 10.0.3 under the PostgreSQL License, Microsoft.IdentityModel 8.19.2, …)
are listed in `THIRD_PARTY_NOTICES.md` §2 and are all permissive. Should the Docker image ever be handed to a
customer, the same notice duty applies (the base images carry Debian package licenses). Build/test tools (vpk,
dotnet-ef, xunit, test SDK, coverlet) are listed in §3 and never ship.

## Deprecated, outdated and unnecessary dependencies

| Item | Finding | Recommendation |
|---|---|---|
| xunit 2.9.3 / xunit.runner.visualstudio 3.1.4 | Test-only. xunit 2.x is in maintenance; the current line is xunit.v3 (4.0.2 on nuget.org) and runner 4.0.1 | Migrate after release; no license or distribution impact |
| Microsoft.NET.Test.Sdk 17.14.1, coverlet.collector 6.0.4 | Test-only; 18.11.0 and 10.1.0 are current | Upgrade with the xunit migration |
| NAudio.Core / NAudio.Wasapi 2.2.1 | Shipped. Released 2023; nuget.org now lists NAudio.Core 3.2.0 and NAudio.Wasapi 22.0.0 (unusual version jump — **verify** it is the same project and still MIT before upgrading) | Keep 2.2.1 for rc.2 (stable, MIT); evaluate 3.x afterwards |
| NAudio.Core explicit `PackageReference` | Already a dependency of NAudio.Wasapi 2.2.1 | Redundant but harmless (pins the version); may be removed |
| PdfPig 0.1.16 | Pre-1.0 version number, but the latest stable release | Keep |
| Velopack / vpk 1.2.161 | Latest stable | Keep |
| .NET runtime packs 10.0.13 | Latest 10.0 patch on nuget.org at audit time | CI must keep pulling the newest 10.0.x patch (security fixes) and refresh the runtime notice files when the patch changes |
| Backend EF Core / Mvc.Testing 10.0.12, TimeProvider.Testing 10.10.0 | One patch/minor behind (10.0.13, 10.11.0) | Not distributed; upgrade in the normal patch cycle |
| PdfPig reaching the backend via InterviewAssistant.Core | Server-side PDF extraction uses it too | No distribution impact |

No shipped dependency is unnecessary: NAudio (WASAPI loopback capture), PdfPig (CV / job-description PDF text),
Velopack (install hooks and updates) are each used by production code paths (`AudioCaptureService`,
`DocumentTextExtractor`, `Program`).

## Verify items (open)

1. **Runtime patch per release build** — the runtime license files are from the 10.0.13 packs; CI (`setup-dotnet`
   10.0.x) may build with a newer patch. Check `obj/project.assets.json` → `downloadDependencies` in the release job
   and refresh `licenses/dotnet-runtime-*` and `licenses/windowsdesktop-runtime-*` from the packs if it changed.
2. **Velopack native crate list** — 153 of 236 crates come from the dependency graph and may not all be linked; and a
   crate without any source path *and* outside the graph walk would be missed. Confirm with cargo-about on
   velopack@92d6a1c for x86_64/i686-pc-windows-msvc with vpk's build features.
3. **Holders for crates without license file or authors** — webview2-com-sys 0.39.1 and zune-inflate 0.2.54 (standard
   MIT text written with a generic holder). webview2-com-sys may also embed Microsoft's WebView2 loader; confirm
   whether that applies to the binaries vpk ships.
4. **Apache PDFBox NOTICE revision** for PdfPig's embedded `GlyphList.additional` (current trunk NOTICE reproduced).
5. **WindowsDesktop third-party notices** come from the source commit, not the pack (the pack has none).
6. **Which vendor executables vpk selects for `--runtime win-x64`** (`update.exe` vs `update_x64.exe`, `setup.exe` vs
   `setup_x64.exe`, `stub.exe` vs `stub_x64.exe`) — all six were scanned, so the notices cover either choice.
7. **xunit.abstractions 2.0.3** (test-only) declares its license by URL only.

## Release checklist impact

`RC2_COMMERCIALIZATION_READINESS.md` "Dependency licenses": full license texts and the .NET runtime notices now ship
with the package; remaining items are the verify list above (none blocks redistribution under the licenses as found).

## rc.4

Re-reviewed for 2.1.0-rc.4: **no package added, removed or upgraded** (only the app version changed in the project
files). New test-only assets: 16 additional generated audio fixtures in `tests/fixtures/audio/` (CC0, produced by
`scripts/generate-audio-fixtures.py` from eSpeak NG output and first-principles synthesis; not shipped). The shipped
`licenses/` folder and `THIRD_PARTY_NOTICES.md` are unchanged.

## rc.5

Re-reviewed for 2.1.0-rc.5: **no package added, removed or upgraded** — every project's PackageReference set is the
same as in rc.4 (`git diff a0bc946 -- '*.csproj'` changes only the app version). The new rc.5 code (pitch-run
tracking, follow-up intent, readiness model, design-system controls, readable exports, the `.iaprofile` backup format
with its ZIP/SHA-256 handling) uses only the .NET base class library and WPF. New test-only assets: 14 generated
flat-intonation speech fixtures in `tests/fixtures/audio/speech_fu_*` (CC0, produced by
`scripts/generate-audio-fixtures.py` from eSpeak NG output with SSML prosody ranges; not shipped — `package-rc.ps1`
refuses anything under `tests\`). The shipped `licenses/` folder and `THIRD_PARTY_NOTICES.md` are unchanged.
