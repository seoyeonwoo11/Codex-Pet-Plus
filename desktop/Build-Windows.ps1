$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet run --project desktop/CodexPetPlus.Tests/CodexPetPlus.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Desktop core tests failed.' }
    dotnet run --project desktop/CodexPetPlus.Windows.Tests/CodexPetPlus.Windows.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Windows pet follower smoke test failed.' }
    $output = Join-Path $projectRoot 'dist/windows'
    if (Test-Path $output) { Remove-Item -Recurse -Force $output }
    dotnet publish desktop/CodexPetPlus.Windows/CodexPetPlus.Windows.csproj -c Release -p:PublishProfile=Portable -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Copy-Item desktop/START-HERE.ko.md (Join-Path $output 'START-HERE.ko.md')
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath (Join-Path $projectRoot 'dist/CodexPetPlus-Windows-x64.zip') -Force
    Write-Host 'Built dist/CodexPetPlus-Windows-x64.zip'
}
finally { Pop-Location }
