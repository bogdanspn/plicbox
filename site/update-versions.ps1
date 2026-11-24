# Writes the published versions into the website, from the GitHub releases (needs gh, signed in):
# - changes.html: every version, newest first, with its date and release notes;
# - index.html: the newest version and its date under the Download buttons;
# - index.html: the newest version's size, start time and memory, from perf/history.csv;
# - sitemap.xml: today's date for both pages.
# release.ps1 runs it after each upload; it can also be run by hand. Then commit and deploy (webdisk/deploy.ps1).
# Visitors' browsers never ask GitHub for anything: the versions are written into the pages here.

$ErrorActionPreference = "Stop"
$repo = "bogdanspn/plicbox"
# gh writes UTF-8; a console on its usual code page would read arrows and accents as something else.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$en = [Globalization.CultureInfo]::GetCultureInfo("en-GB")
function Html([string]$text) { $text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") }
function Write-Utf8([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false)) }

$releases = gh release list --repo $repo --limit 200 --exclude-drafts --exclude-pre-releases --json tagName,publishedAt | ConvertFrom-Json |
    Sort-Object { [datetime]$_.publishedAt } -Descending
if (-not $releases) { throw "No releases found in $repo" }

$blocks = foreach ($release in $releases) {
    $version = $release.tagName.TrimStart("v")
    $date = ([datetime]$release.publishedAt).ToLocalTime()
    $body = (gh release view $release.tagName --repo $repo --json body | ConvertFrom-Json).body
    # The notes are a short list ("- one change" per line); anything else becomes a paragraph.
    $items = @(); $paragraphs = @()
    foreach ($line in ($body -split "`r?`n")) {
        $line = $line.Trim()
        if (-not $line) { continue }
        $text = Html (($line -replace '^[-*]\s+', '') -replace ' [\u2192] ', ' > ')
        if ($line -match '^[-*]\s+') { $items += "      <li>$text</li>" } else { $paragraphs += "    <p>$text</p>" }
    }
    $notes = ($paragraphs + $(if ($items) { @("    <ul>") + $items + @("    </ul>") })) -join "`n"
    if (-not $notes) { $notes = if ($release -eq $releases[-1]) { "    <p>The first version.</p>" } else { "    <p>Fixes and small improvements.</p>" } }
    @"
  <section class="release" id="v$version">
    <h2>$version <span class="when">$(Html $date.ToString("d MMMM yyyy", $en))</span></h2>
$notes
  </section>
"@
}

$site = $PSScriptRoot
$changes = Join-Path $site "changes.html"
$text = [IO.File]::ReadAllText($changes) -replace "`r`n", "`n"
$text = [regex]::Replace($text, '(?s)(<!-- releases:[^\n]*-->\n).*?(<!-- /releases -->)', { param($m) $m.Groups[1].Value + ($blocks -join "`n`n") + "`n" + $m.Groups[2].Value })
Write-Utf8 $changes $text

