param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    $flavor = if ($FrameworkDependent) { 'runtime-required' } else { 'self-contained' }
    $publish = Join-Path $PSScriptRoot "artifacts\LumaToolkit-$flavor"
    if (Test-Path $publish) { throw "Output folder already exists: $publish. Rename it or remove it before rebuilding." }
    dotnet publish src/Luma.Desktop/Luma.Desktop.csproj -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item README.md, TEST-RESULTS.md, THIRD-PARTY-NOTICES.md, LICENSE $publish
    Copy-Item licenses (Join-Path $publish 'licenses') -Recurse
    if (!$FrameworkDependent) {
        $packageRoot = ((dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
        $config = Get-Content (Join-Path $publish 'LumaToolkit.runtimeconfig.json') -Raw | ConvertFrom-Json
        $runtimeVersion = ($config.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
        $runtimePackage = Join-Path $packageRoot "microsoft.netcore.app.runtime.win-x64\$runtimeVersion"
        $desktopPackage = Join-Path $packageRoot "microsoft.windowsdesktop.app.runtime.win-x64\$runtimeVersion"
        Copy-Item (Join-Path $runtimePackage 'LICENSE.TXT') (Join-Path $publish 'licenses\dotnet-LICENSE.TXT')
        Copy-Item (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') (Join-Path $publish 'licenses\dotnet-THIRD-PARTY-NOTICES.TXT')
        Copy-Item (Join-Path $desktopPackage 'LICENSE') (Join-Path $publish 'licenses\WPF-LICENSE.txt')
    }
    if (!(Test-Path "$publish\LumaToolkit.exe") -or !(Test-Path "$publish\libvlc\win-x64\libvlc.dll")) { throw 'Required launch files are missing.' }
    Compress-Archive -Path $publish -DestinationPath "artifacts\LumaToolkit-win-x64-$flavor.zip"
    Write-Host "Portable ZIP created in artifacts. External FFmpeg/FFprobe are intentionally not bundled."
} finally { Pop-Location }
