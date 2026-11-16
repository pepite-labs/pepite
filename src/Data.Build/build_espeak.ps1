<#
.SYNOPSIS
  Reproducible build of libespeak-ng.dll + the French/English espeak-ng data for the Piper voices (plan 08a, step 1).

.DESCRIPTION
  Piper (piper1-gpl 1.8.0) phonemizes with espeak-ng commit 724808c5 (1.52.0.1) and needs
  espeak_TextToPhonemesWithTerminator, which no official espeak-ng binary release has. This script builds that exact
  commit with a pinned MSYS2 UCRT64 gcc toolchain, entirely under the work folder (nothing installed on C:), checks the
  result and stages it to Data.Build/out/espeak (git-ignored), which Predict.Keyboard.csproj copies next to the exe.

  Stages: 1 toolchain (MSYS2 base + pinned packages, every file hash-checked against espeak-ng.lock.json; a mismatch
  deletes the file), 2 source (git clone + checkout of the pinned commit), 3 build (cmake/ninja in a UCRT64 shell),
  4 checks (DLL imports, export present, compiled data byte-identical to the Piper 1.8.0 wheel), 5 stage.

  -Work <dir> (else $env:PREDICT_BUILD_DIR) is required: there is no default, so the ~1.5 GB toolchain never lands on C:
  by accident. -Pin downloads the current toolchain packages once and prints the JSON to paste into the lock.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build_espeak.ps1 -Work D:\build\predict-espeak   (any folder with ~1.5 GB free)
#>
param(
    [string]$Work = $env:PREDICT_BUILD_DIR,
    [string]$Out = (Join-Path $PSScriptRoot 'out\espeak'),
    [switch]$Pin
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

if (-not $Work) { throw 'No work folder: pass -Work <dir> or set PREDICT_BUILD_DIR (about 1.5 GB; keep it off C:).' }
$lock = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'espeak-ng.lock.json') | ConvertFrom-Json
$downloads = Join-Path $Work 'downloads'
$msys = Join-Path $Work 'msys64'
$src = Join-Path $Work 'src'
$bld = Join-Path $Work 'build'
$inst = Join-Path $Work 'install'
New-Item -ItemType Directory -Force $downloads | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

# Download to <name>.part, check the hash, then rename; a mismatch deletes the file and stops (as fetch_fonts.py).
function Get-Pinned([string]$url, [string]$sha) {
    $name = [IO.Path]::GetFileName(([Uri]$url).AbsolutePath)
    $dest = Join-Path $downloads $name
    if (Test-Path -LiteralPath $dest) {
        if ((Get-Sha256 $dest) -eq $sha) { return $dest }
        Remove-Item -LiteralPath $dest
    }
    $part = "$dest.part"
    Write-Host "  fetch $name"
    (New-Object Net.WebClient).DownloadFile($url, $part)
    $got = Get-Sha256 $part
    if ($got -ne $sha) { Remove-Item -LiteralPath $part; throw "SHA-256 mismatch for ${name}: got $got, expected $sha" }
    Move-Item -LiteralPath $part $dest
    return $dest
}

