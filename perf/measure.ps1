<#
.SYNOPSIS
    Measures a Plicbox build and compares it with the last release: download and install size, how long until the
    mail list is up, and memory (Plicbox itself and the WebView2 processes that show messages), all with the demo's
    data. release.ps1 runs it on every release and records the numbers in perf\history.csv, and the website shows them.

    Start time and memory are the median of several runs. The first run of a new build is left out: it takes over twice
    as long as any later one. Rows with runs = 1 (up to 0.1.18) are that single first run, so they don't compare.
.EXAMPLE
    .\perf\measure.ps1 -Version 0.1.9 -PublishDir artifacts\publish -ReleasesDir artifacts\releases -Record
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$PublishDir,
    [string]$ReleasesDir,
    [int]$Runs = 5,
    [switch]$Record
)

$ErrorActionPreference = 'Stop'
$history = Join-Path $PSScriptRoot 'history.csv'
# Figures go into the CSV with a decimal point whatever the Windows region (a comma would read back as a thousand times more).
$invariant = [Globalization.CultureInfo]::InvariantCulture
function Num($value) { ([double]$value).ToString($invariant) }
function Mb([long]$bytes) { Num ([math]::Round($bytes / 1MB, 1)) }

# ─── Sizes ───
$files = Get-ChildItem $PublishDir -Recurse -File
$now = [ordered]@{
    version = $Version
    date = (Get-Date).ToString('yyyy-MM-dd')
    setupMb = ''
    packageMb = ''
    appFolderMb = Mb ($files | Measure-Object Length -Sum).Sum
    files = $files.Count
    startupMs = ''
    appPrivateMb = ''
    webViewPrivateMb = ''
    managedMb = ''
    runs = ''
}
if ($ReleasesDir) {
    $setup = Get-ChildItem $ReleasesDir -Filter '*-Setup.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    $package = Get-ChildItem $ReleasesDir -Filter "*-$Version-full.nupkg" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($setup) { $now.setupMb = Mb $setup.Length }
    if ($package) { $now.packageMb = Mb $package.Length }
}

# ─── Start time and memory: the build itself, run with the demo's data (a window opens for about 20 seconds each time) ───
$report = Join-Path ([IO.Path]::GetTempPath()) "plicbox-measure-$PID.json"
$measured = @(foreach ($run in 0..$Runs) {
    Remove-Item $report -ErrorAction SilentlyContinue
    $app = Start-Process (Join-Path $PublishDir 'Plicbox.App.exe') -ArgumentList @('--demo', '--measure', $report) -PassThru
    if (-not $app.WaitForExit(120000)) { $app.Kill(); Write-Host '  A measuring run didn''t finish within 2 minutes.' -ForegroundColor Yellow }
    if (Test-Path $report) {
        $result = Get-Content $report -Raw | ConvertFrom-Json
        Remove-Item $report
        if ($run -gt 0) { $result } # run 0 only warms up
    }
})
function Median([string]$name) {
    $sorted = @($measured.$name | Sort-Object)
    $middle = [math]::Floor($sorted.Count / 2)
    [math]::Round($(if ($sorted.Count % 2) { $sorted[$middle] } else { ($sorted[$middle - 1] + $sorted[$middle]) / 2 }), 1)
}
if ($measured.Count) {
    $now.runs = $measured.Count
    foreach ($key in 'startupMs', 'appPrivateMb', 'webViewPrivateMb', 'managedMb') { $now[$key] = Num (Median $key) }
    Write-Host "  Median of $($measured.Count) runs; message display ranged $(($measured.webViewPrivateMb | Measure-Object -Minimum).Minimum) to $(($measured.webViewPrivateMb | Measure-Object -Maximum).Maximum) MB." -ForegroundColor DarkGray
}
else {
    Write-Host '  No memory figures: the demo didn''t report back (is a Plicbox demo already open?).' -ForegroundColor Yellow
}

# ─── Compared with the last release recorded (a re-run of the same version replaces its row) ───
$rows = @(if (Test-Path $history) { Import-Csv $history | Where-Object version -ne $Version })
$before = $rows | Select-Object -Last 1
# How much growth is worth a warning: sizes barely vary between builds, start time and memory a bit more.
$limits = [ordered]@{
    setupMb = @('Setup.exe', 'MB', 0.05); packageMb = @('Update package', 'MB', 0.05); appFolderMb = @('Installed app', 'MB', 0.05)
    startupMs = @('Until the mail list is up', 'ms', 0.25); appPrivateMb = @('Memory: Plicbox', 'MB', 0.10)
    webViewPrivateMb = @('Memory: message display', 'MB', 0.15); managedMb = @('Memory: .NET objects', 'MB', 0.15)
}
$warnings = 0
Write-Host ("  {0,-28}{1,12}{2,12}" -f '', $(if ($before) { $before.version } else { '' }), $Version)
foreach ($key in $limits.Keys) {
    $label, $unit, $limit = $limits[$key]
    $value = $now[$key]
    if ($value -eq '') { continue }
    # One first run (runs = 1) and a median of warm ones don't compare; sizes always do.
    $comparable = $key -in 'setupMb', 'packageMb', 'appFolderMb' -or [int]('0' + $before.runs) -gt 1
    $was = if ($before -and $comparable -and $before.$key) { [double]::Parse($before.$key, $invariant) } else { $null }
    $change = if ($was) { ([double]::Parse($value, $invariant) - $was) / $was } else { $null }
    $line = "  {0,-28}{1,12}{2,12}" -f $label, $(if ($null -ne $was) { "$was $unit" } else { '' }), "$value $unit"
    if ($null -ne $change) { $line += "  {0:+0.0;-0.0;0}%" -f ($change * 100) }
    if ($null -ne $change -and $change -gt $limit) {
        Write-Host "$line  ← grew more than $($limit * 100)%" -ForegroundColor Yellow
        $warnings++
    }
    else { Write-Host $line }
}
if ($warnings) { Write-Host "  $warnings figure(s) grew noticeably since $($before.version): worth a look before the next release." -ForegroundColor Yellow }

if ($Record) {
    @($rows) + [pscustomobject]$now | Export-Csv $history -NoTypeInformation -Encoding utf8NoBOM -UseQuotes AsNeeded
    Write-Host "  Recorded in perf\history.csv" -ForegroundColor DarkGray
}
