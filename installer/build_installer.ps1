<#
.SYNOPSIS
  Build the Pepite installer (plan 12 step 5): one command, from a clean checkout with the pinned data in
  src\Data.Build\out\.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build_installer.ps1 -Version 0.11.1 [-Private] [-VerifyReproducible]

.DESCRIPTION
  -WorkDir <work> (or $env:PEPITE_WORK): the bulk-data folder outside the repository. Outputs:
    <work>\build\stage-<ver>[-test]\         the publish folder the installer packs (PDBs removed)
    <work>\release-<ver>\                    Pepite-<ver>[-test]-installation.exe, SHA256SUMS[-test].txt,
                                             files[-test].sha256, size-report[-test].md, symbols-<ver>[-test].zip,
                                             LISEZ-MOI[-test].txt, DEPLOIEMENT.txt, exemple-etablissement.json,
                                             BUILD-INFO[-test].txt, signatures[-test].txt,
                                             espeak-ng-<commit>.tar.gz (the GPL-3 source offer)
    Every file is at the top of the folder (plan 13: the release assets are flat), and SHA256SUMS names them without
    a sub-path, so "sha256sum -c SHA256SUMS.txt" works in a folder of downloaded assets.
  Steps, one heavy job at a time (the lab disk deadlocks under parallel writes; the test VM must be off):
    preconditions -> dotnet publish -> voice packs (D28) -> LISEZ-MOI -> release lint -> PDBs to symbols zip ->
    files.sha256 -> ISCC -> SHA256SUMS -> size report -> espeak-ng source archive -> [-VerifyReproducible]
  -Private: a private test build (suffix -test): keeps the "public": false voice packs.
  -AllowDirty: build from a working tree with uncommitted changes (recorded in BUILD-INFO.txt; never for a release).
  -EspeakSrc: the espeak-ng git clone of build_espeak.ps1 (default <PREDICT_BUILD_DIR or work\build>\espeak\src).
  -OldRelease: the publish folder the size report compares with (default <work>\publish-0.9.0); -OldRelease '' (or
   'none') = no comparison (plan 13: the CI build).
  -Forbidden: the release-lint word list, kept OUTSIDE the repository (default <work>\release-config\release-lint-forbidden.txt).
  -NoForbiddenList (plan 13: the CI build, where the list is not available): skips only the forbidden-word pass of the
   release lint (release_lint.py --no-forbidden; the other checks run) and records it in BUILD-INFO.txt. Such a build is
   published only after the owner's local rebuild from the same tag passes the full lint.
  -EspeakSourceArchive: the espeak-ng-<commit>.tar.gz of the data release (fetch_data_release.py) instead of a git archive
   of the -EspeakSrc clone.
  BUILD-INFO.txt records the commit, SDK, Python, Inno Setup, the VC++ runtime versions, the forbidden-term pass and, on
  GitHub Actions, the runner image and the run URL; signatures.txt lists the Authenticode status of every PE file of the
  stage and of the installer.
  ASCII only (Windows PowerShell 5.1).
