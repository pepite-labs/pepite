<#
.SYNOPSIS
  Regenerate the bar pictures of the embedded help (freeware\aide\img\barre-*.png) with the app itself
  (plan « aide intégrée », owner Q2).

.DESCRIPTION
  Each picture is drawn by Pepite.exe --render-bar: the real bar (BarBuilder, the bundled fonts) rendered into a PNG,
  with no hook, no window shown and nothing injected (App.RenderBar returns before the single-instance guard, the
  hooks and the tray). --no-pictures always: the help contains no ARASAAC pictogram (owner Q2), the drawings of the
  help are our own.

  Run it after a change of the bar's look, from a Release build that has its fonts (Data.Build\fetch_fonts.py, then
  dotnet build Predict.Keyboard -c Release); the PNGs are small and committed with the help. One render at a time.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\Predict.Keyboard\bin\Release\net6.0-windows\Pepite.exe'),
    [string]$Out = (Join-Path $PSScriptRoot '..\..\aide\img'),
    [int]$Dpi = 144
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "$Exe missing: dotnet build Predict.Keyboard -c Release first" }
$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

# name = arguments after --render-bar <png> (words last); one word per picture: --render-bar colours the words in turn
# (blue, green, orange), which would tell the wrong story in a help that explains the colours
$renders = [ordered]@{
    'barre-maison'                 = @('maison')
    'barre-marques-connu'          = @('--mark', 'known', 'maison')
    'barre-marques-son'            = @('--mark', 'sounds', 'maison')
    'barre-marques-inconnu'        = @('--mark', 'unknown', 'le')
    'barre-polices-opendyslexic'   = @('--font', 'opendyslexic', 'maison')
    'barre-polices-lexend'         = @('--font', 'lexend', 'maison')
    'barre-polices-andika'         = @('--font', 'andika', 'maison')
    'barre-espacement-tres-large'  = @('--font', 'opendyslexic', '--spacing', 'widest', 'maison')
}
foreach ($name in $renders.Keys) {
    $png = Join-Path $Out "$name.png"
    $argv = @('--render-bar', "`"$png`"", '--no-pictures', '--dpi', "$Dpi") + $renders[$name]
    $p = Start-Process -FilePath $Exe -ArgumentList $argv -Wait -PassThru -NoNewWindow
    $report = "$png.txt"
    if ($p.ExitCode -ne 0) { throw "$name failed (exit $($p.ExitCode)): $(Get-Content -Raw $report -ErrorAction SilentlyContinue)" }
    $first = (Get-Content $report -TotalCount 2) -join ' | '
    Remove-Item $report   # the render report names local paths: never committed
    Write-Host "$name.png  $first"
}
