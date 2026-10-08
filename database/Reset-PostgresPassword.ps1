<#
.SYNOPSIS
    Sets a new password for the PostgreSQL 'postgres' superuser when the current one is unknown
    (e.g. after an unattended install). Must be run as Administrator.

.DESCRIPTION
    1. Backs up pg_hba.conf and temporarily allows password-less connections from this machine only
       (127.0.0.1 and ::1; nothing else changes).
    2. Restarts the service and runs psql's \password, which prompts you for the new password
       without echoing it.
    3. Restores the original pg_hba.conf and restarts the service, even if a step fails.

.EXAMPLE
    Right-click PowerShell > Run as Administrator, then:
    powershell -ExecutionPolicy Bypass -File database\Reset-PostgresPassword.ps1
#>
#Requires -RunAsAdministrator
param(
    [string]$PgBin = "C:\Program Files\PostgreSQL\17\bin",
    [string]$DataDir = "C:\Program Files\PostgreSQL\17\data",
    [string]$ServiceName = "postgresql-x64-17"
)

$ErrorActionPreference = "Stop"

$hba = Join-Path $DataDir "pg_hba.conf"
$backup = "$hba.ers-backup"

Copy-Item $hba $backup -Force
try {
    $lines = Get-Content $backup | ForEach-Object {
        $_ -replace '^(host\s+all\s+all\s+(127\.0\.0\.1/32|::1/128)\s+)scram-sha-256', '$1trust'
    }
    # Write without a BOM so PostgreSQL parses the first line correctly.
    [IO.File]::WriteAllLines($hba, [string[]]$lines, (New-Object Text.UTF8Encoding $false))

    Write-Host "Temporarily allowing local connections without a password..."
    Restart-Service $ServiceName

    Write-Host "Choose the new password for the 'postgres' superuser:"
    & (Join-Path $PgBin "psql.exe") -h 127.0.0.1 -U postgres -d postgres -c "\password postgres"
    if ($LASTEXITCODE -ne 0) { throw "psql failed with exit code $LASTEXITCODE" }
}
finally {
    Copy-Item $backup $hba -Force
    Remove-Item $backup
    Restart-Service $ServiceName
    Write-Host "Original authentication settings restored."
}

Write-Host ""
Write-Host "Done. Now run database\Setup-Database.ps1 and enter the password you just chose."
