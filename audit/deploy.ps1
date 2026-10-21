<#
.SYNOPSIS
  Installs the audited build (mod + launcher) on this PC and, with -Laptop, on the laptop over ssh.
  Refuses to do anything unless the newest FULL audit passed for exactly this build.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File audit\deploy.ps1 -Laptop
#>
param(
    [switch]$Laptop,
    [string]$LaptopHost = 'laptop',
    [string]$LaptopRoot = 'C:/Users/POINT/GrowHomeCoop',
    [string]$GameDir = (Join-Path $env:USERPROFILE 'GrowHomeCoop\game')
)
$ErrorActionPreference = 'Stop'
$AuditDir = $PSScriptRoot
$Root     = Split-Path $AuditDir -Parent
$ModDll   = Join-Path $Root 'coopmod\bin\Release\net35\CoopMod.dll'
$ExeFile  = Join-Path $Root 'exe\bin\Release\net48\GrowHomeCoop.exe'
function Say([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Sha([string]$p) { (Get-FileHash $p -Algorithm SHA256).Hash.ToLower() }

# 1. the gate
$report = Join-Path $AuditDir 'last-full-pass.md'
if (-not (Test-Path $report)) { Say 'No audit report yet: run audit\run-audit.ps1 first.' 'Red'; exit 2 }
$text = Get-Content $report -Raw
if ($text -notmatch 'AUDIT PASSED') { Say 'The last full audit report does not say AUDIT PASSED. Not deploying.' 'Red'; exit 2 }
if ($text -notmatch 'Mod sha256: `([0-9a-f]{64})`') { Say 'The audit report has no mod fingerprint. Not deploying.' 'Red'; exit 2 }
$audited = $Matches[1]
$built = Sha $ModDll
if ($audited -ne $built) { Say "The mod was rebuilt after the audit (audited $($audited.Substring(0,12)), built $($built.Substring(0,12))). Run the audit again." 'Red'; exit 2 }
Say "Audit passed for mod $($built.Substring(0,12)). Deploying." 'Green'

# 2. this PC
if (Get-Process GrowHome -ErrorAction SilentlyContinue) { Say 'Grow Home is running here. Close it first.' 'Red'; exit 2 }
$managed = Join-Path $GameDir 'GrowHome_Data\Managed'
Copy-Item $ModDll (Join-Path $managed 'CoopMod.dll') -Force
$installDir = Split-Path $GameDir -Parent
Copy-Item $ExeFile (Join-Path $installDir 'GrowHomeCoop.exe') -Force
foreach ($f in 'coop-selftest.txt', 'coop-selftest-result.txt', 'coop-bots.txt') { Remove-Item (Join-Path $GameDir $f) -ErrorAction SilentlyContinue }
$bundled = ([regex]::Match((Get-Content (Join-Path $Root 'exe\Program.cs') -Raw), 'const int BundledVersion = (\d+);')).Groups[1].Value    # the version the launcher carries inside itself
if ($bundled) { Set-Content (Join-Path $GameDir 'coop-version.txt') $bundled -Encoding ascii -NoNewline }
$ok = ((Sha (Join-Path $managed 'CoopMod.dll')) -eq $built) -and ((Sha (Join-Path $installDir 'GrowHomeCoop.exe')) -eq (Sha $ExeFile))
Say ("this PC: mod + launcher " + $(if ($ok) { 'installed and verified' } else { 'MISMATCH after copy' })) $(if ($ok) { 'Green' } else { 'Red' })
if (-not $ok) { exit 1 }

# 3. the laptop (it sometimes drops off the network, so every call is retried a few times)
function Invoke-Remote([string]$Kind, [string[]]$CmdArgs) {
    for ($try = 1; $try -le 5; $try++) {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try {
            if ($Kind -eq 'ssh') { $out = & ssh -o ConnectTimeout=10 -o BatchMode=yes @CmdArgs 2>&1 | Out-String }
            else { $out = & scp -o ConnectTimeout=10 -o BatchMode=yes @CmdArgs 2>&1 | Out-String }
            $code = $LASTEXITCODE
        } finally { $ErrorActionPreference = $prev }
        if ($code -eq 0) { return $out }
        Start-Sleep -Seconds 6
    }
    return $null
}
function Remote-Sha([string]$WinPath) {
    $o = Invoke-Remote 'ssh' @($LaptopHost, "certutil -hashfile `"$WinPath`" SHA256")
    if (-not $o) { return '' }
    $line = ($o -split "`r?`n" | Where-Object { $_ -match '^[0-9a-fA-F ]{64,}$' } | Select-Object -First 1)
    if (-not $line) { return '' }
    return $line.Replace(' ', '').Trim().ToLower()
}

if ($Laptop) {
    $alive = Invoke-Remote 'ssh' @($LaptopHost, 'hostname')
    if (-not $alive) { Say 'The laptop does not answer over ssh (it may be asleep). The build is installed here only.' 'Yellow'; exit 3 }
    $running = Invoke-Remote 'ssh' @($LaptopHost, 'tasklist /fi "imagename eq GrowHome.exe" /nh')
    if ($running -match 'GrowHome.exe') { Say 'Grow Home is running on the laptop. Not touching it.' 'Yellow'; exit 3 }
    $all = $true
    $rootWin = $LaptopRoot.Replace('/', '\')
    foreach ($copy in 'game', 'test-install') {
        $dirWin = "$rootWin\$copy"
        $exists = Invoke-Remote 'ssh' @($LaptopHost, "if exist `"$dirWin\GrowHome_Data\Managed`" (echo yes)")
        if (-not ($exists -match 'yes')) { continue }
        $sent = Invoke-Remote 'scp' @($ModDll, "${LaptopHost}:$LaptopRoot/$copy/GrowHome_Data/Managed/CoopMod.dll")
        $sum = Remote-Sha "$dirWin\GrowHome_Data\Managed\CoopMod.dll"
        $good = ($sum -eq $built); if (-not $good) { $all = $false }
        Say ("laptop $copy mod: " + $(if ($good) { 'verified' } else { "MISMATCH ($sum)" })) $(if ($good) { 'Green' } else { 'Red' })
    }
    $sent = Invoke-Remote 'scp' @($ExeFile, "${LaptopHost}:$LaptopRoot/GrowHomeCoop.exe")
    $exeSum = Remote-Sha "$rootWin\GrowHomeCoop.exe"
    $exeGood = ($exeSum -eq (Sha $ExeFile)); if (-not $exeGood) { $all = $false }
    Say ("laptop launcher: " + $(if ($exeGood) { 'verified' } else { "MISMATCH ($exeSum)" })) $(if ($exeGood) { 'Green' } else { 'Red' })
    foreach ($f in 'coop-selftest.txt', 'coop-selftest-result.txt', 'coop-bots.txt') { [void](Invoke-Remote 'ssh' @($LaptopHost, "del /q `"$rootWin\game\$f`" 2>nul & exit 0")) }
    if ($bundled) { foreach ($copy in 'game', 'test-install') { [void](Invoke-Remote 'ssh' @($LaptopHost, "if exist `"$rootWin\$copy\GrowHome_Data`" (<nul set /p =$bundled> `"$rootWin\$copy\coop-version.txt`")")) } }
    if (-not $all) { exit 1 }
}
Say 'Deployed. This is NOT published to GitHub: friends get it only when you say so.' 'Cyan'
exit 0
