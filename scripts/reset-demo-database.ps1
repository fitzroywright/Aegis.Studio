$ErrorActionPreference = "Stop"

$studioDataPath = Join-Path $env:LOCALAPPDATA "Aegis.Studio"
$databasePath = Join-Path $studioDataPath "aegis-studio.db"
$walPath = "$databasePath-wal"
$shmPath = "$databasePath-shm"

Write-Host "Aegis.Studio demo database reset"
Write-Host "Data directory: $studioDataPath"

$paths = @($databasePath, $walPath, $shmPath)
foreach ($path in $paths)
{
    if (Test-Path $path)
    {
        Remove-Item $path -Force
        Write-Host "Deleted: $path"
    }
}

if (-not (Test-Path $studioDataPath))
{
    New-Item -ItemType Directory -Path $studioDataPath | Out-Null
}

Write-Host "Reset complete. Start Aegis.Studio Web + Service to recreate and reseed the database."