# Run a bash script in the UCRT64 environment of the work folder's MSYS2 (a file, so PowerShell 5.1 never re-quotes it).
function Invoke-Ucrt64([string]$script) {
    $f = Join-Path $Work 'step.sh'
    [IO.File]::WriteAllText($f, "set -euo pipefail`n" + $script.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
    $env:MSYSTEM = 'UCRT64'; $env:CHERE_INVOKING = '1'; $env:MSYS2_PATH_TYPE = 'minimal'
    & (Join-Path $msys 'usr\bin\bash.exe') -l (ConvertTo-MsysPath $f)
    if ($LASTEXITCODE -ne 0) { throw "UCRT64 step failed (exit $LASTEXITCODE)" }
}

function ConvertTo-MsysPath([string]$p) {
    $full = [IO.Path]::GetFullPath($p)
    '/' + $full.Substring(0, 1).ToLowerInvariant() + $full.Substring(2).Replace('\', '/')
}

# ---- 1. toolchain ---------------------------------------------------------------------------------------------------
Write-Host '== 1. toolchain'
$base = Get-Pinned $lock.msys2.url $lock.msys2.sha256
if (-not (Test-Path (Join-Path $msys 'usr\bin\bash.exe'))) {
    Write-Host '  extract MSYS2 base'
    & tar.exe -xJf $base -C $Work
    if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
    Invoke-Ucrt64 'echo "MSYS2 first start done"'   # keyring init + profile
}
if ($Pin) {
    # One-time pinning: resolve the packages against the current repo and print lock entries.
    Invoke-Ucrt64 ("pacman -Sy >/dev/null`npacman -Sp --needed --print-format '%n %v %l' " + ($lock.toolchain -join ' ') + " > '" + (ConvertTo-MsysPath (Join-Path $Work 'pin.txt')) + "'")
    $entries = foreach ($line in Get-Content (Join-Path $Work 'pin.txt')) {
        $n, $v, $u = $line -split ' '
        $u = $u -replace '^https://mirror\.msys2\.org/', 'https://repo.msys2.org/'
        $name = [IO.Path]::GetFileName(([Uri]$u).AbsolutePath)
        $dest = Join-Path $downloads $name
        if (-not (Test-Path $dest)) { (New-Object Net.WebClient).DownloadFile($u, $dest) }
        [ordered]@{ name = $n; version = $v; url = $u; sha256 = (Get-Sha256 $dest) }
    }
    $entries | ConvertTo-Json -Depth 3
    return
}
$pkgs = foreach ($p in $lock.packages) { Get-Pinned $p.url $p.sha256 }
$want = ($lock.packages | ForEach-Object { "$($_.name) $($_.version)" }) | Sort-Object
Invoke-Ucrt64 ("pacman -Q > '" + (ConvertTo-MsysPath (Join-Path $Work 'pacman-q.txt')) + "'")
$have = Get-Content (Join-Path $Work 'pacman-q.txt')
if (@($want | Where-Object { $have -notcontains $_ }).Count -gt 0) {
    Write-Host '  install toolchain packages'
    Invoke-Ucrt64 ("pacman -U --noconfirm --needed " + (($pkgs | ForEach-Object { "'" + (ConvertTo-MsysPath $_) + "'" }) -join ' '))
    Invoke-Ucrt64 ("pacman -Q > '" + (ConvertTo-MsysPath (Join-Path $Work 'pacman-q.txt')) + "'")
    $have = Get-Content (Join-Path $Work 'pacman-q.txt')
}
$missing = @($want | Where-Object { $have -notcontains $_ })
if ($missing.Count -gt 0) { throw "toolchain packages not installed at the pinned version: $($missing -join ', ')" }
# Hard links of the packages (ld.exe, gcc.exe, liblto_plugin.dll…) are silently lost on a volume without hard links
# (exFAT): restore each missing one as a copy of its target (idempotent).
$pkgList = ($pkgs | ForEach-Object { "'" + (ConvertTo-MsysPath $_) + "'" }) -join ' '
Invoke-Ucrt64 (@'
restored=0
for f in PKGS; do
  while read -r dst src; do
    if [ ! -e "/$dst" ]; then mkdir -p "$(dirname "/$dst")"; cp -p "/$src" "/$dst"; restored=$((restored+1)); fi
  done < <(bsdtar -tvf "$f" | sed -n 's/^h.* \(ucrt64\/[^ ]*\) link to \(ucrt64\/[^ ]*\)$/\1 \2/p')
done
echo "  hard links restored as copies: $restored"
'@.Replace('PKGS', $pkgList))

# ---- 2. source ------------------------------------------------------------------------------------------------------
Write-Host '== 2. source'
$git = @('-c', 'safe.directory=*')
if (-not (Test-Path (Join-Path $src '.git'))) { & git @git clone -q $lock.espeak.repo $src; if ($LASTEXITCODE) { throw 'git clone failed' } }
& git @git -C $src checkout -q --force $lock.espeak.commit
if ($LASTEXITCODE) { & git @git -C $src fetch -q origin; & git @git -C $src checkout -q --force $lock.espeak.commit }
$head = (& git @git -C $src rev-parse HEAD).Trim()
if ($head -ne $lock.espeak.commit) { throw "espeak-ng HEAD is $head, expected $($lock.espeak.commit)" }

# ---- 3. build -------------------------------------------------------------------------------------------------------
Write-Host '== 3. build'
if (Test-Path $bld) { Remove-Item -Recurse -Force $bld }
if (Test-Path $inst) { Remove-Item -Recurse -Force $inst }
$cmakeArgs = ($lock.cmake | ForEach-Object { "'" + $_ + "'" }) -join ' '
# espeak's CMake fetches libsonic with git even when unused; git refuses a work tree on a volume without ownership
# (exFAT) unless it is a safe.directory, set here for this process only
Invoke-Ucrt64 @"
export GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=safe.directory GIT_CONFIG_VALUE_0='*'
cmake -S '$(ConvertTo-MsysPath $src)' -B '$(ConvertTo-MsysPath $bld)' -G Ninja $cmakeArgs
cmake --build '$(ConvertTo-MsysPath $bld)'
cmake --install '$(ConvertTo-MsysPath $bld)' --prefix '$(ConvertTo-MsysPath $inst)'
objdump -p '$(ConvertTo-MsysPath (Join-Path $inst 'bin\libespeak-ng.dll'))' > '$(ConvertTo-MsysPath (Join-Path $Work 'objdump.txt'))'
"@

# ---- 4. checks ------------------------------------------------------------------------------------------------------
Write-Host '== 4. checks'
$dll = Join-Path $inst 'bin\libespeak-ng.dll'
$dump = Get-Content (Join-Path $Work 'objdump.txt')
$imports = @($dump | Select-String -Pattern '^\s*DLL Name:\s*(\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value })
$bad = @($imports | Where-Object { $_ -notmatch '^(KERNEL32\.dll|USER32\.dll|ADVAPI32\.dll|api-ms-win-crt-[a-z0-9-]+\.dll)$' })
Write-Host "  imports: $($imports -join ', ')"
if ($bad.Count -gt 0) { throw "unexpected DLL imports: $($bad -join ', ')" }
if (-not ($dump | Select-String -SimpleMatch 'espeak_TextToPhonemesWithTerminator')) { throw 'export espeak_TextToPhonemesWithTerminator missing' }
$data = Join-Path $inst 'share\espeak-ng-data'
foreach ($p in $lock.data.PSObject.Properties) {
    $f = Join-Path $data $p.Name
    if (-not (Test-Path -LiteralPath $f)) { throw "data file missing: $($p.Name)" }
    $h = Get-Sha256 $f
    if ($h -ne $p.Value) { throw "data file $($p.Name) differs from the Piper 1.8.0 wheel: $h" }
}
Write-Host '  data: byte-identical to piper-tts 1.8.0'
$dllSha = Get-Sha256 $dll
if ($lock.dll_sha256 -and $dllSha -ne $lock.dll_sha256) { Write-Warning "libespeak-ng.dll SHA-256 $dllSha differs from the lock ($($lock.dll_sha256)); the golden test is the gate" }

# ---- 5. stage -------------------------------------------------------------------------------------------------------
Write-Host "== 5. stage -> $Out"
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force (Join-Path $Out 'espeak-ng-data') | Out-Null
Copy-Item $dll $Out
foreach ($p in $lock.data.PSObject.Properties) {
    $d = Join-Path (Join-Path $Out 'espeak-ng-data') $p.Name
    New-Item -ItemType Directory -Force (Split-Path $d) | Out-Null
    Copy-Item -LiteralPath (Join-Path $data $p.Name) $d
}
Copy-Item (Join-Path $src 'COPYING') (Join-Path $Out 'COPYING')
$staged = Get-ChildItem -Recurse -File $Out | Sort-Object FullName
$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine('espeak-ng (GPL-3.0-or-later), built by Data.Build/build_espeak.ps1 for Predict.Keyboard (Piper voices).')
[void]$sb.AppendLine("Source: $($lock.espeak.repo) commit $($lock.espeak.commit) (version 1.52.0.1, the commit piper1-gpl 1.8.0 builds)")
[void]$sb.AppendLine("Source archive: $($lock.espeak.repo -replace '\.git$', '')/archive/$($lock.espeak.commit).tar.gz")
[void]$sb.AppendLine("Toolchain: MSYS2 UCRT64 ($([IO.Path]::GetFileName($lock.msys2.url)) $($lock.msys2.sha256))")
foreach ($p in $lock.packages) { [void]$sb.AppendLine("  $($p.name) $($p.version)") }
[void]$sb.AppendLine("cmake -G Ninja $($lock.cmake -join ' ')")
[void]$sb.AppendLine('Staged: the shared library and the French + English data subset; full data and sources at the commit above.')
[void]$sb.AppendLine('SHA-256:')
foreach ($f in $staged) { [void]$sb.AppendLine("  $(Get-Sha256 $f.FullName)  $($f.FullName.Substring($Out.Length + 1).Replace('\', '/'))") }
[IO.File]::WriteAllText((Join-Path $Out 'SOURCE.txt'), $sb.ToString(), (New-Object Text.UTF8Encoding $false))
Get-ChildItem -Recurse -File $Out | Sort-Object FullName | ForEach-Object { '{0,10}  {1}' -f $_.Length, $_.FullName.Substring($Out.Length + 1) }
Write-Host "libespeak-ng.dll SHA-256 $dllSha"
