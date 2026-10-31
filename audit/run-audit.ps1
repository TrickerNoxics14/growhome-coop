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
  - -Quick -Groups clone,fx   QUICK AUDIT of only those groups (world, plants, party, net, clone, fx, ui, speedrun, session, movement, story, creatures, launcher): the game runs only the
    sections tagged with them (coopmod\SelfTest.cs), the verdict requires only the checks of those groups (audit\check-groups.txt),
    the launcher stage runs only with the group "launcher", and the launcher is not rebuilt when its sources did not change.
    -Groups implies -Quick. A quick run NEVER writes last-full-pass.md, so publish.ps1 and deploy.ps1 never accept it.
  - -SkipGame / -SkipExe   leave that part out (the verdict then says PARTIAL)
  - -KeepBuild   leave the audited mod installed in the test copy afterwards (default: put the previous mod back)
  - -ForceClose  close a running Grow Home first (default: stop with a message, so a game you are playing is never killed)
  - -IgnoreLaptop  start the game here even if the laptop is playing it (default: wait, because Steam Family Sharing would knock the laptop out)
#>
param(
    [switch]$Quick,
    [string[]]$Groups,
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

# quick audit groups (-Groups a,b or -Groups "a,b" both work); $QuickGroups = $null means every group
$KnownGroups = 'world', 'plants', 'party', 'net', 'clone', 'fx', 'ui', 'speedrun', 'session', 'movement', 'story', 'creatures', 'launcher'
$QuickGroups = $null
if ($Groups) {
    $QuickGroups = @($Groups | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ } | ForEach-Object { $_.Trim().ToLower() } | Select-Object -Unique)
    $unknown = @($QuickGroups | Where-Object { $KnownGroups -notcontains $_ })
    if ($unknown.Count -gt 0 -or $QuickGroups.Count -eq 0) { Write-Host "Unknown group(s): $($unknown -join ', '). Known groups: $($KnownGroups -join ', ')" -ForegroundColor Red; exit 2 }
    $Quick = $true                                                   # choosing groups is always a quick audit, never a release audit
}
$GameGroups   = if ($QuickGroups) { @($QuickGroups | Where-Object { $_ -ne 'launcher' }) } else { $null }
$ExeSkipped   = $SkipExe -or ($QuickGroups -and $QuickGroups -notcontains 'launcher')
$GameSkipped  = $SkipGame -or ($QuickGroups -and $GameGroups.Count -eq 0)
$QuickLabel   = 'QUICK AUDIT (not a release audit)'
$GroupsText   = if ($QuickGroups) { $QuickGroups -join ',' } else { 'all' }

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

# ---- the owner's real data: the Steam save, the kept co-op save and the game's registry values (PlayerPrefs) ------------------------
# Unity keeps the game's settings and launch counters in HKCU\Software\Reflections\Grow Home; every start of the game (the co-op copy too)
# changes some of them. The audit takes them as they are before anything runs, puts them back after the game stage and proves at the end
# that the whole run left them, the real save and save-keep exactly as they were (REAL-DATA-UNCHANGED).
$RegPath  = 'Software\Reflections\Grow Home'
$SaveKeep = Join-Path $env:USERPROFILE 'GrowHomeCoop\save-keep'
function Get-RegValues {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($RegPath)
    if ($null -eq $key) { return $null }
    try {
        $list = New-Object System.Collections.ArrayList
        foreach ($n in ($key.GetValueNames() | Sort-Object)) {
            [void]$list.Add([pscustomobject]@{ Name = $n; Kind = $key.GetValueKind($n).ToString(); Value = $key.GetValue($n, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) })
        }
        return [pscustomobject]@{ Values = @($list); SubKeys = @($key.GetSubKeyNames() | Sort-Object) }
    } finally { $key.Close() }
}
function Get-RegDigest($snap) {
    if ($null -eq $snap) { return 'absent' }
    $sb = New-Object System.Text.StringBuilder
    foreach ($v in $snap.Values) {
        $val = $v.Value
        $text = if ($val -is [byte[]]) { [BitConverter]::ToString($val) } elseif ($val -is [string[]]) { $val -join [string][char]1 } else { [string]$val }
        [void]$sb.Append($v.Name).Append('|').Append($v.Kind).Append('|').Append($text).Append("`n")
    }
    [void]$sb.Append('subkeys:' + ($snap.SubKeys -join ','))
    $sha = [Security.Cryptography.SHA256]::Create()
    return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sb.ToString()))) -replace '-', '').ToLower()
}
# put the values back as they were: changed ones rewritten with their own kind, new ones removed (only values; this key has no subkeys)
function Restore-RegValues($snap) {
    $now = Get-RegValues
    if ((Get-RegDigest $now) -eq (Get-RegDigest $snap)) { return 0 }
    if ($null -eq $snap) { return 0 }                                  # nothing was there before: nothing of the owner's to put back
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($RegPath)
    $n = 0
    try {
        $want = @{}; foreach ($v in $snap.Values) { $want[$v.Name] = $v }
        foreach ($name in $key.GetValueNames()) { if (-not $want.ContainsKey($name)) { $key.DeleteValue($name, $false); $n++ } }
        $have = @{}; if ($now) { foreach ($v in $now.Values) { $have[$v.Name] = $v } }
        foreach ($v in $snap.Values) {
            $h = $have[$v.Name]
            $same = $h -and $h.Kind -eq $v.Kind -and ((Get-RegDigest ([pscustomobject]@{ Values = @($h); SubKeys = @() })) -eq (Get-RegDigest ([pscustomobject]@{ Values = @($v); SubKeys = @() })))
            if (-not $same) { $key.SetValue($v.Name, $v.Value, [Microsoft.Win32.RegistryValueKind]$v.Kind); $n++ }
        }
    } finally { $key.Close() }
    return $n
}
$RealSnap = [pscustomobject]@{ Saves = (Get-RealSaves); Keep = (Get-FileHashes $SaveKeep); Reg = (Get-RegValues) }
$RealSnap | Add-Member -NotePropertyName RegDigest -NotePropertyValue (Get-RegDigest $RealSnap.Reg)
$RegBackupFile = Join-Path $Report 'registry-before.clixml'
try { if ($RealSnap.Reg) { $RealSnap.Reg | Export-Clixml -Path $RegBackupFile -Depth 4 } } catch { }
$RegPutBack = 0

