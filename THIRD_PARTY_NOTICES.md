# Third-Party Notices & License Review

| Component | Version | License | Commercial use | Used for |
|---|---|---|---|---|
| .NET runtime / ASP.NET Core / WPF | 10.0 | MIT | Yes | Platform |
| NAudio.Core / NAudio.Wasapi | 2.2.1 | MIT | Yes | WASAPI loopback capture |
| DPAPI (`ProtectedData`, built into the .NET 10 Windows Desktop framework) | 10.0 | MIT | Yes | Local encryption at rest |
| PdfPig (UglyToad.PdfPig) | 0.1.16 | Apache-2.0 | Yes (keep NOTICE/attribution) | PDF text extraction |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.12 | MIT | Yes | Backend: Supabase JWT validation |
| Microsoft.EntityFrameworkCore (+ Relational, Design) | 10.0.12 | MIT | Yes | Backend: data access, migrations |
| Npgsql.EntityFrameworkCore.PostgreSQL (+ Npgsql) | 10.0.3 | PostgreSQL License (permissive, MIT-like) | Yes | Backend: PostgreSQL provider |
| dotnet-ef (local tool) | 10.0.12 | MIT | Build-time only | Migrations, migration bundles |
| Docker base images mcr.microsoft.com/dotnet/sdk, aspnet | 10.0 | MIT (.NET) + Debian package licenses | Yes | Backend container |
| xUnit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, coverlet.collector | 2.9 / 3.1 / 17.14 / 6.0 | Apache-2.0 / Apache-2.0 / MIT / MIT | Test-only | Tests |
| Microsoft.AspNetCore.Mvc.Testing, Microsoft.Extensions.TimeProvider.Testing | 10.0.12 / 10.10.0 | MIT | Test-only | Backend integration tests |

Policy: every new dependency must be added here with license and commercial-distribution check before merge. No GPL/AGPL or paid-license components. PDF *generation* is intentionally not added: reports export as HTML (printable to PDF by the OS) to avoid a commercial-license dependency (e.g. QuestPDF community licence limits).
