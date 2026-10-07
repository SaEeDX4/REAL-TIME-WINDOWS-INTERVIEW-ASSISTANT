# Third-Party Notices & License Review

| Component | Version | License | Commercial use | Used for |
|---|---|---|---|---|
| .NET runtime / ASP.NET Core / WPF | 10.0 | MIT | Yes | Platform |
| NAudio.Core / NAudio.Wasapi | 2.2.1 | MIT | Yes | WASAPI loopback capture |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT | Yes | DPAPI encryption |
| PdfPig (UglyToad.PdfPig) | 0.1.16 | Apache-2.0 | Yes (keep NOTICE/attribution) | PDF text extraction |
| xUnit, Microsoft.NET.Test.Sdk | 2.9 / 17.14 | Apache-2.0 / MIT | Test-only | Tests |

Policy: every new dependency must be added here with license and commercial-distribution check before merge. No GPL/AGPL or paid-license components. PDF *generation* is intentionally not added: reports export as HTML (printable to PDF by the OS) to avoid a commercial-license dependency (e.g. QuestPDF community licence limits).