# ---- the game processes: only the co-op copy may ever run (LAUNCH-ONLY-COOP in the launcher, ONLY-COOP-EXE here) -------------------
$CoopExe    = [IO.Path]::GetFullPath((Join-Path $GameDir 'GrowHome.exe'))
$RegularExe = [IO.Path]::GetFullPath((Join-Path $Steam 'steamapps\common\GrowHome\GrowHome.exe'))
$GameSeen = @{}; $GameSamples = 0
function Watch-GameExes {
    foreach ($gp in @(Get-Process GrowHome -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $gp.Path } catch { }
        if (-not $path) { try { $path = (Get-CimInstance Win32_Process -Filter "ProcessId=$($gp.Id)" -ErrorAction Stop).ExecutablePath } catch { } }
        $k = if ($path) { [IO.Path]::GetFullPath($path) } else { "(pid $($gp.Id): path not readable)" }
        $script:GameSeen[$k] = 1 + $(if ($script:GameSeen.ContainsKey($k)) { $script:GameSeen[$k] } else { 0 })
        $script:GameSamples++
    }
}

# ---- crashes and leftovers -----------------------------------------------------------------------------------------------------------
$RunStart = Get-Date
function Get-CrashEvents([datetime]$Since) {
    $out = @()
    try {
        $ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $Since } -ErrorAction Stop | Where-Object { 'Application Error', '.NET Runtime', 'Application Hang', 'Windows Error Reporting' -contains $_.ProviderName })
        foreach ($e in $ev) {
            $m = [string]$e.Message
            if ($m -match '(?i)\bGrowHome(Coop)?\.exe\b') { $out += ('{0:HH:mm:ss} {1} {2}: {3}' -f $e.TimeCreated, $e.ProviderName, $e.Id, (($m -replace '\s+', ' ').Substring(0, [Math]::Min(160, ($m -replace '\s+', ' ').Length)))) }
        }
    } catch { if ($_.Exception.Message -notmatch 'No events were found') { $out += "the event log could not be read: $($_.Exception.Message)" } }
    return $out
}
# what an interrupted or crashed launcher audit can leave: fake games (ping.exe copies) in its throw-away folders, and its own process
function Stop-AuditStrays {
    $tmpRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    foreach ($gp in @(Get-Process GrowHome, ping -ErrorAction SilentlyContinue)) {
        $path = $null; try { $path = $gp.Path } catch { }
        if ($path -and $path.StartsWith($tmpRoot, [StringComparison]::OrdinalIgnoreCase) -and $path -match 'growhome-coop-') { try { $gp.Kill(); Say "  closed a leftover fake game $path" 'Yellow' } catch { } }
    }
    foreach ($lp in @(Get-Process GrowHomeCoop -ErrorAction SilentlyContinue)) {
        $path = $null; try { $path = $lp.Path } catch { }
        if ($path -and [IO.Path]::GetFullPath($path) -eq [IO.Path]::GetFullPath($ExeFile)) { try { $lp.Kill(); Say "  closed a leftover audited launcher (pid $($lp.Id))" 'Yellow' } catch { } }
    }
}

