param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK, then run this script again.' }
Push-Location $projectRoot
try {
    dotnet run --project tests/DraftLedger.Tests/DraftLedger.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed. Publishing stopped.' }
    dotnet run --project tests/DraftLedger.WpfTests/DraftLedger.WpfTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'WPF regression tests failed. Publishing stopped.' }
    dotnet publish src/DraftLedger.App/DraftLedger.App.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o "artifacts/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Copy-Item docs/USER-GUIDE.md "artifacts/$Runtime/USER-GUIDE.md" -Force
    Copy-Item docs/RELEASE-NOTES.md "artifacts/$Runtime/RELEASE-NOTES.md" -Force
    Copy-Item docs/VALIDATION.md "artifacts/$Runtime/VALIDATION.md" -Force
    Copy-Item docs/THIRD-PARTY-NOTICES.md "artifacts/$Runtime/THIRD-PARTY-NOTICES.md" -Force
    Copy-Item licenses "artifacts/$Runtime/licenses" -Recurse -Force
    Compress-Archive -Path "artifacts/$Runtime/*" -DestinationPath "artifacts/DraftLedger-$Runtime.zip" -Force
    Write-Host "Built: $projectRoot/artifacts/DraftLedger-$Runtime.zip"
} finally { Pop-Location }
