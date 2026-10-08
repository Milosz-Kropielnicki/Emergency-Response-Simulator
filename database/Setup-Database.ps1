<#
.SYNOPSIS
    Creates the ers_app role and the ers / ers_test databases (with PostGIS) on a local PostgreSQL,
    and writes the connection strings to the git-ignored appsettings.Local.json.

.DESCRIPTION
    psql prompts for the superuser's password. The ers_app password is generated on first run and
    reused afterwards, so the script is safe to run again.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File database\Setup-Database.ps1
#>
param(
    [string]$PgBin = "C:\Program Files\PostgreSQL\17\bin",
    [string]$SuperUser = "postgres",
    [string]$PgHost = "localhost",
    [int]$Port = 5432
)

$ErrorActionPreference = "Stop"

$settingsPath = Join-Path $PSScriptRoot "..\Emergency Response Simulator\appsettings.Local.json"
$password = $null

if (Test-Path $settingsPath) {
    $existing = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $match = [regex]::Match([string]$existing.ConnectionStrings.Ers, "Password=([^;]+)")
    if ($match.Success) { $password = $match.Groups[1].Value }
}

if (-not $password) {
    # Hex keeps the password free of characters that need escaping in connection strings.
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = -join ($bytes | ForEach-Object { $_.ToString("x2") })

    $connection = "Host=$PgHost;Port=$Port;Username=ers_app;Password=$password"
    $settings = [ordered]@{
        ConnectionStrings = [ordered]@{
            Ers     = "$connection;Database=ers"
            ErsTest = "$connection;Database=ers_test"
        }
    }
    $settings | ConvertTo-Json -Depth 4 | Set-Content -Path $settingsPath -Encoding utf8
    Write-Host "Wrote $((Resolve-Path $settingsPath).Path)"
}

Write-Host "Connecting to PostgreSQL as '$SuperUser' (enter that user's password when prompted)..."
& (Join-Path $PgBin "psql.exe") -h $PgHost -p $Port -U $SuperUser -d postgres `
    -v ON_ERROR_STOP=1 -v "app_password=$password" -f (Join-Path $PSScriptRoot "setup.sql")
if ($LASTEXITCODE -ne 0) { throw "psql failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Database ready. Apply the schema with:"
Write-Host "  cd ""Emergency Response Simulator"""
Write-Host "  dotnet tool run dotnet-ef database update --project ""Emergency Response Simulator.Data"""