# ---- is the mod's result file whole? (RESULT-COMPLETE; the scorer is tested on made-up texts first) ---------------------------------
function Test-ResultText([string[]]$Lines, [bool]$NeedSender) {
    $p = New-Object System.Collections.ArrayList
    if (-not $Lines -or $Lines.Count -eq 0) { [void]$p.Add('no result file'); return @($p) }
    $body = @($Lines | Where-Object { $_.Trim() -ne '' })
    if ($body.Count -eq 0 -or $body[$body.Count - 1].Trim() -ne 'DONE') { [void]$p.Add('cut off: no DONE at the end') }
    $res = $body | Where-Object { $_ -match '^RESULT pass=(\d+) fail=(\d+)' } | Select-Object -First 1
    $checks = @($body | Where-Object { $_ -match '^CHECK \S+ (PASS|FAIL) ' }).Count
    if (-not $res) { [void]$p.Add('no RESULT line') }
    elseif ($res -match '^RESULT pass=(\d+) fail=(\d+)') { if ([int]$Matches[1] + [int]$Matches[2] -ne $checks) { [void]$p.Add("RESULT counts $([int]$Matches[1] + [int]$Matches[2]) checks but $checks CHECK lines are there") } }
    if ($checks -eq 0) { [void]$p.Add('not a single check') }
    $open = @{}
    foreach ($l in $body) {
        if ($l -match '^SECTION (.+) BEGIN$') { $open[$Matches[1]] = 1 }
        elseif ($l -match '^SECTION (.+) END \d+$') { $open.Remove($Matches[1]) }
        elseif ($l -match '^SECTION (.+) ABORTED') { [void]$p.Add("section $($Matches[1]) stopped half way"); $open.Remove($Matches[1]) }
    }
    foreach ($k in $open.Keys) { [void]$p.Add("section $k never ended") }
    $senders = 0
    foreach ($l in $body) {
        if ($l -match '^SENDER (\S+) (\d+) (\d+)$') { $senders++; if ([uint64]$Matches[3] -le [uint64]$Matches[2]) { [void]$p.Add("sender $($Matches[1]) never advanced ($($Matches[2]) -> $($Matches[3]))") } }
    }
    if ($NeedSender -and $senders -eq 0) { [void]$p.Add('no network sender was reported') }
    if (-not ($body | Where-Object { $_ -like 'NOTE scenario complete*' })) { [void]$p.Add('the scenario never reached its end') }
    return @($p)
}
# The groups whose live sections report a network SENDER, from the "# senders:" line of check-groups.txt (one place for the rule and the tags).
function Get-SenderGroups {
    $gf = Join-Path $AuditDir 'check-groups.txt'
    if (Test-Path $gf) { foreach ($line in Get-Content $gf) { if ($line -match '^\s*#\s*senders:\s*(.+)$') { return @($Matches[1].Split(',') | ForEach-Object { $_.Trim().ToLower() } | Where-Object { $_ }) } } }
    return @('movement', 'party', 'session')
}
# A full run always needs a sender; a quick run only when one of its game groups has a section that reports one. A quick run with no
# groups chosen (-Quick alone: $Groups is $null) runs every section, so it needs one as well.
function Get-NeedSender([bool]$IsQuick, $Groups, $SenderGroups) {
    if (-not $IsQuick -or $null -eq $Groups) { return $true }
    return (@($Groups | Where-Object { $SenderGroups -contains $_ }).Count -gt 0)
}
function Test-ResultScorer {
    $good = @('RESULT pass=2 fail=0 warn=0', 'SECTION a BEGIN', 'SECTION plant 1-2 BEGIN', 'SECTION plant 1-2 END 0', 'CHECK X PASS one', 'SENDER s 0 5', 'CHECK Y PASS two', 'SECTION a END 2', 'NOTE scenario complete: every chosen section ran', 'DONE')
    $cases = [ordered]@{
        'whole'            = @{ L = $good; Bad = $false }
        'missing'          = @{ L = @(); Bad = $true }
        'cut off'          = @{ L = $good[0..5]; Bad = $true }
        'section open'     = @{ L = @($good | Where-Object { $_ -notlike 'SECTION a END*' }); Bad = $true }
        'section aborted'  = @{ L = @($good | ForEach-Object { if ($_ -like 'SECTION a END*') { 'SECTION a ABORTED NullReferenceException' } else { $_ } }); Bad = $true }
        'sender stuck'     = @{ L = @($good | ForEach-Object { if ($_ -like 'SENDER*') { 'SENDER s 7 7' } else { $_ } }); Bad = $true }
        'no sender'        = @{ L = @($good | Where-Object { $_ -notlike 'SENDER*' }); Bad = $true }
        'count mismatch'   = @{ L = @($good | ForEach-Object { if ($_ -like 'RESULT*') { 'RESULT pass=3 fail=0 warn=0' } else { $_ } }); Bad = $true }
        'early exit'       = @{ L = @($good | Where-Object { $_ -notlike 'NOTE scenario complete*' }); Bad = $true }
    }
    $wrong = @()
    foreach ($name in $cases.Keys) {
        $c = $cases[$name]; $got = @(Test-ResultText $c.L $true)
        if (($got.Count -gt 0) -ne $c.Bad) { $wrong += "$name -> $(if ($got.Count) { $got -join '; ' } else { 'accepted' })" }
    }
    # the sender rule is scoped to the groups a quick run chose, and stays strict in a full run
    $noSender = @($good | Where-Object { $_ -notlike 'SENDER*' })
    $sg = @(Get-SenderGroups)
    $scoped = [ordered]@{
        'quick launcher, no sender' = @{ Q = $true; G = @(); Bad = $false }
        'quick ui, no sender'       = @{ Q = $true; G = @('ui'); Bad = $false }
        'quick party, no sender'    = @{ Q = $true; G = @('party'); Bad = $true }
        'quick ui+session, no sender' = @{ Q = $true; G = @('ui', 'session'); Bad = $true }
        'quick all, no sender'      = @{ Q = $true; G = $null; Bad = $true }
        'full, no sender'           = @{ Q = $false; G = $null; Bad = $true }
    }
    foreach ($name in $scoped.Keys) {
        $c = $scoped[$name]; $got = @(Test-ResultText $noSender (Get-NeedSender $c.Q $c.G $sg))
        if (($got.Count -gt 0) -ne $c.Bad) { $wrong += "$name -> $(if ($got.Count) { $got -join '; ' } else { 'accepted' })" }
    }
    if (@('movement', 'party', 'session' | Where-Object { $sg -notcontains $_ }).Count -gt 0) { $wrong += "sender groups from check-groups.txt: $($sg -join ',')" }
    return [pscustomobject]@{ Cases = $cases.Count + $scoped.Count + 1; Wrong = $wrong }
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
Say "== Grow Home Co-op audit  $Stamp$(if ($Quick) { "  ($QuickLabel, groups: $GroupsText)" })" 'Cyan'
if ($Quick) { Say "   $QuickLabel - it is never accepted by publish.ps1 or deploy.ps1; only a full run of every check is." 'Yellow' }
Say "1/4 build"

# What the launcher is built from (its folder without bin/obj) plus the mod it carries: the game stage installs the audited mod, and the
# launcher's mod pin replaces any mod that is not its own bundled one, so a launcher carrying an older mod must never be reused.
$ExeStamp = Join-Path $AuditDir 'work\exe-build-stamp.txt'
function Get-ExeSourceHash([bool]$WithMod) {
    $exeDir = (Get-Item (Join-Path $Root 'exe')).FullName.TrimEnd('\')
    $lines = Get-ChildItem $exeDir -Recurse -File | Where-Object { $_.FullName.Substring($exeDir.Length) -notmatch '^\\(bin|obj)\\' -and $_.Name -ne 'CoopMod.dll' } |
        Sort-Object FullName | ForEach-Object { $_.FullName.Substring($exeDir.Length) + ' ' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    if ($WithMod) { $lines = @($lines) + ('mod ' + (Get-FileHash $ModDll -Algorithm SHA256).Hash) }
    $sha = [Security.Cryptography.SHA256]::Create()
    return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))) -replace '-', '').ToLower()
}

