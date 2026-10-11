param(
    [switch]$SkipMobile,
    [switch]$AndroidOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    Write-Host "=== AEGIS STUDIO WORKDAY SERVER / TESTS ==="
    dotnet restore Aegis.Studio.slnx
    if ($LASTEXITCODE -ne 0) { throw "Studio restore failed." }

    dotnet build Aegis.Studio.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Studio Release build failed." }

    dotnet test Aegis.Studio.slnx -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Studio Release tests failed." }

    if (-not $SkipMobile) {
        Write-Host "=== AEGIS STUDIO WORKDAY ANDROID ==="
        dotnet workload restore Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -p:TargetFramework=net10.0-android
        if ($LASTEXITCODE -ne 0) { throw "MAUI workload restore failed." }

        dotnet restore Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -p:TargetFramework=net10.0-android
        if ($LASTEXITCODE -ne 0) { throw "WorkDay Android restore failed." }

        dotnet build Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -f net10.0-android -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "WorkDay Android build failed." }

        if (-not $AndroidOnly -and $IsMacOS) {
            Write-Host "=== AEGIS STUDIO WORKDAY IOS ==="
            dotnet workload restore Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -p:TargetFramework=net10.0-ios
            if ($LASTEXITCODE -ne 0) { throw "MAUI iOS workload restore failed." }
            dotnet restore Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -p:TargetFramework=net10.0-ios
            if ($LASTEXITCODE -ne 0) { throw "WorkDay iOS restore failed." }
            dotnet build Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -f net10.0-ios -c Release --no-restore
            if ($LASTEXITCODE -ne 0) { throw "WorkDay iOS build failed." }
        }
    }

    Write-Host "WORKDAY BUILD/TEST PASS"
}
finally {
    Pop-Location
}