$latest = $releases[0]
$version = $latest.tagName.TrimStart("v")
$date = ([datetime]$latest.publishedAt).ToLocalTime().ToString("d MMMM yyyy", $en)
$index = Join-Path $site "index.html"
$text = [IO.File]::ReadAllText($index) -replace "`r`n", "`n"
$text = [regex]::Replace($text, '(?s)(<!-- version -->).*?(<!-- /version -->)',
    { param($m) $m.Groups[1].Value + "Version $version, $date. <a href=`"changes.html`">What's new</a>" + $m.Groups[2].Value })

# The numbers under "Measured, every release": a tile each, with a column per version (the newest in the accent colour).
$inv = [Globalization.CultureInfo]::InvariantCulture
function Num($value) { [double]::Parse($value, $inv) }
function Show($value, [string]$format) { ([double]$value).ToString($format, $inv) }
# Rounded first, so the two figures shown beside it add up to it.
function Memory($row) { [math]::Round((Num $row.appPrivateMb)) + [math]::Round((Num $row.webViewPrivateMb)) }
function Columns($points, [string]$label) {
    # Columns from zero, so a small change looks small. One point isn't a trend.
    $points = @($points | Select-Object -Last 14)
    if ($points.Count -lt 2) { return "" }
    $max = ($points.value | Measure-Object -Maximum).Maximum
    $bars = for ($i = 0; $i -lt $points.Count; $i++) {
        $height = [math]::Max(4, [math]::Round(40 * $points[$i].value / $max, 1))
        $x = $i * 16
        $side = Show ($height - 3) '0.#'
        $class = if ($i -eq $points.Count - 1) { ' class="now"' } else { "" }
        "<g$class><title>$($points[$i].version): $($points[$i].text)</title><rect class=`"hit`" x=`"$x`" width=`"16`" height=`"40`"/><path d=`"M$($x + 2) 40v-${side}q0-3 3-3h6q3 0 3 3v${side}z`"/></g>"
    }
    $width = $points.Count * 16
    $summary = "${label}: $($points[0].text) in $($points[0].version), $($points[-1].text) in $($points[-1].version)"
    "<svg class=`"columns`" viewBox=`"0 0 $width 40`" width=`"$width`" height=`"40`" role=`"img`" aria-label=`"$summary`">$($bars -join '')</svg>" +
    "<span class=`"range`" style=`"width:${width}px`"><span>$($points[0].version)</span><span>$($points[-1].version)</span></span>"
}
function Tile([string]$name, [string]$value, [string]$unit, [string]$note, [string]$chart) {
    "        <div><h3>$name</h3><p class=`"figure`">$value <span>$unit</span></p><p>$note</p>$chart</div>"
}
$history = @(Import-Csv (Join-Path $site "../perf/history.csv"))
$now = $history[-1]
if ($now.version -ne $version) { Write-Host "perf/history.csv ends at $($now.version): the numbers on the home page are that version's." -ForegroundColor Yellow }
# Start time and memory are a median of several runs (runs > 1). Up to 0.1.18 they're one run straight after the build,
# slower to start: shown in the charts alike, but the figures above them are always a median.
$timed = @($history | Where-Object startupMs)
$steady = @($history | Where-Object { [int]("0" + $_.runs) -gt 1 })
$setups = $history | Where-Object setupMb | ForEach-Object { @{ version = $_.version; value = Num $_.setupMb; text = "$($_.setupMb) MB" } }
$installs = $history | Where-Object appFolderMb | ForEach-Object { @{ version = $_.version; value = Num $_.appFolderMb; text = "$($_.appFolderMb) MB" } }
$starts = $timed | ForEach-Object { $s = (Num $_.startupMs) / 1000; @{ version = $_.version; value = $s; text = "$(Show $s '0.0') s" } }
$memory = $timed | ForEach-Object { $mb = Memory $_; @{ version = $_.version; value = $mb; text = "$(Show $mb '0') MB" } }
$tiles = @(
    (Tile "Download" (Show (Num $now.setupMb) '0.0') "MB" "The installer. Updates fetch only what changed." (Columns $setups "Installer size"))
    (Tile "On disk" (Show (Num $now.appFolderMb) '0.0') "MB" "Installed, before your mail." (Columns $installs "Installed size"))
    if ($steady) {
        $last = $steady[-1]
        (Tile "Start" (Show ((Num $last.startupMs) / 1000) '0.0') "s" "From opening Plicbox to the mail list on screen." (Columns $starts "Start time"))
        (Tile "Memory" (Show (Memory $last) '0') "MB" "$(Show (Num $last.appPrivateMb) '0') for Plicbox, $(Show (Num $last.webViewPrivateMb) '0') for WebView2, which shows the messages." (Columns $memory "Memory"))
    }
)
$runs = if ($steady) { "Start time and memory are the <a href=`"https://github.com/$repo/tree/main/perf`">median of $($steady[-1].runs) runs</a> with a small demo mailbox, on the PC Plicbox is built on, so yours will differ." } else { "" }
$perf = @"
      <div class="numbers">
$($tiles -join "`n")
      </div>
      <p class="fine">Version $($now.version). $runs</p>
"@ -replace "`r`n", "`n"
$text = [regex]::Replace($text, '(?s)(<!-- perf:[^\n]*-->\n).*?( *<!-- /perf -->)', { param($m) $m.Groups[1].Value + $perf + "`n" + $m.Groups[2].Value })
Write-Utf8 $index $text

$sitemap = Join-Path $site "sitemap.xml"
$text = [IO.File]::ReadAllText($sitemap) -replace "`r`n", "`n"
$today = (Get-Date).ToString("yyyy-MM-dd")
foreach ($page in "/", "/changes.html") {
    $text = [regex]::Replace($text, "(<loc>https://[^<]*$([regex]::Escape($page))</loc><lastmod>)[^<]*", "`${1}$today")
}
Write-Utf8 $sitemap $text

Write-Host "Website: $($releases.Count) versions in changes.html, $version on the home page." -ForegroundColor Green