$modBuilt = $false; $exeBuilt = $false; $exeReused = $false
try {
    $modBuilt = Invoke-Build $ModProj 'mod'
    if ($modBuilt -and -not (Test-Path $ModDll)) { $modBuilt = $false }
    if ($modBuilt) {
        # the bundled mod always counts: the launcher pins it (EnforceModPin) and would put a stale bundled copy over the audited one in the game stage
        $withMod = $true
        if ($Quick -and (Test-Path $ExeStamp) -and (Test-Path $ExeFile)) {
            $st = (Get-Content $ExeStamp -Raw).Trim().Split(' ')
            $srcNow = Get-ExeSourceHash $false
            $ok = $st.Count -eq 3 -and $st[0] -eq $srcNow -and $st[2] -eq (Get-FileHash $ExeFile -Algorithm SHA256).Hash.ToLower()
            if ($ok -and $withMod) { $ok = $st[1] -eq (Get-ExeSourceHash $true) }
            if ($ok) { $exeBuilt = $true; $exeReused = $true; Say "  launcher sources unchanged since its last build: not rebuilt (quick audit)" 'DarkGray' }
        }
        if (-not $exeReused) {
            Copy-Item $ModDll (Join-Path $Root 'exe\CoopMod.dll') -Force          # the launcher carries the mod inside itself
            $exeBuilt = Invoke-Build $ExeProj 'exe'
            if ($exeBuilt -and -not (Test-Path $ExeFile)) { $exeBuilt = $false }
            if ($exeBuilt) {
                New-Item -ItemType Directory -Force -Path (Split-Path $ExeStamp) | Out-Null
                "$(Get-ExeSourceHash $false) $(Get-ExeSourceHash $true) $((Get-FileHash $ExeFile -Algorithm SHA256).Hash.ToLower())" | Set-Content $ExeStamp -Encoding ascii
            } else { Remove-Item $ExeStamp -ErrorAction SilentlyContinue }
        }
    }
} catch { Say "  build crashed: $($_.Exception.Message)" 'Red' }
$modHash = if ($modBuilt) { (Get-FileHash $ModDll -Algorithm SHA256).Hash.ToLower() } else { '' }
Add-Check 'BUILD-OK' ($modBuilt -and $exeBuilt) 'the mod and the launcher build without errors' $(if ($modBuilt -and $exeBuilt) { "mod sha256 $($modHash.Substring(0, 16))$(if ($exeReused) { '; launcher not rebuilt (sources unchanged, quick audit)' })" } else { "mod built=$modBuilt exe built=$exeBuilt (see reports\$Stamp\build-*.txt)" })

