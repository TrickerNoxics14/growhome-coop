<#
.SYNOPSIS
  Publishes the audited build to GitHub: a SIGNED version.json, the mod, the sources, a release with the launcher and the installer. Then checks what friends will really get.
  Refuses unless the newest full audit passed for exactly this build. Run it only when the author has said to publish.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File audit\publish.ps1 -Notes "what is new in one sentence"
#>
param(
    [Parameter(Mandatory = $true)][string]$Notes,
    [string]$Repo = 'TrickerNoxics14/growhome-coop',
    [string]$KeyFile = (Join-Path $env:USERPROFILE '.growhome-coop\signing-key.xml')
)
$ErrorActionPreference = 'Stop'
$AuditDir = $PSScriptRoot
$Root     = Split-Path $AuditDir -Parent
$RepoDir  = Join-Path $Root 'repo'
$ModDll   = Join-Path $Root 'coopmod\bin\Release\net35\CoopMod.dll'
$ExeFile  = Join-Path $Root 'exe\bin\Release\net48\GrowHomeCoop.exe'
function Say([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Sha([string]$p) { (Get-FileHash $p -Algorithm SHA256).Hash.ToLower() }
function Fail([string]$t) { Say $t 'Red'; exit 2 }

# 1. the gate: a full audit passed for exactly this mod
$report = Join-Path $AuditDir 'last-full-pass.md'
if (-not (Test-Path $report)) { Fail 'No passing full audit on record. Run audit\run-audit.ps1 first.' }
$text = Get-Content $report -Raw
if ($text -notmatch 'AUDIT PASSED' -or $text -notmatch 'Mod sha256: `([0-9a-f]{64})`') { Fail 'The last full audit did not pass.' }
$built = Sha $ModDll
if ($Matches[1] -ne $built) { Fail 'The mod was rebuilt after the audit. Run the audit again.' }
if (-not (Test-Path $KeyFile)) { Fail "The signing key is missing ($KeyFile). Without it nobody can publish an update." }
$bundled = ([regex]::Match((Get-Content (Join-Path $Root 'exe\Program.cs') -Raw), 'const int BundledVersion = (\d+);')).Groups[1].Value
if (-not $bundled) { Fail 'Cannot read the version number from the launcher.' }
$version = [int]$bundled
Say "Publishing version $version (mod $($built.Substring(0, 12))), audit passed." 'Green'

# 2. the signed manifest
$url = "https://raw.githubusercontent.com/$Repo/main/CoopMod.dll"
$data = [Text.Encoding]::UTF8.GetBytes("growhome-coop-manifest-v1`n$version`n$built`n$url")
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.FromXmlString([IO.File]::ReadAllText($KeyFile))
$signature = [Convert]::ToBase64String($rsa.SignData($data, 'SHA256'))
$manifest = [ordered]@{ version = $version; dll = $url; sha256 = $built; notes = $Notes; signature = $signature } | ConvertTo-Json
Set-Content (Join-Path $RepoDir 'version.json') $manifest -Encoding UTF8

# 3. the files (no personal scripts, no reports)
Copy-Item $ModDll (Join-Path $RepoDir 'CoopMod.dll') -Force
Copy-Item (Join-Path $Root 'coopmod\*.cs'), (Join-Path $Root 'coopmod\CoopMod.csproj') (Join-Path $RepoDir 'mod-src') -Force
Copy-Item (Join-Path $Root 'exe\*.cs'), (Join-Path $Root 'exe\GrowHomeCoop.csproj') (Join-Path $RepoDir 'installer-src') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $RepoDir 'audit') | Out-Null
foreach ($f in 'run-audit.ps1', 'publish.ps1', 'required-checks.txt', 'CRITICAL-CHECKS.md', 'SECURITY.md') { Copy-Item (Join-Path $AuditDir $f) (Join-Path $RepoDir "audit\$f") -Force }
Copy-Item (Join-Path $AuditDir 'SECURITY.md') (Join-Path $RepoDir 'SECURITY.md') -Force
Remove-Item (Join-Path $RepoDir 'audit\deploy.ps1') -ErrorAction SilentlyContinue         # it names the author's own laptop
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'build-setup.ps1') | Out-Null
$rel = Join-Path $Root 'release'; New-Item -ItemType Directory -Force -Path $rel | Out-Null
Copy-Item $ExeFile (Join-Path $rel 'GrowHomeCoop.exe') -Force
Copy-Item (Join-Path $Root 'GrowHomeCoop-Setup.bat') (Join-Path $rel 'GrowHomeCoop-Setup.bat') -Force

