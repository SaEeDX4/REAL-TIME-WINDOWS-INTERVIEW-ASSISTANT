# Build

## Option A — download (no tools needed)
GitHub → **Actions** → latest **windows-release** run → artifact `InterviewAssistant-win-x64` (or push a tag `v1.0.0` to get a GitHub Release). Unzip, run `InterviewAssistant.exe`.

## Option B — build on Windows
Requirements: Windows 10/11 x64, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
```powershell
git clone <this repo>; cd <repo>
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
# → artifacts\InterviewAssistant-win-x64.zip  and  artifacts\InterviewAssistant\InterviewAssistant.exe
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1   # optional launch check
```
`-FolderLayout` publishes EXE + DLLs instead of a single file (fallback if antivirus dislikes single-file extraction). `-SkipTests` skips tests.

The output is **self-contained** (no .NET runtime install needed), ~66 MB single EXE + `knowledge\` folder.

## Option C — Linux/macOS cross-compile (how this release was produced)
The Core library and tests build anywhere with .NET 8. The WPF app cross-compiles with Microsoft's .NET SDK (distro source-built SDKs lack the WindowsDesktop targets):
```bash
dotnet test tests/InterviewAssistant.Tests -c Release
dotnet publish src/InterviewAssistant.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o artifacts/InterviewAssistant
```

## Stack
C# 12 / .NET 8 · WPF · NAudio 2.2.1 (WASAPI loopback) · System.Net.WebSockets · HttpClient SSE · DPAPI (System.Security.Cryptography.ProtectedData) · xUnit. Nullable enabled, warnings as errors.
