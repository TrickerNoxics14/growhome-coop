<#
.SYNOPSIS
  Grow Home Co-op audit. Builds the mod and the launcher, runs every critical check (see required-checks.txt),
  prints PASS / FAIL for each one and exits with code 1 if anything critical did not pass.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File audit\run-audit.ps1

.NOTES
  - Nothing here needs a second PC or a person: the mod's checks run inside the real game on this PC (it presses CONTINUE by itself,
    grows the plants, plays the network messages in and out, then closes), the launcher's checks run against a throw-away web server.
  - The game's saves, settings and mod file are backed up first and put back afterwards, even if the audit is interrupted.
  - -Quick   shorter plant growth (development only, a quick run is not a release gate)
  - -SkipGame / -SkipExe   leave that part out (the verdict then says PARTIAL)
  - -KeepBuild   leave the audited mod installed in the test copy afterwards (default: put the previous mod back)
  - -ForceClose  close a running Grow Home first (default: stop with a message, so a game you are playing is never killed)
  - -IgnoreLaptop  start the game here even if the laptop is playing it (default: wait, because Steam Family Sharing would knock the laptop out)
#>
param(
    [switch]$Quick,
    [switch]$SkipGame,
    [switch]$SkipExe,
    [switch]$KeepBuild,
    [switch]$ForceClose,
    [switch]$IgnoreLaptop,
    [string]$LaptopHost = 'laptop',
    [string]$GameDir = (Join-Path $env:USERPROFILE 'GrowHomeCoop\game'),
    [int]$GameTimeoutMinutes = 15
)

$ErrorActionPreference = 'Stop'
$AuditDir = $PSScriptRoot
$Root     = Split-Path $AuditDir -Parent
$ModProj  = Join-Path $Root 'coopmod\CoopMod.csproj'
$ModDll   = Join-Path $Root 'coopmod\bin\Release\net35\CoopMod.dll'
$ExeProj  = Join-Path $Root 'exe\GrowHomeCoop.csproj'
$ExeFile  = Join-Path $Root 'exe\bin\Release\net48\GrowHomeCoop.exe'
$Managed  = Join-Path $GameDir 'GrowHome_Data\Managed'
$Stamp    = Get-Date -Format 'yyyyMMdd-HHmmss'
$Report   = Join-Path $AuditDir "reports\$Stamp"
$Steam    = Join-Path ${env:ProgramFiles(x86)} 'Steam'
$OriginalAssemblySha = '8d99d51663061da95016e496116191850b6a5437250cfc38bb8576027b2aa40d'   # Grow Home Steam build 857617
New-Item -ItemType Directory -Force -Path $Report | Out-Null

$Checks = New-Object System.Collections.ArrayList
function Add-Check([string]$Id, [bool]$Ok, [string]$Text, [string]$Detail = '') {
    [void]$Checks.Add([pscustomobject]@{ Id = $Id; Ok = $Ok; Text = $Text; Detail = $Detail })
}
function Say([string]$Text, [string]$Color = 'Gray') { Write-Host $Text -ForegroundColor $Color }

function Read-Results([string]$Path) {
    if (-not (Test-Path $Path)) { return $false }
    foreach ($line in Get-Content $Path) {
        if ($line -match '^CHECK (\S+) (PASS|FAIL) (.*)$') {
            $text = $Matches[3]; $detail = ''
            $i = $text.IndexOf(' - ')
            if ($i -ge 0) { $detail = $text.Substring($i + 3); $text = $text.Substring(0, $i) }
            Add-Check $Matches[1] ($Matches[2] -eq 'PASS') $text $detail
        }
    }
    return $true
}

function Stop-Game {
    Get-Process GrowHome -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 30 -and (Get-Process GrowHome -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Milliseconds 800   # let Windows release the files
}

function Assert-GameClosed {
    if (Get-Process GrowHome -ErrorAction SilentlyContinue) {
        if ($ForceClose) { Say '  Grow Home is running: closing it (-ForceClose)' 'Yellow'; Stop-Game }
        else { Say 'Grow Home is running. Close it (or run again with -ForceClose) so the audit does not interfere with your game.' 'Red'; exit 2 }
    }
}

# Steam Family Sharing lets only one person play from a shared library at a time: starting Grow Home on this PC would knock the laptop out of its game.
# So the audit does not start the game here while the laptop is playing it. (If the laptop cannot be reached - for example it is at school - nothing can be known.)
function Assert-LaptopNotPlaying {
    if ($IgnoreLaptop) { return }
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $out = & ssh -o ConnectTimeout=5 -o BatchMode=yes $LaptopHost 'tasklist /fi "imagename eq GrowHome.exe" /nh' 2>&1 | Out-String; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $prev }
    if ($code -eq 0 -and $out -match 'GrowHome.exe') {
        Say 'The laptop is playing Grow Home right now. Starting the game here would knock it out (Steam Family Sharing allows one player at a time). Run the audit later, or use -IgnoreLaptop.' 'Red'
        exit 2
    }
    if ($code -ne 0) { Say '  (the laptop cannot be reached, so I cannot tell whether it is playing)' 'DarkGray' }
}

