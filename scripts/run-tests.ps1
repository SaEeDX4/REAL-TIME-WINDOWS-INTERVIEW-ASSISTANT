<#
.SYNOPSIS  Runs the automated test suite (unit, engine, simulated 60-minute soak, answer-quality evaluation).
.NOTES     Set $env:OPENAI_API_KEY to also run the optional live LLM evaluation.
#>
$ErrorActionPreference = "Stop"
Set-Location (Resolve-Path (Join-Path $PSScriptRoot ".."))
dotnet test tests/InterviewAssistant.Tests -c Release --logger "console;verbosity=detailed"
if ($LASTEXITCODE) { throw "tests failed" }
