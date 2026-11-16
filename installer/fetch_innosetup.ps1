<#
.SYNOPSIS
  Download and install the pinned Inno Setup compiler (plan 12 step 1) into <work>\tools, without admin rights.

.DESCRIPTION
  -WorkDir <work>: the local bulk-data folder outside the repository (or $env:PEPITE_WORK). The download goes to
  <work>\tools\innosetup-dl\, the compiler to <work>\tools\innosetup-<version>\.
  -Pin: first run (or a deliberate update): requires a valid Authenticode signature on the download and writes the
  signer, the SHA-256 and the date into innosetup.lock.json. Without -Pin the download must have the pinned SHA-256
  ("sha256 mismatch" otherwise) - the signature is checked again and must name the pinned signer.
  The installer is Inno Setup's own: /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /NOICONS /DIR=... /PORTABLE=1 (the
  portable switch of Inno Setup's own setup; ignored if unknown). Prints the path of ISCC.exe on the last line.
  ASCII only (Windows PowerShell 5.1).
#>
param([string]$WorkDir = $env:PEPITE_WORK, [switch]$Pin, [switch]$NoInstall)
$ErrorActionPreference = 'Stop'
if (-not $WorkDir) { throw 'give -WorkDir <work> (or set PEPITE_WORK): the bulk-data folder outside the repository' }
$lockPath = Join-Path $PSScriptRoot 'innosetup.lock.json'
$lock = Get-Content $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
$ver = $lock.version
$dl = Join-Path $WorkDir 'tools\innosetup-dl'
$dest = Join-Path $WorkDir "tools\innosetup-$ver"
$iscc = Join-Path $dest 'ISCC.exe'
New-Item -ItemType Directory -Force $dl | Out-Null
$file = Join-Path $dl ([IO.Path]::GetFileName($lock.url))

if (-not (Test-Path $file)) {
  Write-Host "downloading $($lock.url)"
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $tmp = "$file.part"
  Invoke-WebRequest -Uri $lock.url -OutFile $tmp -UseBasicParsing
  Move-Item $tmp $file -Force
}
$hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
$sig = Get-AuthenticodeSignature $file
$signer = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '' }

if ($lock.expectedSigner -and $signer -ne $lock.expectedSigner) { throw "unexpected signer '$signer' (expected '$($lock.expectedSigner)')" }
if ($Pin) {
  if ($sig.Status -ne 'Valid') { throw "Authenticode signature not valid ($($sig.Status)): $file" }
  $lock.sha256 = $hash
  $lock.signer = $signer
  $lock.pinned = (Get-Date -Format 'yyyy-MM-dd')
  $json = $lock | ConvertTo-Json -Depth 3
  [IO.File]::WriteAllText($lockPath, $json + "`n", (New-Object Text.UTF8Encoding $false))
  Write-Host "pinned $ver sha256 $hash signer '$signer'"
} else {
  if (-not $lock.sha256) { throw 'innosetup.lock.json has no sha256: run once with -Pin' }
  if ($hash -ne $lock.sha256) { throw "sha256 mismatch: $file is $hash, the lock says $($lock.sha256)" }
  if ($sig.Status -ne 'Valid' -or $signer -ne $lock.signer) { throw "signature changed ($($sig.Status), '$signer')" }
  Write-Host "verified $ver sha256 $hash"
}

if (-not $NoInstall -and -not (Test-Path $iscc)) {
  $log = Join-Path $dl "install-$ver.log"
  $p = Start-Process -FilePath $file -Wait -PassThru -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/CURRENTUSER', '/NOICONS', '/NORESTART', '/PORTABLE=1', "/DIR=`"$dest`"", "/LOG=`"$log`"")
  if ($p.ExitCode -ne 0) { throw "Inno Setup install failed, exit $($p.ExitCode) (log: $log)" }
}
if (-not (Test-Path $iscc)) { throw "ISCC.exe not found in $dest" }
$iscc
