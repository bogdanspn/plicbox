# Writes the published versions into the website, from the GitHub releases (needs gh, signed in):
# - changes.html: every version, newest first, with its date and release notes;
# - index.html: the newest version and its date under the Download buttons;
# - sitemap.xml: today's date for both pages.
# release.ps1 runs it after each upload; it can also be run by hand. Then commit and deploy (webdisk/deploy.ps1).
# Visitors' browsers never ask GitHub for anything: the versions are written into the pages here.

$ErrorActionPreference = "Stop"
$repo = "bogdanspn/plicbox"
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
        $text = Html (($line -replace '^[-*]\s+', '') -replace ' > ', ' → ')
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
Write-Utf8 $index $text

$sitemap = Join-Path $site "sitemap.xml"
$text = [IO.File]::ReadAllText($sitemap) -replace "`r`n", "`n"
$today = (Get-Date).ToString("yyyy-MM-dd")
foreach ($page in "/", "/changes.html") {
    $text = [regex]::Replace($text, "(<loc>https://[^<]*$([regex]::Escape($page))</loc><lastmod>)[^<]*", "`${1}$today")
}
Write-Utf8 $sitemap $text

Write-Host "Website: $($releases.Count) versions in changes.html, $version on the home page." -ForegroundColor Green
