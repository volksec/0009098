# =============================================================================
# Portal do Corretor — atalho para PowerShell.
# Delega ao start.sh, que contém a lógica única de bootstrap.
#
#   .\start.ps1            sobe o ambiente
#   .\start.ps1 -Reset     recria o banco do zero
#   .\start.ps1 -Stop      encerra os serviços
# =============================================================================
param([switch]$Reset, [switch]$Stop, [switch]$NoSeed)

$ErrorActionPreference = 'Stop'

$bash = @(
    "$env:ProgramFiles\Git\bin\bash.exe",
    "$env:ProgramFiles\Git\usr\bin\bash.exe",
    "${env:ProgramFiles(x86)}\Git\bin\bash.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $bash) {
    Write-Host "Git Bash nao encontrado. Instale o Git para Windows: https://git-scm.com/" -ForegroundColor Red
    exit 1
}

$argsList = @()
if ($Reset)  { $argsList += '--reset' }
if ($Stop)   { $argsList += '--stop' }
if ($NoSeed) { $argsList += '--no-seed' }

& $bash -lc "cd '$PSScriptRoot' && ./start.sh $($argsList -join ' ')"
exit $LASTEXITCODE