function Get-FileHashes([string]$Dir) {
    $map = @{}
    if (Test-Path $Dir) {
        Get-ChildItem $Dir -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            $map[$_.FullName.Substring($Dir.Length)] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        }
    }
    return $map
}

function Same-Hashes($a, $b) {
    if ($a.Count -ne $b.Count) { return $false }
    foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k) -or $b[$k] -ne $a[$k]) { return $false } }
    return $true
}

function Get-RealSaves {
    $map = @{}
    $ud = Join-Path $Steam 'userdata'
    if (Test-Path $ud) {
        foreach ($d in Get-ChildItem $ud -Directory -ErrorAction SilentlyContinue) {
            $g = Join-Path $d.FullName '323320'
            if (Test-Path $g) { foreach ($kv in (Get-FileHashes $g).GetEnumerator()) { $map[$g + $kv.Key] = $kv.Value } }
        }
    }
    return $map
}

function Get-OriginalAssemblySha {
    $p = Join-Path $Steam 'steamapps\common\GrowHome\GrowHome_Data\Managed\Assembly-CSharp.dll'
    if (Test-Path $p) { return (Get-FileHash $p -Algorithm SHA256).Hash.ToLower() }
    return ''
}

function Get-CrashFolders {
    $n = 0
    foreach ($dir in @($GameDir, (Join-Path $Steam 'steamapps\common\GrowHome'))) {
        if (Test-Path $dir) { $n += @(Get-ChildItem $dir -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}_\d{6}$' }).Count }
    }
    return $n
}

function Invoke-Build([string]$Project, [string]$Label) {
    Say "  building $Label ..."
    $keep = $ErrorActionPreference; $ErrorActionPreference = 'Continue'      # a warning on stderr must not abort the audit
    $out = & dotnet build $Project -c Release 2>&1 | Out-String
    $code = $LASTEXITCODE; $ErrorActionPreference = $keep
    $out | Set-Content (Join-Path $Report "build-$Label.txt")
    return ($code -eq 0)
}

# ---- 1. build ------------------------------------------------------------------------------------------------------------------------
Say "== Grow Home Co-op audit  $Stamp$(if ($Quick) { '  (QUICK: not a release gate)' })" 'Cyan'
Say "1/4 build"
$modBuilt = $false; $exeBuilt = $false
try {
    $modBuilt = Invoke-Build $ModProj 'mod'
    if ($modBuilt -and -not (Test-Path $ModDll)) { $modBuilt = $false }
    if ($modBuilt) {
        Copy-Item $ModDll (Join-Path $Root 'exe\CoopMod.dll') -Force          # the launcher carries the mod inside itself
        $exeBuilt = Invoke-Build $ExeProj 'exe'
        if ($exeBuilt -and -not (Test-Path $ExeFile)) { $exeBuilt = $false }
    }
} catch { Say "  build crashed: $($_.Exception.Message)" 'Red' }
$modHash = if ($modBuilt) { (Get-FileHash $ModDll -Algorithm SHA256).Hash.ToLower() } else { '' }
Add-Check 'BUILD-OK' ($modBuilt -and $exeBuilt) 'the mod and the launcher build without errors' $(if ($modBuilt -and $exeBuilt) { "mod sha256 $($modHash.Substring(0, 16))" } else { "mod built=$modBuilt exe built=$exeBuilt (see reports\$Stamp\build-*.txt)" })

# ---- 2. launcher checks --------------------------------------------------------------------------------------------------------------
Say "2/4 launcher (updater and patcher)"
$exeRan = $false
if ($SkipExe) { Say "  skipped (-SkipExe)" 'Yellow' }
elseif (-not $exeBuilt) { Say "  not run: the build failed" 'Red' }
else {
    Assert-GameClosed
    $exeOut = Join-Path $Report 'exe-result.txt'
    try {
        $p = Start-Process -FilePath $ExeFile -ArgumentList @('--audit', '--use-game', "`"$GameDir`"", '--audit-out', "`"$exeOut`"") -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Report 'exe-console.txt')
        if (-not $p.WaitForExit(120000)) { $p.Kill(); Say "  the launcher audit did not finish in 2 minutes" 'Red' }
        $exeRan = Read-Results $exeOut
    } catch { Say "  launcher audit crashed: $($_.Exception.Message)" 'Red' }
}

# ---- 3. the game ---------------------------------------------------------------------------------------------------------------------
Say "3/4 game (mod checks inside the real game)"
$gameRan = $false
if ($SkipGame) { Say "  skipped (-SkipGame)" 'Yellow' }
elseif (-not $modBuilt) { Say "  not run: the build failed" 'Red' }
elseif (-not (Test-Path (Join-Path $GameDir 'GrowHome.exe'))) { Say "  not run: no co-op copy of the game at $GameDir" 'Red' }
else {
    Assert-GameClosed
    Assert-LaptopNotPlaying
    $backup = Join-Path $AuditDir "work\backup-$Stamp"
    New-Item -ItemType Directory -Force -Path (Join-Path $backup 'files') | Out-Null
    $realBefore  = Get-RealSaves
    $asmBefore   = Get-OriginalAssemblySha
    $crashBefore = Get-CrashFolders
    $savesDir    = Join-Path $GameDir 'coop-saves'
    $savesBefore = Get-FileHashes $savesDir
    $smallBefore = @{}
    if (Test-Path $savesDir) { Copy-Item $savesDir (Join-Path $backup 'coop-saves') -Recurse -Force }
    Get-ChildItem $GameDir -File | Where-Object { $_.Length -lt 2MB } | ForEach-Object { Copy-Item $_.FullName (Join-Path $backup 'files') -Force; $smallBefore[$_.Name] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    $dllBackup = Join-Path $backup 'CoopMod.dll'
    Copy-Item (Join-Path $Managed 'CoopMod.dll') $dllBackup -Force
    $resultFile = Join-Path $GameDir 'coop-selftest-result.txt'
    $process = $null
    try {
        # a clean stage: no bots, friends, LAN lists or old results; fresh log
        foreach ($f in 'coop-bots.txt', 'coop-peers.txt', 'coop-peer.txt', 'coop-lan.txt', 'coop-autosync.txt', 'coop-noshare.txt', 'coopmod-steam.log', 'coop-selftest-result.txt', 'coop-selftest-result.tmp') {
            Remove-Item (Join-Path $GameDir $f) -ErrorAction SilentlyContinue
        }
        Copy-Item $ModDll (Join-Path $Managed 'CoopMod.dll') -Force
        Set-Content (Join-Path $GameDir 'coop-selftest.txt') $(if ($Quick) { 'quick' } else { 'audit' }) -Encoding ascii
        # start the game the way a friend does: open the launcher window; here it presses Play by itself. It must start the co-op copy, confirm that the mod loaded and close again.
        $launcherLog = Join-Path (Split-Path $GameDir -Parent) 'launcher.log'
        $logBefore = if (Test-Path $launcherLog) { @(Get-Content $launcherLog).Count } else { 0 }
        $launchClock = [Diagnostics.Stopwatch]::StartNew()
        $launcher = Start-Process -FilePath $ExeFile -ArgumentList @('--use-game', "`"$GameDir`"", '--no-update', '--auto-play', '--game-args', '"-screen-width 800 -screen-height 450"') -PassThru
        $null = $launcher.Handle                                    # without this PowerShell loses the exit code
        Say "  launcher window opened and started the game; it plays by itself now, please do not touch it"
        if (-not $launcher.WaitForExit(180000)) { try { $launcher.Kill() } catch { }; $launcherCode = -1 } else { $launcherCode = $launcher.ExitCode }
        $newLines = if (Test-Path $launcherLog) { @(Get-Content $launcherLog | Select-Object -Skip $logBefore) } else { @() }
        $launcherText = ($newLines -join ' | ')
        Copy-Item $launcherLog (Join-Path $Report 'launcher.log') -Force -ErrorAction SilentlyContinue
        $took = $null; $hit = $newLines | Where-Object { $_ -match 'outcome Loaded after (\d+) s' } | Select-Object -Last 1; if ($hit -and $hit -match 'outcome Loaded after (\d+) s') { $took = [int]$Matches[1] }
        Add-Check 'LAUNCH-VERIFIED' ($launcherCode -eq 0 -and $null -ne $took -and $took -le 60) 'the launcher window starts the co-op copy, confirms that the mod loaded and closes by itself' $(if ($launcherCode -eq 0 -and $null -ne $took) { "mod loaded $took s after the start (limit 60 s), window closed after $([int]$launchClock.Elapsed.TotalSeconds) s" } else { "launcher exit code $launcherCode; log: " + $launcherText })
        $process = Get-Process GrowHome -ErrorAction SilentlyContinue | Select-Object -First 1
        $clock = [Diagnostics.Stopwatch]::StartNew(); $lastShown = ''; $died = $false
        $modLog = Join-Path $GameDir 'coopmod-steam.log'
        while ($clock.Elapsed.TotalMinutes -lt $GameTimeoutMinutes) {
            if (Test-Path $resultFile) { break }
            if (-not $process -or $process.HasExited) { $died = $true; break }
            if (Test-Path $modLog) {
                $line = $null
                try { $line = Get-Content $modLog -Tail 40 -ErrorAction Stop | Where-Object { $_ -like 'selftest: *' } | Select-Object -Last 1 } catch { }
                if ($line -and $line -ne $lastShown) { $lastShown = $line; $short = if ($line.Length -gt 105) { $line.Substring(0, 105) } else { $line }; Say ("  [{0:mm\:ss}] {1}" -f $clock.Elapsed, $short) 'DarkGray' }
            }
            Start-Sleep -Seconds 2
        }
        $timedOut = -not (Test-Path $resultFile) -and -not $died
        $gameRan = Read-Results $resultFile
        Start-Sleep -Seconds 1
    }
    finally {
        Stop-Game
        # keep the evidence, then put everything back
        foreach ($f in 'coopmod-steam.log', 'coop-selftest-result.txt') { if (Test-Path (Join-Path $GameDir $f)) { Copy-Item (Join-Path $GameDir $f) (Join-Path $Report ('game-' + $f)) -Force } }
        $unityLog = Join-Path $GameDir 'GrowHome_Data\output_log.txt'
        if (Test-Path $unityLog) { Copy-Item $unityLog (Join-Path $Report 'unity-output_log.txt') -Force }
        Get-ChildItem $GameDir -File | Where-Object { $_.Length -lt 2MB } | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
        Get-ChildItem (Join-Path $backup 'files') -File | ForEach-Object { Copy-Item $_.FullName (Join-Path $GameDir $_.Name) -Force }
        if (Test-Path $savesDir) { Remove-Item $savesDir -Recurse -Force -ErrorAction SilentlyContinue }
        if (Test-Path (Join-Path $backup 'coop-saves')) { Copy-Item (Join-Path $backup 'coop-saves') $savesDir -Recurse -Force }
        if (-not $KeepBuild) { Copy-Item $dllBackup (Join-Path $Managed 'CoopMod.dll') -Force }
    }

    # script-side checks
    $logText = if (Test-Path (Join-Path $Report 'game-coopmod-steam.log')) { Get-Content (Join-Path $Report 'game-coopmod-steam.log') } else { @() }
    $loaded = [bool]($logText | Where-Object { $_ -like 'CoopMod loaded*' })
    $inWorld = [bool]($logText | Where-Object { $_ -like 'selftest: in the world*' })
    $crashAfter = Get-CrashFolders
    $why = @()
    if (-not $loaded) { $why += 'the mod never loaded' }
    if (-not $inWorld) { $why += 'never reached the world' }
    if ($died) { $why += 'the game closed by itself before the checks finished' }
    if ($timedOut) { $why += "no result after $GameTimeoutMinutes minutes" }
    if ($crashAfter -ne $crashBefore) { $why += 'a crash report folder appeared' }
    if (-not $gameRan) { $why += 'no result file' }
    Add-Check 'GAME-STARTS-MODDED' ($why.Count -eq 0) 'the modded game starts, loads the mod, reaches the world on its own and does not crash' ($why -join '; ')

    $bad = New-Object System.Collections.ArrayList
    foreach ($l in $logText) {
        if ($l -like 'selftest:*' -or $l.Contains('[test]')) { continue }
        if ($l -match '(?i)\b(failed|exception|error|rejected a bad message|rebuilding my copy)\b') { [void]$bad.Add($l) }
    }
    $unityPath = Join-Path $Report 'unity-output_log.txt'
    if (Test-Path $unityPath) {
        $u = @(Get-Content $unityPath)
        for ($i = 0; $i -lt $u.Count; $i++) {
            if ($u[$i] -match 'Exception' -and $u[$i] -notmatch '^\s+at ') {
                $end = [Math]::Min($i + 16, $u.Count - 1)
                $frame = $u[$i..$end] | Where-Object { $_ -match 'CoopMod' } | Select-Object -First 1
                if ($frame) { [void]$bad.Add("unity log: $($u[$i].Trim()) <- $($frame.Trim())") }
            }
        }
    }
    $badList = @($bad)
    Add-Check 'NO-UNEXPECTED-ERRORS' ($badList.Count -eq 0) 'the mod log and the Unity log contain no errors or exceptions from the mod' $(if ($badList.Count) { "$($badList.Count) lines, first: $($badList[0])" } else { '' })

    $realAfter = Get-RealSaves; $asmAfter = Get-OriginalAssemblySha
    Add-Check 'ORIGINAL-UNTOUCHED' ((Same-Hashes $realBefore $realAfter) -and $asmBefore -eq $asmAfter -and $asmAfter -eq $OriginalAssemblySha) 'the real Steam save files and the original Grow Home assembly are exactly as before' "$($realBefore.Count) real save files checked, original assembly sha $(if ($asmAfter) { $asmAfter.Substring(0, 12) } else { 'missing' })"

    $savesAfter = Get-FileHashes $savesDir
    $smallOk = $true
    foreach ($kv in $smallBefore.GetEnumerator()) {
        $p = Join-Path $GameDir $kv.Key
        if ($KeepBuild -and $kv.Key -eq 'CoopMod.dll') { continue }
        if (-not (Test-Path $p) -or (Get-FileHash $p -Algorithm SHA256).Hash -ne $kv.Value) { $smallOk = $false }
    }
    $strays = @(Get-ChildItem $GameDir -File | Where-Object { $_.Length -lt 2MB -and -not $smallBefore.ContainsKey($_.Name) })
    $dllRestored = $KeepBuild -or ((Get-FileHash (Join-Path $Managed 'CoopMod.dll') -Algorithm SHA256).Hash -eq (Get-FileHash $dllBackup -Algorithm SHA256).Hash)
    Add-Check 'AUDIT-RESTORED' ((Same-Hashes $savesBefore $savesAfter) -and $smallOk -and $strays.Count -eq 0 -and $dllRestored) "the test copy's saves, logs and settings are put back exactly" "$($savesBefore.Count) save files, $($smallBefore.Count) settings and log files, $($strays.Count) leftovers"
}

# ---- 4. verdict ----------------------------------------------------------------------------------------------------------------------
Say "4/4 verdict"
$required = @()
foreach ($line in Get-Content (Join-Path $AuditDir 'required-checks.txt')) {
    if ($line -match '^\s*#' -or $line.Trim() -eq '') { continue }
    $parts = $line.Split('|')
    $required += [pscustomobject]@{ Id = $parts[0].Trim(); Where = $parts[1].Trim(); Title = $parts[2].Trim() }
}
$rows = New-Object System.Collections.ArrayList
$failed = 0; $skipped = 0; $passed = 0
$scriptGameChecks = 'GAME-STARTS-MODDED', 'NO-UNEXPECTED-ERRORS', 'ORIGINAL-UNTOUCHED', 'AUDIT-RESTORED', 'LAUNCH-VERIFIED'
foreach ($r in $required) {
    $mine = @($Checks | Where-Object { $_.Id -eq $r.Id })
    $skip = ($SkipGame -and ($r.Where -eq 'game' -or $scriptGameChecks -contains $r.Id)) -or ($SkipExe -and $r.Where -eq 'exe')
    $detail = ''
    if ($mine.Count -eq 0) {
        if ($skip) { $status = 'SKIPPED'; $skipped++ } else { $status = 'NOT RUN'; $failed++ }
    } else {
        $bads = @($mine | Where-Object { -not $_.Ok })
        if ($bads.Count -gt 0) { $status = 'FAIL'; $failed++; $detail = ($bads | Select-Object -First 3 | ForEach-Object { $_.Text + $(if ($_.Detail) { ' - ' + $_.Detail } else { '' }) }) -join ' || ' }
        else { $status = 'PASS'; $passed++; $detail = "$($mine.Count) checks" }
    }
    [void]$rows.Add([pscustomobject]@{ Id = $r.Id; Where = $r.Where; Status = $status; Title = $r.Title; Detail = $detail })
}
$requiredIds = @($required | ForEach-Object { $_.Id })
$unlisted = @($Checks | Where-Object { -not $_.Ok -and ($requiredIds -notcontains $_.Id) })
foreach ($u in $unlisted) { $failed++; [void]$rows.Add([pscustomobject]@{ Id = $u.Id; Where = '?'; Status = 'FAIL'; Title = $u.Text; Detail = $u.Detail }) }

Say ''
foreach ($row in $rows) {
    $color = switch ($row.Status) { 'PASS' { 'Green' } 'SKIPPED' { 'Yellow' } default { 'Red' } }
    Say ("  {0,-8} {1,-22} {2}" -f $row.Status, $row.Id, $(if ($row.Status -eq 'PASS' -or $row.Status -eq 'SKIPPED') { $row.Title } else { $row.Title })) $color
    if ($row.Status -in 'FAIL', 'NOT RUN' -and $row.Detail) { Say ("           -> " + $row.Detail) 'Red' }
}
Say ''
$gate = (-not $Quick) -and $skipped -eq 0
if ($failed -eq 0 -and $gate) { $verdict = "AUDIT PASSED: all $passed critical checks passed" }
elseif ($failed -eq 0) { $verdict = "AUDIT PARTIAL: $passed passed, $skipped skipped$(if ($Quick) { ', quick run' }) - this is not a release gate" }
else { $verdict = "AUDIT FAILED: $failed of $($rows.Count) critical checks did not pass" }
Say $verdict $(if ($failed -eq 0 -and $gate) { 'Green' } elseif ($failed -eq 0) { 'Yellow' } else { 'Red' })

# report file
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Grow Home Co-op audit $Stamp")
[void]$md.AppendLine('')
[void]$md.AppendLine("**$verdict**")
[void]$md.AppendLine('')
[void]$md.AppendLine("Mod sha256: ``$modHash``  |  quick run: $Quick  |  game copy: ``$GameDir``")
[void]$md.AppendLine('')
[void]$md.AppendLine('| Result | Check | Where | What it proves |')
[void]$md.AppendLine('|---|---|---|---|')
foreach ($row in $rows) { [void]$md.AppendLine("| $($row.Status) | $($row.Id) | $($row.Where) | $($row.Title) |") }
$fails = @($rows | Where-Object { $_.Status -in 'FAIL', 'NOT RUN' })
if ($fails.Count) { [void]$md.AppendLine(''); [void]$md.AppendLine('## What failed'); foreach ($f in $fails) { [void]$md.AppendLine("- **$($f.Id)**: $($f.Detail)") } }
[void]$md.AppendLine(''); [void]$md.AppendLine('## Every individual check'); [void]$md.AppendLine('')
foreach ($c in $Checks) { [void]$md.AppendLine("- $(if ($c.Ok) { 'PASS' } else { 'FAIL' }) ``$($c.Id)`` $($c.Text)$(if ($c.Detail) { ' - ' + $c.Detail })") }
$md.ToString() | Set-Content (Join-Path $Report 'audit-report.md') -Encoding UTF8
Copy-Item (Join-Path $Report 'audit-report.md') (Join-Path $AuditDir 'latest-report.md') -Force
if ($failed -eq 0 -and $gate) { Copy-Item (Join-Path $Report 'audit-report.md') (Join-Path $AuditDir 'last-full-pass.md') -Force }   # what deploy.ps1 trusts: only a full run in which everything passed
Say "report: $(Join-Path $Report 'audit-report.md')" 'DarkGray'

# keep the disk tidy: the last 3 backups and the last 10 reports
foreach ($pair in @(@('work', 'backup-*', 3), @('reports', '2*', 10))) {
    $dir = Join-Path $AuditDir $pair[0]
    if (Test-Path $dir) { Get-ChildItem $dir -Directory -Filter $pair[1] | Sort-Object Name -Descending | Select-Object -Skip $pair[2] | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $(if ($failed -gt 0) { 1 } elseif (-not $gate) { 3 } else { 0 })     # 0 = full pass, 1 = something failed, 3 = partial or quick run (not a release gate)