# ---- 2. launcher checks --------------------------------------------------------------------------------------------------------------
# Not run in parallel with the game stage, on purpose: the launcher's --audit refuses several checks while any GrowHome process runs
# (UPDATE-STAGED-APPLY, RUNNING-GAME, LAUNCH-VERIFY), starts fake "GrowHome.exe" processes of its own (which the game stage would take for
# the game, and Stop-Game would kill), and its patch check reads the real co-op copy's Managed folder while the game stage swaps CoopMod.dll there.
Say "2/4 launcher (updater and patcher)"
$exeRan = $false
if ($SkipExe) { Say "  skipped (-SkipExe)" 'Yellow' }
elseif ($ExeSkipped) { Say "  skipped (quick audit without the group 'launcher')" 'Yellow' }
elseif (-not $exeBuilt) { Say "  not run: the build failed" 'Red' }
else {
    Assert-GameClosed
    $exeOut = Join-Path $Report 'exe-result.txt'
    try {
        $p = Start-Process -FilePath $ExeFile -ArgumentList @('--audit', '--use-game', "`"$GameDir`"", '--audit-out', "`"$exeOut`"") -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Report 'exe-console.txt')
        if (-not $p.WaitForExit(120000)) { $p.Kill(); Say "  the launcher audit did not finish in 2 minutes" 'Red' }
        $exeRan = Read-Results $exeOut
        if (-not $exeRan) { Say "  the launcher audit left no result (exit code $(try { $p.ExitCode } catch { '?' }))" 'Red' }
    } catch { Say "  launcher audit crashed: $($_.Exception.Message)" 'Red' }
    finally { Stop-AuditStrays }
}

# ---- 3. the game ---------------------------------------------------------------------------------------------------------------------
Say "3/4 game (mod checks inside the real game)"
$gameRan = $false
if ($SkipGame) { Say "  skipped (-SkipGame)" 'Yellow' }
elseif ($GameSkipped) { Say "  skipped (quick audit without an in-game group)" 'Yellow' }
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
        if (Test-Path $savesDir) { Remove-Item $savesDir -Recurse -Force -ErrorAction SilentlyContinue }   # the audit plays a fresh copy of the world (small starting plants), never the owner's grown save; the restore after the run puts that back
        $selftest = @($(if ($Quick) { 'quick' } else { 'audit' }))
        if ($GameGroups) { $selftest += "groups=$($GameGroups -join ',')" }       # the mod runs only the sections of these groups (SelfTest.cs On(...))
        Set-Content (Join-Path $GameDir 'coop-selftest.txt') $selftest -Encoding ascii
        # start the game the way a friend does: open the launcher window; here it presses Play by itself. It must start the co-op copy, confirm that the mod loaded and close again.
        $launcherLog = Join-Path (Split-Path $GameDir -Parent) 'launcher.log'
        $logBefore = if (Test-Path $launcherLog) { @(Get-Content $launcherLog).Count } else { 0 }
        $launchClock = [Diagnostics.Stopwatch]::StartNew()
        $launcher = Start-Process -FilePath $ExeFile -ArgumentList @('--use-game', "`"$GameDir`"", '--no-update', '--auto-play', '--via-steam', '--game-args', '"-screen-width 800 -screen-height 450"') -PassThru
        $null = $launcher.Handle                                    # without this PowerShell loses the exit code
        Say "  launcher window opened and started the game; it plays by itself now, please do not touch it"
        # wait for the launcher to close, looking at every GrowHome process it starts meanwhile (ONLY-COOP-EXE)
        $launcherCode = -1
        while ($true) {
            Watch-GameExes
            if ($launcher.HasExited) { $launcherCode = $launcher.ExitCode; break }
            if ($launchClock.Elapsed.TotalSeconds -gt 180) { try { $launcher.Kill() } catch { }; break }
            Start-Sleep -Milliseconds 200
        }
        Watch-GameExes
        $newLines = if (Test-Path $launcherLog) { @(Get-Content $launcherLog | Select-Object -Skip $logBefore) } else { @() }
        $launcherText = ($newLines -join ' | ')
        Copy-Item $launcherLog (Join-Path $Report 'launcher.log') -Force -ErrorAction SilentlyContinue
        $took = $null; $hit = $newLines | Where-Object { $_ -match 'outcome Loaded after (\d+) s' } | Select-Object -Last 1; if ($hit -and $hit -match 'outcome Loaded after (\d+) s') { $took = [int]$Matches[1] }
        # the real start ran its gates in order before it launched anything: steam_appid.txt, the mod pin, the Steam gate, then the start
        $gateAt = @{}
        foreach ($g in @(@('appid', 'steam_appid\.txt: '), @('pin', 'mod pin: '), @('gate', 'steam gate: |steam is running but not registered'), @('start', 'started (directly|through steam)'))) {
            $gateAt[$g[0]] = -1
            for ($i = 0; $i -lt $newLines.Count; $i++) { if ($newLines[$i] -match $g[1]) { $gateAt[$g[0]] = $i; break } }
        }
        $gatesInOrder = $gateAt['appid'] -ge 0 -and $gateAt['pin'] -gt $gateAt['appid'] -and $gateAt['gate'] -gt $gateAt['pin'] -and $gateAt['start'] -gt $gateAt['gate']
        $gatesText = "start gates in the log: steam_appid.txt line $($gateAt['appid']), mod pin $($gateAt['pin']), Steam gate $($gateAt['gate']), start $($gateAt['start'])"
        Add-Check 'LAUNCH-VERIFIED' ($launcherCode -eq 0 -and $null -ne $took -and $took -le 60 -and $gatesInOrder) 'the launcher window starts the co-op copy (after its steam_appid.txt, mod pin and Steam checks, in that order), confirms that the mod loaded and closes by itself' $(if ($launcherCode -eq 0 -and $null -ne $took -and $gatesInOrder) { "mod loaded $took s after the start (limit 60 s), window closed after $([int]$launchClock.Elapsed.TotalSeconds) s; $gatesText" } else { "launcher exit code $launcherCode; $gatesText; log: " + $launcherText })
        $process = Get-Process GrowHome -ErrorAction SilentlyContinue | Select-Object -First 1
        $clock = [Diagnostics.Stopwatch]::StartNew(); $lastShown = ''; $died = $false
        $modLog = Join-Path $GameDir 'coopmod-steam.log'
        while ($clock.Elapsed.TotalMinutes -lt $GameTimeoutMinutes) {
            if (Test-Path $resultFile) { break }
            Watch-GameExes
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
        try { if ($launcher -and -not $launcher.HasExited) { $launcher.Kill(); Say "  the launcher window was still open: closed" 'Yellow' } } catch { }
        Stop-Game
        Stop-AuditStrays
        try { $RegPutBack += Restore-RegValues $RealSnap.Reg } catch { Say "  could not put the game's registry values back: $($_.Exception.Message) (copy in $RegBackupFile)" 'Red' }
        # keep the evidence, then put everything back
        foreach ($f in 'coopmod-steam.log', 'coop-selftest-result.txt', 'coopmod-ui.log') { if (Test-Path (Join-Path $GameDir $f)) { Copy-Item (Join-Path $GameDir $f) (Join-Path $Report ('game-' + $f)) -Force } }
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

    # only the co-op copy ever ran: every GrowHome process seen while the launcher started the game and while the checks ran
    $seenText = ($GameSeen.GetEnumerator() | ForEach-Object { "$($_.Key) x$($_.Value)" }) -join '; '
    $foreign = @($GameSeen.Keys | Where-Object { $_ -ne $CoopExe })
    $regularSeen = @($GameSeen.Keys | Where-Object { $_ -eq $RegularExe }).Count -gt 0
    # a regular game the launcher closed between two samples still counts: the launcher's own log of this run says so
    $handoffLines = @(@($newLines) | Where-Object { $_ -match 'a regular Grow Home came up next to the co-op copy|Steam started the regular game|could not close the regular game|refused to start' })
    Add-Check 'ONLY-COOP-EXE' ($GameSamples -gt 0 -and $GameSeen.ContainsKey($CoopExe) -and $foreign.Count -eq 0 -and -not $regularSeen -and $handoffLines.Count -eq 0) 'during the launcher flow and the whole game stage only the co-op copy GrowHome.exe ever ran, never the regular Steam one (not even briefly: the launcher log of this run names none it closed)' "$GameSamples samples: $(if ($seenText) { $seenText } else { 'no game process seen' })$(if ($regularSeen) { ' - the REGULAR game ran' })$(if ($handoffLines.Count -gt 0) { ' - launcher log: ' + ($handoffLines -join ' | ') })"

    # the mod's result file is whole: the scorer first proves on made-up texts that it fails every hollow shape, then reads the real file
    $scorer = Test-ResultScorer
    $resultLines = if (Test-Path (Join-Path $Report 'game-coop-selftest-result.txt')) { @(Get-Content (Join-Path $Report 'game-coop-selftest-result.txt')) } else { @() }
    $needSender = Get-NeedSender ([bool]$Quick) $GameGroups @(Get-SenderGroups)
    $resultProblems = @(Test-ResultText $resultLines ([bool]$needSender))
    $sectionCount = @($resultLines | Where-Object { $_ -match '^SECTION .+ BEGIN$' }).Count
    $senderCount = @($resultLines | Where-Object { $_ -match '^SENDER ' }).Count
    Add-Check 'RESULT-COMPLETE' ($scorer.Wrong.Count -eq 0 -and $resultProblems.Count -eq 0) "the mod's result file is whole (RESULT counts match, DONE at the end, every section ended, every sender advanced, the scenario reached its end); the scorer fails each hollow shape" "scorer $($scorer.Cases - $scorer.Wrong.Count)/$($scorer.Cases) cases right$(if ($scorer.Wrong.Count) { ' (wrong: ' + ($scorer.Wrong -join ' | ') + ')' }); real file: $($resultLines.Count) lines, $sectionCount sections, $senderCount senders$(if ($resultProblems.Count) { ' - ' + ($resultProblems -join '; ') })"
}

# ---- no crash of the launcher or the game during the run: the Application event log has no error report for either -----------------
Stop-AuditStrays
Start-Sleep -Seconds 2                                                  # Windows writes the report a moment after the crash
$crashEvents = @(Get-CrashEvents $RunStart)
Add-Check 'NO-CRASH-EVENTS' ($crashEvents.Count -eq 0) 'no Application Error, .NET Runtime, Application Hang or Windows Error Reporting event for GrowHomeCoop.exe or GrowHome.exe appeared during the run (no crash dialog on the screen)' "$($crashEvents.Count) events since $($RunStart.ToString('HH:mm:ss'))$(if ($crashEvents.Count) { ': ' + ($crashEvents | Select-Object -First 2 | ForEach-Object { $_ }) -join ' || ' })"

# ---- the owner's real data is exactly as before the run (whatever ran: launcher, game or nothing) ----------------------------------
try { $RegPutBack += Restore-RegValues $RealSnap.Reg } catch { }      # in case the launcher stage alone touched them
$realEnd = [pscustomobject]@{ Saves = (Get-RealSaves); Keep = (Get-FileHashes $SaveKeep); RegDigest = (Get-RegDigest (Get-RegValues)) }
$why = @()
if (-not (Same-Hashes $RealSnap.Saves $realEnd.Saves)) { $why += 'the real Steam save changed' }
if (-not (Same-Hashes $RealSnap.Keep $realEnd.Keep)) { $why += 'save-keep changed' }
if ($RealSnap.RegDigest -ne $realEnd.RegDigest) { $why += "the registry values changed ($($RealSnap.RegDigest.Substring(0, [Math]::Min(12, $RealSnap.RegDigest.Length))) -> $($realEnd.RegDigest.Substring(0, [Math]::Min(12, $realEnd.RegDigest.Length))))" }
$regCount = if ($RealSnap.Reg) { $RealSnap.Reg.Values.Count } else { 0 }
Add-Check 'REAL-DATA-UNCHANGED' ($why.Count -eq 0) "the owner's real save, save-keep and the game's registry values (HKCU\Software\Reflections\Grow Home) are exactly as before the whole run" "$($RealSnap.Saves.Count) real save files, $($RealSnap.Keep.Count) save-keep files, $regCount registry values (the game run changed $RegPutBack, put back)$(if ($why.Count) { ' - ' + ($why -join '; ') })"

# ---- 4. verdict ----------------------------------------------------------------------------------------------------------------------
Say "4/4 verdict"
$required = @()
foreach ($line in Get-Content (Join-Path $AuditDir 'required-checks.txt')) {
    if ($line -match '^\s*#' -or $line.Trim() -eq '') { continue }
    $parts = $line.Split('|')
    $required += [pscustomobject]@{ Id = $parts[0].Trim(); Where = $parts[1].Trim(); Title = $parts[2].Trim() }
}
# quick audit: which groups each in-game check belongs to (audit\check-groups.txt; only read for a quick run with -Groups)
$groupMap = @{}
if ($QuickGroups) {
    $gf = Join-Path $AuditDir 'check-groups.txt'
    if (Test-Path $gf) {
        foreach ($line in Get-Content $gf) {
            if ($line -match '^\s*#' -or $line.Trim() -eq '') { continue }
            $parts = $line.Split('|')
            if ($parts.Count -ge 2) { $groupMap[$parts[0].Trim()] = @($parts[1].Split(',') | ForEach-Object { $_.Trim().ToLower() } | Where-Object { $_ }) }
        }
    } else { Say "  audit\check-groups.txt is missing: every in-game check is required" 'Yellow' }
}
function Test-InScope($r) {
    if (-not $QuickGroups) { return $true }                          # a full audit (and -Quick without -Groups) requires everything
    if ($r.Id -eq 'BUILD-OK') { return $true }
    if ($r.Where -eq 'exe') { return ($QuickGroups -contains 'launcher') }
    if ($r.Where -eq 'script') { return (-not $GameSkipped) }
    if (-not $groupMap.ContainsKey($r.Id)) { return $true }          # no group on record: required in every quick run
    foreach ($g in $groupMap[$r.Id]) { if ($QuickGroups -contains $g) { return $true } }
    return $false
}

$rows = New-Object System.Collections.ArrayList
$failed = 0; $skipped = 0; $passed = 0
$scriptGameChecks = 'GAME-STARTS-MODDED', 'NO-UNEXPECTED-ERRORS', 'ORIGINAL-UNTOUCHED', 'AUDIT-RESTORED', 'LAUNCH-VERIFIED', 'ONLY-COOP-EXE', 'RESULT-COMPLETE'
foreach ($r in $required) {
    $mine = @($Checks | Where-Object { $_.Id -eq $r.Id })
    $skip = ($GameSkipped -and ($r.Where -eq 'game' -or $scriptGameChecks -contains $r.Id)) -or ($ExeSkipped -and $r.Where -eq 'exe') -or -not (Test-InScope $r)
    $detail = ''
    if ($mine.Count -eq 0) {
        if ($skip) { $status = 'SKIPPED'; $skipped++ } else { $status = 'NOT RUN'; $failed++; if ($QuickGroups -and $r.Where -eq 'game' -and -not $groupMap.ContainsKey($r.Id)) { $detail = 'this check has no group in audit\check-groups.txt, so every quick run requires it' } }
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
$gate = (-not $Quick) -and (-not $QuickGroups) -and $skipped -eq 0
if ($failed -eq 0 -and $gate) { $verdict = "AUDIT PASSED: all $passed critical checks passed" }
elseif ($Quick -and $failed -eq 0) { $verdict = "$QuickLabel, groups $GroupsText - no failures: $passed passed, $skipped skipped. Only a full audit can release." }
elseif ($Quick) { $verdict = "$QuickLabel, groups $GroupsText - FAILED: $failed of $($rows.Count) critical checks did not pass" }
elseif ($failed -eq 0) { $verdict = "AUDIT PARTIAL: $passed passed, $skipped skipped - this is not a release gate" }
else { $verdict = "AUDIT FAILED: $failed of $($rows.Count) critical checks did not pass" }
Say $verdict $(if ($failed -eq 0 -and $gate) { 'Green' } elseif ($failed -eq 0) { 'Yellow' } else { 'Red' })

# report file
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Grow Home Co-op audit $Stamp")
[void]$md.AppendLine('')
[void]$md.AppendLine("**$verdict**")
[void]$md.AppendLine('')
[void]$md.AppendLine("Mod sha256: ``$modHash``  |  quick run: $Quick  |  groups: $GroupsText  |  game copy: ``$GameDir``")
if ($Quick) { [void]$md.AppendLine(''); [void]$md.AppendLine("**$QuickLabel**: only for development; publish.ps1 and deploy.ps1 accept only a full audit (last-full-pass.md).") }
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
if ($Quick) { Say "$QuickLabel - last-full-pass.md was not touched" 'Yellow' }
if ($failed -eq 0 -and $gate -and -not $Quick -and -not $QuickGroups) { Copy-Item (Join-Path $Report 'audit-report.md') (Join-Path $AuditDir 'last-full-pass.md') -Force }   # what deploy.ps1 trusts: only a full run in which everything passed
Say "report: $(Join-Path $Report 'audit-report.md')" 'DarkGray'

# keep the disk tidy: the last 3 backups and the last 10 reports
foreach ($pair in @(@('work', 'backup-*', 3), @('reports', '2*', 10))) {
    $dir = Join-Path $AuditDir $pair[0]
    if (Test-Path $dir) { Get-ChildItem $dir -Directory -Filter $pair[1] | Sort-Object Name -Descending | Select-Object -Skip $pair[2] | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $(if ($failed -gt 0) { 1 } elseif (-not $gate) { 3 } else { 0 })     # 0 = full pass, 1 = something failed, 3 = partial or quick run (not a release gate)
