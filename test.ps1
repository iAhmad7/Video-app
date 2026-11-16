param([string]$OutputDirectory = (Join-Path $PSScriptRoot ("artifacts\tests-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))))
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --project tests/Luma.Tests/Luma.Tests.csproj -c Release -- $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'One or more integration tests failed. Read test-results.txt in the test directory.' }
} finally { Pop-Location }
