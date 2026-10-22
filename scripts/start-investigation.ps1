<#
.SYNOPSIS
  Start a read-only investigation session: SDK trade watcher + UIA recorder.
.DESCRIPTION
  Builds TradeWatch, then opens two windows that record into
  investigation/<timestamp>-<Name>/ (gitignored). Stop each with Ctrl+C.
#>
param(
    [string]$Name = "session",
    # Nested-request safety re-check while no trade is open (0 = events only).
    [double]$FallbackS = 2.0
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$session = Join-Path $root ("investigation\{0}-{1}" -f (Get-Date -Format "yyyyMMdd-HHmmss"), $Name)
New-Item -ItemType Directory -Force $session | Out-Null

dotnet build (Join-Path $root "src\TradeWatch") -c Release -v q
if (-not $?) { throw "TradeWatch build failed" }
$tradeWatch = Join-Path $root "src\TradeWatch\bin\Release\net10.0-windows7.0\TradeWatch.exe"

Start-Process -FilePath $tradeWatch -ArgumentList @("--out", "`"$session`"") -WorkingDirectory $root

$recorderDir = Join-Path $root "tools\uia_recorder"
$recorderArgs = @("run", "python", "-u", "uia_recorder.py", "--out", "`"$session`"", "--fallback-s", $FallbackS)
Start-Process -FilePath "uv" -ArgumentList $recorderArgs -WorkingDirectory $recorderDir

Write-Host "Recording to $session"
