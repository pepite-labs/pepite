<#
.SYNOPSIS
  Stage the four Visual C++ 2015-2022 x64 runtime DLLs that onnxruntime.dll imports, for an app-local copy next to the exe
  (plan 08a, assumption A13).

.DESCRIPTION
  onnxruntime.dll imports MSVCP140.dll, MSVCP140_1.dll, VCRUNTIME140.dll and VCRUNTIME140_1.dll. A clean Windows (the
  test VM) has none of them, and the self-contained .NET publish brings only its own vcruntime140_cor3.dll: the Piper voice
  then fails to load and the app speaks with SAPI. Microsoft's redistribution terms allow these files to ship app-local
  with a program that uses them.

  The files are taken from a Windows where the official "Microsoft Visual C++ Redistributable 2015-2022 (x64)" is
  installed (-Source, default C:\Windows\System32), and each must be Authenticode-signed by Microsoft with file version
  >= 14.40. They are copied to Data.Build\out\vcruntime\ (git-ignored) with a SOURCE.txt (versions, SHA-256, signer);
  Predict.Keyboard.csproj copies them next to the exe when that folder exists. Reversible: delete the folder to ship
  without them (then the LISEZ-MOI asks for the redistributable).
#>
param(
    [string]$Source = (Join-Path $env:SystemRoot 'System32'),
    [string]$Out = (Join-Path $PSScriptRoot 'out\vcruntime')
)
$ErrorActionPreference = 'Stop'
$files = 'msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll'
$min = [version]'14.40.0.0'
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out | Out-Null
$lines = @('Visual C++ 2015-2022 x64 runtime (Microsoft), app-local copy for onnxruntime.dll; staged by Data.Build/stage_vcruntime.ps1',
           "from $Source on $(Get-Date -Format yyyy-MM-dd) (installed by the official Microsoft Visual C++ Redistributable).",
           'Redistributed under the Visual Studio redistribution terms (https://learn.microsoft.com/visualstudio/releases/2022/redistribution).',
           'file version sha256 signer:')
foreach ($f in $files) {
    $p = Join-Path $Source $f
    if (-not (Test-Path $p)) { throw "$p missing: install the Visual C++ Redistributable 2015-2022 x64 on this machine first" }
    $v = [version](Get-Item $p).VersionInfo.FileVersionRaw
    if ($v -lt $min) { throw "$f is $v, need >= $min" }
    $sig = Get-AuthenticodeSignature $p
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw "${f}: signature $($sig.Status) $($sig.SignerCertificate.Subject)" }
    Copy-Item $p (Join-Path $Out $f)
    $lines += '  {0} {1} {2} {3}' -f $f, $v, (Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant(), ($sig.SignerCertificate.Subject -replace ',.*$', '')
}
[IO.File]::WriteAllLines((Join-Path $Out 'SOURCE.txt'), $lines, (New-Object Text.UTF8Encoding $false))
Get-Content (Join-Path $Out 'SOURCE.txt')