#>
param(
  [Parameter(Mandatory)][string]$Version,
  [switch]$Private,
  [switch]$VerifyReproducible,
  [switch]$AllowDirty,
  [string]$WorkDir = $env:PEPITE_WORK,
  [string]$EspeakSrc = '',
  [string]$OldRelease = '',
  [string]$Forbidden = '',
  [switch]$NoForbiddenList,
  [string]$EspeakSourceArchive = ''
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if (-not $WorkDir) { throw 'give -WorkDir <work> (or set PEPITE_WORK): the bulk-data folder outside the repository' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "version ${Version}: expected x.y.z" }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path      # the repository root
$src = Join-Path $repo 'src'
$out = Join-Path $src 'Data.Build\out'
$suffix = if ($Private) { '-test' } else { '' }
$stage = Join-Path $WorkDir "build\stage-$Version$suffix"
$rel = Join-Path $WorkDir "release-$Version"
# -OldRelease '' (or 'none'; "''" when an empty argument reaches -File literally) = no size comparison
$compareOld = -not ($PSBoundParameters.ContainsKey('OldRelease') -and ($OldRelease -in @('', 'none', "''", '""')))
if ($compareOld -and -not $OldRelease) { $OldRelease = Join-Path $WorkDir 'publish-0.9.0' }
if (-not $Forbidden) { $Forbidden = Join-Path $WorkDir 'release-config\release-lint-forbidden.txt' }
if (-not $EspeakSrc) {
  $b = if ($env:PREDICT_BUILD_DIR) { $env:PREDICT_BUILD_DIR } else { Join-Path $WorkDir 'build' }
  $EspeakSrc = Join-Path $b 'espeak\src'
}
$utf8 = New-Object Text.UTF8Encoding $false
function Step([string]$m) { Write-Host ("== {0} {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
function Sha([string]$p) { (Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant() }

# ---------------------------------------------------------------- 1. preconditions
Step 'preconditions'
# MSBuild reads every environment variable as a property (names are case-insensitive): a VERSION variable becomes the
# Version of each project without its own (Predict.Core got 0.10.1 in the 0.10.1 CI build, 1.0.0 locally), so the
# binaries would differ from a clean rebuild. Refuse rather than build different bytes.
$leak = @(@('Version', 'VersionPrefix', 'VersionSuffix', 'AssemblyVersion', 'FileVersion', 'InformationalVersion',
          'PackageVersion') | Where-Object { Test-Path "Env:$_" })
if ($leak) { throw "environment variable(s) $($leak -join ', ') set: MSBuild would take them as project properties (the binaries would differ from a clean build); remove them for this session (Remove-Item Env:$($leak[0])), the version comes from -Version" }
# A native command with its stderr merged: under 'Stop', Windows PowerShell 5.1 turns the first stderr line into a
# terminating NativeCommandError, so run it with 'Continue' and read $LASTEXITCODE instead (lines as plain strings).
function Native([scriptblock]$cmd) {
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { & $cmd 2>&1 | ForEach-Object { "$_" } } finally { $ErrorActionPreference = $old }
}
# git must work: a failing "git status" (not a repository, git missing, "dubious ownership") must not read as clean
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git not found on PATH' }
# stdout only for the status (a warning on stderr must never read as a changed file); stderr only for the error message
$gitOut = @(); $gitErr = @()
$old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
try {
  & git -C $repo status --porcelain -- . 2>&1 | ForEach-Object {
    if ($_ -is [System.Management.Automation.ErrorRecord]) { $gitErr += "$_" } else { $gitOut += "$_" }
  }
} finally { $ErrorActionPreference = $old }
if ($LASTEXITCODE) { throw "git status failed ($LASTEXITCODE) in ${repo}:`n$($gitErr -join "`n")" }
$status = $gitOut -join "`n"
$dirty = [bool]$status
if ($dirty -and -not $AllowDirty) { throw "the working tree has uncommitted changes (git status): commit first, or pass -AllowDirty for a test build`n$status" }
$commit = (& git -C $repo rev-parse HEAD)
if ($LASTEXITCODE -or -not $commit) { throw "git rev-parse HEAD failed ($LASTEXITCODE)" }
$commit = $commit.Trim()
$commitDate = [DateTimeOffset]::Parse((& git -C $repo log -1 --format=%cI HEAD).Trim()).UtcDateTime
$touchDate = $commitDate.ToString('yyyy-MM-dd'); $touchTime = $commitDate.ToString('HH:mm:ss')
# the SDK must start and be exactly the global.json one (rollForward: disable): say how to fix it, not a null error
$wantSdk = (Get-Content (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "dotnet not found on PATH: install the .NET SDK $wantSdk (global.json), or put a portable SDK folder first in PATH for this session (`$env:PATH = '<sdk folder>;' + `$env:PATH)"
}
$sdkOut = (Native { dotnet --version }) -join ' '
$sdkRc = $LASTEXITCODE
if ($sdkRc -or $sdkOut.Trim() -ne $wantSdk) {
  $found = (Native { dotnet --list-sdks }) -join '; '
  throw "the .NET SDK does not start as global.json asks (SDK $wantSdk, rollForward disable): dotnet --version gave exit code ${sdkRc}: $($sdkOut.Trim())`nSDKs of the dotnet on PATH ($((Get-Command dotnet).Source)): $found`nput the folder of SDK $wantSdk first in PATH for this session"
}
$sdk = $sdkOut.Trim()
foreach ($f in @('prediction.sqlite', 'prediction-SOURCES.txt', 'spelling.sqlite', 'spelling\SOURCE.txt', 'spelling\MPL-2.0.txt',
                 'grammar.sqlite', 'grammar\SOURCE.txt', 'grammar\MPL-2.0.txt', 'pictos.sqlite', 'ARASAAC-LICENCE.txt',
                 'noms-pack\noms\noms.json', 'noms-pack\noms\LICENCE.txt', 'fonts', 'espeak\libespeak-ng.dll', 'espeak\COPYING',
                 'espeak\SOURCE.txt', 'vcruntime\vcruntime140.dll', 'vcruntime\SOURCE.txt', 'voices\siwis\voice.json')) {
  if (-not (Test-Path (Join-Path $out $f))) { throw "missing pinned data: src\Data.Build\out\$f (fetch_data_release.py: development\BUILD.md section 0)" }
}
# 0.11.0 (plan offensive-words): the « mots exclus » are built from the committed list, not fetched; without them the app
# would offer every word (release_lint.py also fails the stage)
foreach ($f in @('exclusions\fr.txt', 'exclusions\SOURCE.txt')) {
  if (-not (Test-Path (Join-Path $out $f))) { throw "missing src\Data.Build\out\$f (build_exclusions.py ship: development\BUILD.md section 1b4)" }
}
if ($Private -and -not (Test-Path (Join-Path $out 'voices\pierre\voice.json'))) { Write-Warning 'private build without voices\pierre' }
if (-not $NoForbiddenList -and -not (Test-Path $Forbidden)) { throw "release-lint word list not found: $Forbidden (kept outside the repository; -NoForbiddenList for a CI build)" }
if ($EspeakSourceArchive -and -not (Test-Path $EspeakSourceArchive)) { throw "espeak-ng source archive not found: $EspeakSourceArchive" }
# the lab's test VM, when there is one, must be off (no-op elsewhere)
$vm = $null
try { $vm = Get-VM -Name 'predict-test-win11' -ErrorAction SilentlyContinue } catch { }
if ($vm -and $vm.State -eq 'Running') { throw 'the test VM is running: stop it first (one heavy job at a time on the lab disk)' }
$iscc = (& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'fetch_innosetup.ps1') -WorkDir $WorkDir | Select-Object -Last 1)
if (-not (Test-Path $iscc)) { throw "ISCC.exe not available ($iscc)" }
$dbHash = Sha (Join-Path $out 'prediction.sqlite')
Write-Host "commit $commit ($touchDate $touchTime UTC)$(if ($dirty) { ' + uncommitted changes' }), SDK $sdk, prediction.sqlite $dbHash"

function Build-Stage([string]$stageDir) {
  # ---------------------------------------------------------------- 2. publish
  Step "dotnet publish -> $stageDir"
  if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
  # PathMap: no local build path inside the binaries (the PDB path of the debug directory, CallerFilePath)
  # a full rebuild (Predict.Core included): an up-to-date check ignores a changed PathMap
  $pm = "-p:PathMap=$src\=/_/src/"
  & dotnet build (Join-Path $src 'Predict.Keyboard') -c Release -r win-x64 --self-contained true --no-incremental $pm -p:Deterministic=true -nologo -v:minimal | Out-Host
  if ($LASTEXITCODE) { throw "dotnet build failed ($LASTEXITCODE)" }
  & dotnet publish (Join-Path $src 'Predict.Keyboard') -c Release -r win-x64 --self-contained true --no-build -o $stageDir $pm -nologo -v:minimal | Out-Host
  if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
  # ---------------------------------------------------------------- 3. voice packs (D28)
  Step 'voice packs'
  foreach ($card in Get-ChildItem (Join-Path $stageDir 'voices') -Filter voice.json -Recurse -EA 0) {
    $j = Get-Content $card.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($j.public -eq $false -and -not $Private) { Remove-Item $card.Directory.FullName -Recurse -Force; Write-Host "  removed $($card.Directory.Name) (`"public`": false)" }
  }
  if (-not $Private) {
    foreach ($card in Get-ChildItem (Join-Path $stageDir 'voices') -Filter voice.json -Recurse -EA 0) {
      if ((Get-Content $card.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).public -eq $false) { throw "non-public voice pack left: $($card.Directory.Name)" }
    }
  }
  # ---------------------------------------------------------------- 4. LISEZ-MOI from the template
  Step 'LISEZ-MOI'
  $t = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LISEZ-MOI.txt.in'), [Text.Encoding]::UTF8)
  $notes = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "notes\$Version.txt"), [Text.Encoding]::UTF8).TrimEnd()
  $pierre = ''
  if (Test-Path (Join-Path $stageDir 'voices\pierre')) {   # D28 changed 2026-11-16: public and private builds
    $pierre = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'notes\credit-pierre.txt'), [Text.Encoding]::UTF8).TrimEnd() + "`n"
  }
  $prive = if ($Private) { ' - VERSION DE TEST PRIVEE' } else { ' - VERSION DE TEST' }
  $prive = $prive.Replace('PRIVEE', 'PRIV' + [char]0xC9 + 'E')
  # the gh attestation check (installer\notes\attestation.txt): public builds only, a -Private build has no attestation
  $att = ''
  if (-not $Private) { $att = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'notes\attestation.txt'), [Text.Encoding]::UTF8).TrimEnd() + "`n" }
  $t = $t.Replace('{ATTESTATION}', $att)
  $t = $t.Replace('{VERSION}', $Version).Replace('{SUFFIXE}', $suffix).Replace('{PRIVE}', $prive).Replace('{NOTES}', $notes)
  $t = $t.Replace('{CREDIT_PIERRE}', $pierre)
  # the data release the build fetched (its tag in data-release.lock.json): the link of the LISEZ-MOI credits
  $dataTag = (Get-Content (Join-Path $src 'Data.Build\data-release.lock.json') -Raw | ConvertFrom-Json).tag
  if ($dataTag -notmatch '^data-\d{4}\.\d{2}(\.\d+)?$') { throw "data-release.lock.json: tag '$dataTag' is not data-YYYY.MM[.n]" }
  $t = $t.Replace('{DONNEES}', $dataTag)
  $t = ($t -replace "`r`n", "`n") -replace "`n", "`r`n"
  [IO.File]::WriteAllText((Join-Path $stageDir 'LISEZ-MOI.txt'), $t, $utf8)
  # ---------------------------------------------------------------- 5. release lint
  Step 'release lint'
  $words = if ($NoForbiddenList) { @('--no-forbidden') } else { @('--forbidden', $Forbidden) }
  $lintArgs = @('-I', (Join-Path $PSScriptRoot 'release_lint.py'), '--stage', $stageDir) + $words + @(
                '--text', (Join-Path $PSScriptRoot 'avant-installation.txt'), '--text', (Join-Path $PSScriptRoot 'DEPLOIEMENT.txt'),
                '--text', (Join-Path $PSScriptRoot "notes\$Version.txt"), '--text', (Join-Path $PSScriptRoot 'pepite.iss'),
                '--text', (Join-Path $PSScriptRoot 'exemple-etablissement.json'))
  if (-not $Private) { $lintArgs += '--public' }
  & python @lintArgs | Out-Host
  if ($LASTEXITCODE) { throw 'release lint failed' }
}

function Write-FileList([string]$stageDir, [string]$listPath) {
  $root = (Resolve-Path $stageDir).Path.TrimEnd('\') + '\'
  $lines = Get-ChildItem $stageDir -Recurse -File | ForEach-Object { $_.FullName.Substring($root.Length) } |
    Sort-Object { $_ } -CaseSensitive | ForEach-Object { "$(Sha (Join-Path $root $_))  $_" }
  [IO.File]::WriteAllText($listPath, (($lines -join "`n") + "`n"), $utf8)
}

function Invoke-Iscc([string]$stageDir, [string]$outDir) {
  Step "ISCC -> $outDir"
  New-Item -ItemType Directory -Force $outDir | Out-Null
  $a = @('/Q', "/DVersion=$Version", "/DStageDir=$stageDir", "/DOutDir=$outDir", "/DTouchDate=$touchDate", "/DTouchTime=$touchTime")
  if ($Private) { $a += '/DPrivate' }
  if (Test-Path (Join-Path $stageDir 'voices\pierre\voice.json')) { $a += '/DHasPierre' }   # D28: the pierre component only when staged
  $a += (Join-Path $PSScriptRoot 'pepite.iss')
  & $iscc @a | Out-Host
  if ($LASTEXITCODE) { throw "ISCC failed ($LASTEXITCODE)" }
  Join-Path $outDir "Pepite-$Version$suffix-installation.exe"
}

Build-Stage $stage
New-Item -ItemType Directory -Force $rel | Out-Null

# ---------------------------------------------------------------- 6. PDBs -> symbols zip (never installed)
Step 'symbols'
$sym = Join-Path $rel "symbols-$Version$suffix.zip"
$pdbs = @(Get-ChildItem $stage -Recurse -File -Filter *.pdb)
if (Test-Path $sym) { Remove-Item $sym -Force }
if ($pdbs.Count) { Compress-Archive -Path ($pdbs | ForEach-Object FullName) -DestinationPath $sym -CompressionLevel Optimal; $pdbs | Remove-Item -Force }
Write-Host "  $($pdbs.Count) PDB(s) -> $(Split-Path $sym -Leaf)"

# ---------------------------------------------------------------- 7. files.sha256 (one line per installed file)
Step 'files.sha256'
$list = Join-Path $rel "files$suffix.sha256"
Write-FileList $stage $list
$installedDb = (Get-Content $list | Where-Object { $_ -match '  prediction\.sqlite$' }) -replace '  .*$', ''
if ($installedDb -ne $dbHash) { throw "prediction.sqlite in the stage ($installedDb) differs from src\Data.Build\out ($dbHash)" }

# ---------------------------------------------------------------- 8. ISCC
$exe = Invoke-Iscc $stage $rel

# ---------------------------------------------------------------- 9. texts next to the installer
Copy-Item (Join-Path $stage 'LISEZ-MOI.txt') (Join-Path $rel "LISEZ-MOI$suffix.txt") -Force
Copy-Item (Join-Path $PSScriptRoot 'DEPLOIEMENT.txt'), (Join-Path $PSScriptRoot 'exemple-etablissement.json') $rel -Force

# ---------------------------------------------------------------- 10. size report
Step 'size report'
$old = if ($compareOld) { @('--old', $OldRelease) } else { @() }
& python -I (Join-Path $src 'Data.Build\size_report.py') --new $stage @old --out (Join-Path $rel "size-report$suffix.md") --installer $exe | Out-Host
if ($LASTEXITCODE) { throw 'size report failed' }

# ---------------------------------------------------------------- 11. espeak-ng source archive (GPL-3 source offer)
Step 'espeak-ng source archive'
$lock = Get-Content (Join-Path $src 'Data.Build\espeak-ng.lock.json') -Raw | ConvertFrom-Json
$ec = $lock.espeak.commit
$tgz = Join-Path $rel "espeak-ng-$ec.tar.gz"
if ($EspeakSourceArchive) {
  if ((Split-Path $EspeakSourceArchive -Leaf) -ne "espeak-ng-$ec.tar.gz") { throw "-EspeakSourceArchive must be espeak-ng-$ec.tar.gz (espeak-ng.lock.json)" }
  Copy-Item $EspeakSourceArchive $tgz -Force
} elseif (-not (Test-Path $tgz)) {
  if (-not (Test-Path (Join-Path $EspeakSrc '.git'))) { throw "espeak-ng clone not found: $EspeakSrc (build_espeak.ps1, or pass -EspeakSrc / -EspeakSourceArchive)" }
  & git -c safe.directory=* -C $EspeakSrc archive --format=tar.gz --prefix="espeak-ng-$ec/" -o $tgz $ec
  if ($LASTEXITCODE) { throw 'git archive failed' }
}

# ---------------------------------------------------------------- 12. signatures, build record, SHA256SUMS
Step 'signatures'
# Authenticode status of every PE file we ship and of the installer (plan 13; Phase B signs our own files):
# "<status>  <signer CN or ->  <path in the stage>"
$sigLines = @(@(Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' } | ForEach-Object { $_.FullName }) + @($exe) |
  ForEach-Object {
    $s = Get-AuthenticodeSignature $_
    $who = if ($s.SignerCertificate) { ($s.SignerCertificate.Subject -replace '^CN=("[^"]*"|[^,]*).*$', '$1').Trim('"') } else { '-' }
    $name = if ($_ -eq $exe) { Split-Path $exe -Leaf } else { $_.Substring($stage.Length + 1) }
    [pscustomobject]@{ Name = $name; Line = '{0}  {1}  {2}' -f $s.Status, $who, $name }
  } | Sort-Object Name -CaseSensitive | ForEach-Object { $_.Line })
$sigs = Join-Path $rel "signatures$suffix.txt"
[IO.File]::WriteAllText($sigs, (($sigLines -join "`n") + "`n"), $utf8)
$own = @($sigLines | Where-Object { $_ -match '  (Pepite\.(exe|dll)|Predict\.[^\\]*\.dll|Pepite-.*-installation\.exe)$' })
Write-Host "  $($sigLines.Count) PE file(s); ours ($($own.Count)): $(($own | ForEach-Object { $_.Split(' ')[0] } | Group-Object | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', ')"
Step 'build record'
$vc = @(Get-ChildItem (Join-Path $out 'vcruntime') -Filter *.dll | Sort-Object Name | ForEach-Object { "  $($_.Name) $($_.VersionInfo.FileVersionRaw) $(Sha $_.FullName)" })
$py = (& python --version 2>&1 | Out-String).Trim()
$runner = if ($env:GITHUB_ACTIONS -eq 'true') {
  @("runner image $env:ImageOS $env:ImageVersion ($env:RUNNER_OS $env:RUNNER_ARCH)",
    "run $env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID (attempt $env:GITHUB_RUN_ATTEMPT)")
} else { @('local build (not GitHub Actions)') }
$info = @(
  "Pepite $Version$suffix", "commit $commit$(if ($dirty) { ' (+ uncommitted changes: test build only)' })",
  "commit date (TouchDate/TouchTime, UTC) $touchDate $touchTime", "dotnet SDK $sdk", $py,
  "Inno Setup $((Get-Content (Join-Path $PSScriptRoot 'innosetup.lock.json') -Raw | ConvertFrom-Json).version)") + $runner + @(
  "forbidden-term pass of the release lint: $(if ($NoForbiddenList) { 'SKIPPED (-NoForbiddenList: CI build; the full lint runs on the local rebuild before publishing)' } else { 'done (list kept outside the repository)' })",
  "size report compared with: $(if ($compareOld) { Split-Path $OldRelease -Leaf } else { 'none' })",
  "espeak-ng source archive: $(if ($EspeakSourceArchive) { 'from the data release' } else { 'git archive of the local clone' })",
  'Visual C++ runtime (app-local, stage_vcruntime.ps1):') + $vc + @(
  "prediction.sqlite $dbHash", "installer $(Sha $exe)")
$infoPath = Join-Path $rel "BUILD-INFO$suffix.txt"
[IO.File]::WriteAllText($infoPath, (($info -join "`n") + "`n"), $utf8)
Step 'SHA256SUMS'
$sums = @($exe, $sym, $list, (Join-Path $rel "LISEZ-MOI$suffix.txt"), (Join-Path $rel 'DEPLOIEMENT.txt'),
          (Join-Path $rel 'exemple-etablissement.json'), $tgz, $infoPath, $sigs) | Where-Object { Test-Path $_ } |
  ForEach-Object { "$(Sha $_) *$(Split-Path $_ -Leaf)" }
[IO.File]::WriteAllText((Join-Path $rel "SHA256SUMS$suffix.txt"), (($sums -join "`n") + "`n"), $utf8)
$sums | ForEach-Object { Write-Host "  $_" }

# ---------------------------------------------------------------- 13. -VerifyReproducible: a second build, compared
if ($VerifyReproducible) {
  Step 'reproducibility: second build'
  $stage2 = "$stage-repro"; $rel2 = Join-Path $WorkDir "build\release-$Version$suffix-repro"
  Build-Stage $stage2
  Get-ChildItem $stage2 -Recurse -File -Filter *.pdb | Remove-Item -Force
  $list2 = Join-Path $rel2 "files$suffix.sha256"
  New-Item -ItemType Directory -Force $rel2 | Out-Null
  Write-FileList $stage2 $list2
  $exe2 = Invoke-Iscc $stage2 $rel2
  $d = @(Compare-Object (Get-Content $list) (Get-Content $list2))
  $same = (Sha $exe) -eq (Sha $exe2)
  $r = @("files.sha256: $(if ($d.Count) { "DIFFERENT ($($d.Count) lines)" } else { 'identical' })",
         "installer: $(if ($same) { 'byte-identical' } else { "DIFFERENT: $(Sha $exe) vs $(Sha $exe2)" })")
  $r += $d | Select-Object -First 20 | ForEach-Object { "  $($_.SideIndicator) $($_.InputObject)" }
  [IO.File]::WriteAllText((Join-Path $rel "reproducibility$suffix.txt"), (($r -join "`n") + "`n"), $utf8)
  $r | ForEach-Object { Write-Host "  $_" }
  if ($d.Count) { throw 'files.sha256 differs between two builds of the same commit' }
}
Step "done: $exe"
