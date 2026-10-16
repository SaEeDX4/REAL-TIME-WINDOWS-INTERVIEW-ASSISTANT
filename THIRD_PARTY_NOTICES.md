# Third-Party Notices & License Review

Interview Assistant for Windows includes third-party software. This file is the index: for every component it
names the exact version, the license, the copyright holder, where the source is, and the file in `licenses\` that
holds the full license text and notices. Both this file and the `licenses\` folder ship next to
`InterviewAssistant.exe` in the portable ZIP and in the installed app (`scripts/build-release.ps1` fails the build
if either is missing). The detailed review is in `docs/release/RC2_DEPENDENCY_LICENSE_AUDIT.md` (source repository).

Anything marked **verify** could not be confirmed from the packages themselves and must be checked before a
public release; nothing in this file is written from memory.

## 1. Shipped in the desktop app (portable ZIP and installer)

The app is published self-contained for win-x64 (`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`),
so the .NET and Windows Desktop runtimes are redistributed with it. The NuGet closure of the shipped projects
(InterviewAssistant.App, .Core, .Client, .Contracts; verified with `dotnet list package --include-transitive` and
`obj/project.assets.json` for net10.0-windows/win-x64) is exactly NAudio.Core, NAudio.Wasapi, Velopack and PdfPig;
none of them pulls further packages on net10.0.

| Component | Exact version | License (SPDX) | Copyright holder | Source | File in `licenses/` | Used for |
|---|---|---|---|---|---|---|
| .NET runtime: Microsoft.NETCore.App (runtime pack `Microsoft.NETCore.App.Runtime.win-x64`, single-file host) | 10.0.13 ¹ | MIT, plus the third-party components listed in its notices (MIT, BSD, zlib, Unicode, Apache-2.0, …) | .NET Foundation and Contributors | https://github.com/dotnet/dotnet (commit 4099f83977f4d29604a9aeb971fb0a7bc1d5f699) | `licenses/dotnet-runtime-LICENSE.txt`, `licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt` | Platform |
| Windows Desktop runtime: Microsoft.WindowsDesktop.App, WPF profile (runtime pack `Microsoft.WindowsDesktop.App.Runtime.win-x64`; includes DPAPI `ProtectedData` and the native `wpfgfx_cor3`, `PresentationNative_cor3`, `PenImc_cor3`, `D3DCompiler_47_cor3`, `vcruntime140_cor3` DLLs) | 10.0.13 ¹ | MIT (pack license); WPF/WinForms third-party notices: zlib, MIT (Json.NET), BSD-3-Clause (Ookii.Dialogs) | .NET Foundation and Contributors | https://github.com/dotnet/dotnet (same commit) | `licenses/windowsdesktop-runtime-LICENSE.txt`, `licenses/windowsdesktop-runtime-THIRD-PARTY-NOTICES.txt` ² | UI, local encryption at rest |
| NAudio.Core | 2.2.1 | MIT | Mark Heath | https://github.com/naudio/NAudio | `licenses/NAudio.txt` | Audio primitives |
| NAudio.Wasapi | 2.2.1 | MIT | Mark Heath | https://github.com/naudio/NAudio | `licenses/NAudio.txt` | WASAPI loopback capture |
| PdfPig (UglyToad.PdfPig*.dll) | 0.1.16 | Apache-2.0; embedded data: Adobe AFM terms, BSD-3-Clause (Adobe Glyph List, PDFBox/FontBox-derived code, Adobe CMaps) | UglyToad and PdfPig contributors; Adobe Systems Incorporated (embedded font metrics/glyph lists); www.pdfbox.org / The Apache Software Foundation (derived code and mappings) | https://github.com/UglyToad/PdfPig (commit a7bb35662bbbf405efddad50aedc9bcdcf515afc) | `licenses/PdfPig.txt` | PDF text extraction (CV/job description upload) |
| Velopack (library, Velopack.dll) | 1.2.161 | MIT | Caelan Sayler; Velopack Ltd. | https://github.com/velopack/velopack (commit 92d6a1c91716729d449034df5c50307dcce39493) | `licenses/Velopack.txt` | Install hooks, delta updates, channels |
| Velopack native binaries added by vpk at packaging: installer stub (`InterviewAssistant-Setup.exe`), `Update.exe`, launcher stub | 1.2.161 (vpk) | MIT | Caelan Sayler; Velopack Ltd. | same commit | `licenses/Velopack.txt` | Installer, updater |
| Rust crates statically linked into those native binaries (236 listed: 83 confirmed in the binaries + 153 from the Windows dependency graph; e.g. rustls, ring, ureq, zip, zstd, image, chrono, regex, clap) and the Rust standard library | per crate, listed in the file | MIT (elected where multi-licensed), Apache-2.0, ISC, BSD-2-Clause, BSD-3-Clause, Unicode-3.0, Zlib, CDLA-Permissive-2.0 | per crate, listed in the file | crates.io | `licenses/Velopack-native-THIRD-PARTY-NOTICES.txt` ³ | Inside Setup.exe / Update.exe |

¹ 10.0.13 is the runtime pack resolved by SDK 10.0.402 (the version these files were taken from). CI installs the
latest `10.0.x` SDK, which may select a newer patch: **verify** the runtime pack version in each release build
(`obj/project.assets.json` → `downloadDependencies`) and refresh the two `dotnet-runtime-*` and
`windowsdesktop-runtime-*` files from that pack if it changed.
² The Windows Desktop runtime pack contains only its LICENSE; its third-party notices were taken verbatim from the
WPF and Windows Forms `THIRD-PARTY-NOTICES.TXT` in the pack's source commit.
³ Confirmed crates were read from the source paths compiled into the binaries; the others come from Velopack's
`Cargo.lock` (Windows targets, normal dependencies, no proc-macros) and form a superset. **Verify** with cargo-about on
the Velopack repository at the commit above (see the header of that file).

Not redistributed (provided by Windows): Segoe UI / Segoe Fluent Icons fonts, WASAPI, the DPAPI system service.
The `knowledge\` folder, `cloud.json`, `User Guide.txt` and the app icon are first-party content.

### Redistribution compliance

| License | Components | Obligation | How it is met |
|---|---|---|---|
| MIT | .NET runtime, Windows Desktop runtime, NAudio, Velopack, most Rust crates | Include the copyright notice and permission notice in all copies or substantial portions | Full notice and license text in `licenses/`, shipped with every copy (ZIP and installer) |
| Apache-2.0 | PdfPig; Rust crates fs_at, zopfli, pretty-bytes-rust, cvt, ring (BoringSSL-derived part); self_cell and imgref (Apache-2.0 elected) | Give recipients a copy of the license (4a); state changes in modified files (4b); keep copyright/attribution notices (4c); include the Work's NOTICE file, if any (4d) | Full Apache-2.0 text in `licenses/PdfPig.txt` and `licenses/Velopack-native-THIRD-PARTY-NOTICES.txt`; binaries are shipped unmodified (4b not triggered); attribution retained; PdfPig has no NOTICE file (absent from the package and from the repository at the package commit); the Apache PDFBox NOTICE is reproduced for the embedded PDFBox mappings |
| BSD-3-Clause / BSD-2-Clause (and BSD-style) | Adobe Glyph List / Zapf Dingbats list, PDFBox/FontBox-derived code and Adobe CMaps in PdfPig; Rust crates zstd/zstd-safe/zstd-sys (Zstandard), sha1_smol, subtle, exr, lebe, ravif, avif-serialize, moxcms/pxfm (BSD elected), rav1e, av1-grain, v_frame; Ookii.Dialogs (WinForms notices) | Binary redistribution must reproduce the copyright notice, conditions and disclaimer in the documentation; no endorsement using the authors' names | Notices reproduced verbatim in `licenses/`; the product does not use these names for promotion |
| Adobe AFM terms | 14 Core-font AFM files embedded in UglyToad.PdfPig.Fonts.dll | Keep all copyright notices; do not distribute the AFM files without the accompanying read-me; note modifications | Shipped unmodified inside the PdfPig assembly together with its embedded read-me; terms and notices reproduced in `licenses/PdfPig.txt` |
| ISC, Unicode-3.0, Zlib, CDLA-Permissive-2.0 | ring, rustls-webpki, untrusted, libloading; icu_* / tinystr / zerovec / yoke / litemap / writeable and friends; zlib-rs; webpki-roots (root-certificate data); zlib in WPF | Keep the copyright and permission notice; CDLA-Permissive-2.0: provide the license text with the data | Reproduced in `licenses/` |
| Dual / multi-licensed (MIT OR Apache-2.0, Unlicense OR MIT, …) | Rust crates, Rust standard library | Comply with one of the offered licenses | MIT elected and reproduced; GPL alternatives never elected (Zstandard: BSD-3-Clause; self_cell: Apache-2.0) |

## 2. Backend only (not distributed in the desktop package)

Runs as a hosted service (Docker image built from `src/InterviewAssistant.Backend/Dockerfile`); none of these files
are given to customers. If the backend image is ever distributed, the same notice obligations apply to it.

| Component | Version | License | Commercial use | Used for |
|---|---|---|---|---|
| ASP.NET Core runtime (`mcr.microsoft.com/dotnet/aspnet` image) / .NET SDK image | 10.0 | MIT (.NET) + Debian package licenses | Yes | Backend container |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.12 | MIT | Yes | Supabase JWT validation |
| Microsoft.IdentityModel.* / System.IdentityModel.Tokens.Jwt (transitive) | 8.19.2 | MIT | Yes | JWT handling |
| Microsoft.EntityFrameworkCore (+ Relational, Abstractions, Analyzers) | 10.0.12 | MIT | Yes | Data access, migrations |
| Microsoft.EntityFrameworkCore.Design (PrivateAssets) and its transitive design-time packages: Microsoft.CodeAnalysis.* 5.0.0, Humanizer.Core 2.14.1, Mono.TextTemplating 3.0.0, Microsoft.Build.Framework 18.0.2, Microsoft.VisualStudio.SolutionPersistence 1.0.52, System.CodeDom 6.0.0, System.Composition 9.0.0, Newtonsoft.Json 13.0.4, Microsoft.Bcl.Cryptography 10.0.2 | 10.0.12 | MIT (all) | Yes | Migrations tooling |
| Npgsql.EntityFrameworkCore.PostgreSQL (+ Npgsql) | 10.0.3 | PostgreSQL License (permissive, MIT-like) | Yes | PostgreSQL provider |

## 3. Build and test tools (not distributed)

| Component | Version | License | Notes |
|---|---|---|---|
| .NET SDK | 10.0.x | MIT | Build only |
| vpk (Velopack CLI, .NET tool) | 1.2.161 | MIT | Build machine only; its vendor tools (signtool: Windows SDK license; zstd.exe 1.5.5: BSD-3-Clause/GPL-2.0; WiX fork: MS-RL, used only for MSI, which this project does not build; AppImage/mksquashfs: Linux packaging) are never copied into the package |
| dotnet-ef (local tool) | 10.0.12 | MIT | Migrations, migration bundles |
| xunit / xunit.core / xunit.assert / xunit.extensibility.* | 2.9.3 | Apache-2.0 | Tests |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | Tests |
| xunit.analyzers | 1.18.0 | Apache-2.0 | Tests |
| xunit.abstractions | 2.0.3 | Declared only by license URL (xunit license.txt, Apache-2.0) — verify | Tests |
| Microsoft.NET.Test.Sdk, Microsoft.TestPlatform.*, Microsoft.CodeCoverage | 17.14.1 | MIT | Tests |
| coverlet.collector | 6.0.4 | MIT | Coverage |
| Microsoft.AspNetCore.Mvc.Testing (+ Microsoft.AspNetCore.TestHost, Microsoft.Extensions.* 10.0.12) | 10.0.12 | MIT | Backend integration tests |
| Microsoft.Extensions.TimeProvider.Testing | 10.10.0 | MIT | Backend tests |
| Newtonsoft.Json (test host) | 13.0.3 | MIT | Tests |

## Open verification items

1. **Runtime patch** — the `dotnet-runtime-*` / `windowsdesktop-runtime-*` texts come from the 10.0.13 runtime packs;
   confirm the pack version of each release build and refresh the files if it differs (footnote ¹).
2. **Velopack native crates** — confirm the crate list with cargo-about at Velopack commit 92d6a1c (footnote ³);
   crates without a license file and without an authors field (webview2-com-sys, zune-inflate) need their holder
   confirmed.
3. **Apache PDFBox NOTICE** — the PDFBox revision the embedded glyph mappings came from is not recorded; the current
   NOTICE is reproduced in `licenses/PdfPig.txt` as a reference.
4. **Windows Desktop third-party notices** — taken from the pack's source commit, not from the pack itself (footnote ²).
5. **xunit.abstractions** (test only) — license declared by URL only.

## Policy

Every new dependency must be added here, with its license and commercial-distribution check, **and** — if it ships
in the desktop app — with its full license text in `licenses/` before merge (`LicenseComplianceTests` fails when a
`PackageReference` of the App, Core or Client project is not listed here or a referenced license file is missing).
No GPL/AGPL or paid-license components. PDF *generation* is intentionally not added: reports export as HTML
(printable to PDF by the OS) to avoid a commercial-license dependency (e.g. QuestPDF community licence limits).