# 4. commit, push, release (git and gh talk on stderr even when all is well, so their exit codes decide, not their messages; safe to run again)
Push-Location $RepoDir
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
try {
    git rm -q --cached audit/deploy.ps1 2>$null | Out-Null
    git add -A 2>$null | Out-Null
    $pending = git status --porcelain
    if ($pending) {
        $msg = "Version ${version}: $Notes`n`nAll critical checks passed for this build (audit/CRITICAL-CHECKS.md). The update is signed (SECURITY.md).`n`nCo-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>"
        git commit -q -m $msg 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail 'git commit failed' }
    }
    git push origin main 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'git push failed' }
    $has = gh release view "v$version" --repo $Repo 2>$null
    if ($LASTEXITCODE -ne 0) {
        gh release create "v$version" (Join-Path $rel 'GrowHomeCoop.exe') (Join-Path $rel 'GrowHomeCoop-Setup.bat') --repo $Repo --title "Grow Home Co-op v$version" --notes "$Notes`n`nAll critical checks passed for this build. Friends: download GrowHomeCoop.exe and run it. Existing players get the new mod by themselves; the launcher itself changes only when you download it again." --latest 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail 'creating the release failed' }
    } else { Say "  release v$version already exists" 'DarkGray' }
} finally { $ErrorActionPreference = $prev; Pop-Location }

# 5. what friends will really get
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("gh-verify-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)); New-Item -ItemType Directory -Path $tmp | Out-Null
$ok = $true
try {
    Start-Sleep -Seconds 4
    $resp = Invoke-WebRequest "https://raw.githubusercontent.com/$Repo/main/version.json?t=$([DateTime]::UtcNow.Ticks)" -Headers @{ "Cache-Control" = "no-cache" } -UseBasicParsing
    $liveText = if ($resp.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($resp.Content) } else { [string]$resp.Content }
    $live = $liveText.TrimStart([char]0xFEFF) | ConvertFrom-Json
    $dllPath = Join-Path $tmp 'CoopMod.dll'; Invoke-WebRequest "https://raw.githubusercontent.com/$Repo/main/CoopMod.dll?t=$([DateTime]::UtcNow.Ticks)" -OutFile $dllPath
    $publicKey = [IO.File]::ReadAllText((Join-Path (Split-Path $KeyFile) 'public-key.xml'))
    $pk = New-Object System.Security.Cryptography.RSACryptoServiceProvider; $pk.FromXmlString($publicKey)
    $liveData = [Text.Encoding]::UTF8.GetBytes("growhome-coop-manifest-v1`n$($live.version)`n$($live.sha256.ToLower())`n$($live.dll)")
    $checks = [ordered]@{
        'manifest says this version'         = ($live.version -eq $version)
        'manifest fingerprint = the mod'     = ($live.sha256 -eq $built -and (Sha $dllPath) -eq $built)
        'signature is valid'                 = $pk.VerifyData($liveData, 'SHA256', [Convert]::FromBase64String($live.signature))
    }
    $prev2 = $ErrorActionPreference; $ErrorActionPreference = "Continue"; gh release download "v$version" --repo $Repo --pattern "GrowHomeCoop.exe" --dir $tmp --clobber 2>&1 | Out-Null; $ErrorActionPreference = $prev2
    $checks['release launcher = audited launcher'] = ((Sha (Join-Path $tmp 'GrowHomeCoop.exe')) -eq (Sha $ExeFile))
    foreach ($k in $checks.Keys) { Say ("  {0,-4} {1}" -f $(if ($checks[$k]) { 'OK' } else { 'FAIL' }), $k) $(if ($checks[$k]) { 'Green' } else { 'Red' }); if (-not $checks[$k]) { $ok = $false } }
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
if ($ok) { Say "Published and verified: https://github.com/$Repo/releases/tag/v$version" 'Cyan'; exit 0 } else { Say 'Published, but a check failed: look at it now.' 'Red'; exit 1 }

