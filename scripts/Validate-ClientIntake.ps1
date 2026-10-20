param([string]$PostgresConnection = $env:STUDIO_TEST_POSTGRES)
$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'Run intake restore/build/test validation on the MacBook.' }
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    if (-not $PostgresConnection) { throw 'Set STUDIO_TEST_POSTGRES to a disposable local PostgreSQL database. Do not use production.' }
    $env:STUDIO_TEST_POSTGRES = $PostgresConnection
    dotnet restore Aegis.Studio.slnx
    if ($LASTEXITCODE) { throw 'Restore failed.' }
    dotnet build Aegis.Studio.slnx --no-restore -m:1
    if ($LASTEXITCODE) { throw 'Build failed.' }
    dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-build -m:1
    if ($LASTEXITCODE) { throw 'Web tests failed.' }
    Push-Location Aegis.Studio.PublicSite
    try {
        npm install
        if ($LASTEXITCODE) { throw 'Public-site dependency restore failed.' }
        npm test
        if ($LASTEXITCODE) { throw 'Public-site tests failed.' }
    } finally { Pop-Location }
    Push-Location tests/ui
    try {
        npm ci
        if ($LASTEXITCODE) { throw 'UI dependency restore failed.' }
        node --test
        if ($LASTEXITCODE) { throw 'UI tests failed.' }
    } finally { Pop-Location }
} finally { Pop-Location }
