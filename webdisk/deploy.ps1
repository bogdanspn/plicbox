#Requires -Version 7.0
<#
    Uploads the website (site/) to https://plicbox.bogdanrosu.net over cPanel Web Disk (WebDAV).

    The same approach as wallnode's tools\deploy.ps1, for the same server and the same reasons: Explorer on a Web Disk
    drive is not a safe way to deploy (no Recycle Bin, a drag is a server-side move, the drive view is a cache), so this
    script only ever sends four things:
      PUT       write one file
      MKCOL     create a folder that does not exist yet
      PROPFIND  read a listing, to check what is there
      GET       read one file: .htaccess (see below)
    No DELETE, no MOVE, no COPY. A file deleted from site/ is listed at the end for you to remove by hand.

    The Web Disk is reached at the server's own name (https://bogdanrosu.ro:2078), as wallnode does: plicbox.bogdanrosu.net
    and every other name behind Cloudflare can't be used, because Cloudflare doesn't pass port 2078 through.

    What goes up is decided by git:
      - the files in site/ changed between the last deployed commit and HEAD (-All: every file in site/)
      - never anything with uncommitted changes, so what goes up is exactly a commit
      - never a file git doesn't track
      - never the notes and scripts in $neverDeploy
    On the server, site/x lands at x: the Web Disk address is the site's own folder.
    Every upload is read back with PROPFIND and its size compared; the deployed commit is recorded only when all match.

    .htaccess: if cPanel ever appends one of its "BEGIN cPanel-generated" blocks to the live file (it does when PHP
    settings are changed there), an upload carries those blocks over onto the local file instead of wiping them.

    Per-machine state lives in %APPDATA%\Plicbox\site-deploy, not in the repository:
      site.json       the Web Disk address of the site's folder
      credential.xml  the Web Disk login, encrypted with Windows DPAPI (only this Windows account can read it)
      deployed.txt    the commit that is live

    Usage:
        pwsh -File webdisk\deploy.ps1 -Setup          # once: address and login
        pwsh -File webdisk\deploy.ps1 -All            # the first time: every file in site/
        pwsh -File webdisk\deploy.ps1                 # deploy what changed since the last deploy
        pwsh -File webdisk\deploy.ps1 -DryRun         # show what a deploy would upload
        pwsh -File webdisk\deploy.ps1 -Check          # compare the server with site/
        pwsh -File webdisk\deploy.ps1 -Paths docs.html,media/hero-light.mp4   # just these (paths inside site/)
        pwsh -File webdisk\deploy.ps1 -MarkDeployed   # record HEAD as live, upload nothing
        pwsh -File webdisk\deploy.ps1 -InstallHook    # deploy by itself after each commit that touches site/
        pwsh -File webdisk\deploy.ps1 -RemoveHook

    Optional: set CLOUDFLARE_API_TOKEN (Zone → Cache Purge) and CLOUDFLARE_ZONE_ID, and Cloudflare forgets its cached
    copies of the files a deploy changed (it keeps pictures and videos for days otherwise).
#>

[CmdletBinding(DefaultParameterSetName = 'Deploy')]
param(
    [Parameter(ParameterSetName = 'Setup', Mandatory)] [switch] $Setup,
    [Parameter(ParameterSetName = 'Check', Mandatory)] [switch] $Check,
    [Parameter(ParameterSetName = 'Mark', Mandatory)] [switch] $MarkDeployed,
    [Parameter(ParameterSetName = 'Hook', Mandatory)] [switch] $InstallHook,
    [Parameter(ParameterSetName = 'Unhook', Mandatory)] [switch] $RemoveHook,

    # Upload every file in site/, not just what changed. For the first deploy to an empty server.
    [Parameter(ParameterSetName = 'Deploy')] [switch] $All,
    # Deploy the changes since this commit instead of the recorded one.
    [Parameter(ParameterSetName = 'Deploy')] [string] $Since,
    # Upload exactly these tracked files (paths inside site/). Doesn't move the recorded deployed commit.
    [Parameter(ParameterSetName = 'Deploy')] [string[]] $Paths,
    # List what would be uploaded, and stop.
    [Parameter(ParameterSetName = 'Deploy')] [switch] $DryRun,
    # Don't ask before uploading.
    [Parameter(ParameterSetName = 'Deploy')] [switch] $Yes
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$siteRel  = 'site'
$stateDir = Join-Path $env:APPDATA 'Plicbox\site-deploy'
$siteFile = Join-Path $stateDir 'site.json'
$credFile = Join-Path $stateDir 'credential.xml'
$markFile = Join-Path $stateDir 'deployed.txt'

$defaultAddress = 'https://bogdanrosu.ro:2078'
$liveSite = 'https://plicbox.bogdanrosu.net'
$homeMarker = 'plicbox-releases/releases/latest/download'  # only our home page has it (the Download button), and copy edits don't touch it
$checkPages = '', 'privacy.html', 'terms.html', 'docs.html'

# Tracked in site/, never on the server (matched against the path inside site/, case-insensitively).
$neverDeploy = @(
    '\.md$'     # README.md
    '\.ps1$'    # record-videos.ps1
    '(^|/)\.git'
)

function Step([string] $text) { Write-Host ''; Write-Host $text -ForegroundColor Cyan }

function Invoke-Git {
    $out = & git -C $repoRoot -c core.quotepath=off @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed." }
    return $out
}

# site/docs.html → docs.html; anything outside site/ → $null.
function Get-SitePath([string] $repoPath) {
    if ($repoPath -like "$siteRel/*") { return $repoPath.Substring($siteRel.Length + 1) }
    return $null
}

function Test-Deployable([string] $rel) {
    foreach ($re in $neverDeploy) { if ($rel -match $re) { return $false } }
    return $true
}

# ── Web Disk ─────────────────────────────────────────────────────────────────

$script:site = $null
$script:cred = $null
$script:knownDirs = [System.Collections.Generic.HashSet[string]]::new()
$script:retries = 0

$propfindBody = '<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:getcontentlength/><d:resourcetype/></d:prop></d:propfind>'

# Accepts https://host:2078/folder, or the address as Explorer shows it (\\host@SSL@2078\DavWWWRoot\folder).
function ConvertTo-DavUrl([string] $address) {
    $address = $address.Trim().Trim('"')
    if ($address -match '^\\\\([^\\@]+)(@SSL)?(?:@(\d+))?(?:\\DavWWWRoot)?(\\.*)?$') {
        if (-not $Matches[2]) { throw 'That address is plain HTTP. The login would travel unencrypted; use the SSL Web Disk address.' }
        $port = if ($Matches[3]) { ":$($Matches[3])" } else { '' }
        $address = "https://$($Matches[1])$port$(($Matches[4] ?? '').Replace('\', '/'))"
    }
    if ($address -notmatch '^https://') { throw "Expected an https:// address or an Explorer \\host@SSL@port path, got '$address'." }
    return $address.TrimEnd('/')
}

function Get-DavUri([string] $rel, [switch] $Folder) {
    $segments = @($rel.Replace('\', '/').Split('/', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [uri]::EscapeDataString($_) })
    $uri = $script:site + '/' + ($segments -join '/')
    if ($Folder -and $segments.Count) { $uri += '/' }
    return $uri
}

# The only function that talks to the server, and the only four methods it will send.
function Invoke-Dav([ValidateSet('PUT', 'MKCOL', 'PROPFIND', 'GET')] [string] $Method, [string] $Uri, [string] $InFile, [string] $Depth = '0') {
    $request = @{
        Uri = $Uri; CustomMethod = $Method; Credential = $script:cred; Authentication = 'Basic'
        SkipHttpErrorCheck = $true; TimeoutSec = 300
        # cpdavd answers "Connection: close" and closes; reusing the connection only gets resets (see wallnode).
        DisableKeepAlive = $true
    }
    if ($Method -eq 'PUT') { $request.InFile = $InFile; $request.ContentType = 'application/octet-stream' }
    if ($Method -eq 'PROPFIND') {
        $request.Headers = @{ Depth = $Depth }; $request.Body = $propfindBody; $request.ContentType = 'application/xml; charset=utf-8'
    }
    # The host resets about one new connection in twelve: retry quietly. Every method here is safe to repeat.
    $waits = 1, 5, 15
    for ($attempt = 0; ; $attempt++) {
        try { $response = Invoke-WebRequest @request; break }
        catch [System.Net.Http.HttpRequestException], [System.IO.IOException], [System.Threading.Tasks.TaskCanceledException] {
            if ($attempt -ge $waits.Count) { throw "$Method $Uri failed $($attempt + 1) times: the host kept resetting the connection. Try again in a few minutes. ($($_.Exception.Message))" }
            $script:retries++
            Start-Sleep -Seconds $waits[$attempt]
        }
    }
    if ($response.StatusCode -eq 401) { throw "Web Disk refused the login for '$($script:cred.UserName)' (401). Check the password on cPanel's Web Disk page, then run -Setup again." }
    return $response
}

function Get-DavFile([string] $rel) {
    $response = Invoke-Dav GET (Get-DavUri $rel)
    if ($response.StatusCode -eq 404) { return $null }
    if ($response.StatusCode -ne 200) { throw "GET $rel answered $($response.StatusCode)." }
    return $response.RawContentStream.ToArray()
}

# The file that should be on the server: the local one, except .htaccess, which keeps the live file's cPanel blocks.
$cpanelBlock = '(?s)(?:\r?\n)*# php -- BEGIN cPanel-generated handler, do not edit.*?# php -- END cPanel-generated handler, do not edit[^\n]*\n?'
$script:effective = @{}
function Get-EffectiveFile([string] $rel) {
    $local = Join-Path $repoRoot "$siteRel/$rel"
    if ($rel -ne '.htaccess') { return $local }
    if ($script:effective.ContainsKey($rel)) { return $script:effective[$rel] }
    $utf8 = [Text.UTF8Encoding]::new($false)
    $localText = [IO.File]::ReadAllText($local, $utf8)
    $liveBytes = Get-DavFile $rel
    $blocks = if ($liveBytes) { [regex]::Matches($utf8.GetString($liveBytes), $cpanelBlock) | ForEach-Object Value } else { @() }
    $path = $local
    if ($blocks -and -not [regex]::IsMatch($localText, $cpanelBlock)) {
        $path = Join-Path ([IO.Path]::GetTempPath()) "plicbox-site-deploy-$PID.htaccess"
        [IO.File]::WriteAllText($path, $localText.TrimEnd() + ($blocks -join ''), $utf8)
        Write-Host "  .htaccess        keeping $(@($blocks).Count) cPanel-generated block(s) from the live file" -ForegroundColor DarkGray
    }
    $script:effective[$rel] = $path
    return $path
}

# PROPFIND → @{ name = size } for a folder's entries (folders map to -1), or one file at Depth 0; $null if missing.
function Get-DavListing([string] $rel, [switch] $Folder) {
    $response = Invoke-Dav PROPFIND (Get-DavUri $rel -Folder:$Folder) -Depth $(if ($Folder) { '1' } else { '0' })
    if ($response.StatusCode -eq 404) { return $null }
    if ($response.StatusCode -ne 207) { throw "PROPFIND $rel answered $($response.StatusCode)." }
    $content = $response.Content
    if ($content -is [byte[]]) { $content = [Text.Encoding]::UTF8.GetString($content) }
    $xml = [xml]$content
    $ns = [System.Xml.XmlNamespaceManager]::new($xml.NameTable); $ns.AddNamespace('d', 'DAV:')
    $basePath = [uri]::UnescapeDataString(([uri]$script:site).AbsolutePath).TrimEnd('/')
    $selfPath = ($basePath + '/' + $rel.Replace('\', '/')).TrimEnd('/')
    $entries = @{}
    foreach ($node in $xml.SelectNodes('//d:response', $ns)) {
        $href = $node.SelectSingleNode('d:href', $ns).InnerText
        $path = [uri]::UnescapeDataString(([uri]::new([uri]$script:site, $href)).AbsolutePath).TrimEnd('/')
        $isDir = $null -ne $node.SelectSingleNode('.//d:resourcetype/d:collection', $ns)
        $size = -1
        if (-not $isDir) {
            $lengths = $node.SelectNodes('.//d:getcontentlength', $ns) | Where-Object { $_.InnerText -ne '' }
            $size = if ($lengths) { [long]@($lengths)[0].InnerText } else { -1 }
        }
        if ($Folder) { if ($path -ne $selfPath) { $entries[$path.Substring($path.LastIndexOf('/') + 1)] = $size } }
        else { $entries[$rel] = $size }
    }
    return $entries
}

function Get-RemoteSize([string] $rel) {
    $listing = Get-DavListing $rel
    if ($null -eq $listing) { return $null }
    return $listing[$rel]
}

function Confirm-DavFolder([string] $relDir) {
    $acc = ''
    foreach ($part in $relDir.Split('/', [StringSplitOptions]::RemoveEmptyEntries)) {
        $acc = if ($acc) { "$acc/$part" } else { $part }
        if ($script:knownDirs.Contains($acc)) { continue }
        $response = Invoke-Dav MKCOL (Get-DavUri $acc -Folder)
        if ($response.StatusCode -notin 201, 405) { throw "MKCOL $acc/ answered $($response.StatusCode)." }
        if ($response.StatusCode -eq 201) { Write-Host "  created folder   $acc/" }
        [void]$script:knownDirs.Add($acc)
    }
}

function Send-DavFile([string] $rel) {
    $local = Get-EffectiveFile $rel
    $uri = Get-DavUri $rel
    $response = Invoke-Dav PUT $uri -InFile $local
    if ($response.StatusCode -eq 409) {  # the parent folder doesn't exist yet
        Confirm-DavFolder $(if ($rel.Contains('/')) { $rel.Substring(0, $rel.LastIndexOf('/')) } else { '' })
        $response = Invoke-Dav PUT $uri -InFile $local
    }
    if ($response.StatusCode -notin 200, 201, 204) { throw "PUT $rel answered $($response.StatusCode)." }
}

function Import-Site {
    if (-not (Test-Path $siteFile) -or -not (Test-Path $credFile)) { throw 'Not set up on this machine. Run: pwsh -File webdisk\deploy.ps1 -Setup' }
    $script:site = (Get-Content $siteFile -Raw | ConvertFrom-Json).url
    $script:cred = Import-Clixml $credFile
}

function Write-Retries {
    if ($script:retries) { Write-Host "  $($script:retries) request(s) retried after the host reset the connection. Routine for this server; nothing was lost." -ForegroundColor DarkGray }
}

# ── after a deploy: Cloudflare's cache, and a look at the live site ────────────

function Clear-CloudflareCache([string[]] $rels) {
    $token = $env:CLOUDFLARE_API_TOKEN; $zone = $env:CLOUDFLARE_ZONE_ID
    if (-not $token -or -not $zone -or -not $rels) { return }
    $urls = @($rels | ForEach-Object { if ($_ -eq 'index.html') { "$liveSite/" }; "$liveSite/$_" })
    for ($i = 0; $i -lt $urls.Count; $i += 30) {  # 30 addresses per request
        $batch = $urls[$i..([math]::Min($i + 29, $urls.Count - 1))]
        Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$zone/purge_cache" -Headers @{ Authorization = "Bearer $token" } `
            -ContentType 'application/json' -Body (@{ files = $batch } | ConvertTo-Json) | Out-Null
    }
    Write-Host "  Cloudflare's cached copies of $($urls.Count) address(es) cleared." -ForegroundColor DarkGray
}

function Test-LiveSite {
    Step "Live check: $liveSite"
    $stamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()  # past Cloudflare's cache
    foreach ($page in $checkPages) {
        try {
            $r = Invoke-WebRequest "$liveSite/$page`?v=$stamp" -SkipHttpErrorCheck -TimeoutSec 30
            $ok = $r.StatusCode -eq 200
            Write-Host ("  {0,-3} /{1}" -f $r.StatusCode, $page) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
            if ($page -eq '' -and $ok -and $r.Content -notmatch [regex]::Escape($homeMarker)) {
                Write-Host '      the home page loads but isn''t this site: is the Web Disk address the site''s folder? (-Setup)' -ForegroundColor Yellow
            }
        } catch { Write-Host "  ✗   /$page   $($_.Exception.Message)" -ForegroundColor Red }
    }
}

# ── -Setup ───────────────────────────────────────────────────────────────────

if ($Setup) {
    Step 'Web Disk setup'
    Write-Host "  The Web Disk address of the site's folder. Enter for $defaultAddress (the server's own name;"
    Write-Host '  names behind Cloudflare, like plicbox.bogdanrosu.net, can''t reach port 2078).'
    $answer = Read-Host '  Address'
    $script:site = ConvertTo-DavUrl $(if ($answer) { $answer } else { $defaultAddress })
    $script:cred = Get-Credential -Message 'Web Disk login for plicbox.bogdanrosu.net (the cPanel Web Disk account, not the cPanel login)'

    $listing = Get-DavListing '' -Folder
    if ($null -eq $listing) { throw "$($script:site) doesn't exist on the server." }
    # A Web Disk account can be rooted at the cPanel home folder rather than the site: don't fill that with the site.
    if ($listing.ContainsKey('public_html') -and -not $listing.ContainsKey('index.html')) {
        throw "That folder has public_html/ in it, so it looks like a home folder, not the site's. Add the site's folder to the address (e.g. $($script:site)/plicbox.bogdanrosu.net)."
    }
    if ($listing.ContainsKey('index.html') -and -not $listing.ContainsKey('docs.html')) {
        Write-Host '  Note: there is an index.html but no docs.html. If this is another site''s folder, stop here.' -ForegroundColor Yellow
    }
    $shown = @($listing.Keys | Sort-Object | Select-Object -First 12)
    Write-Host "  In that folder now: $(if ($shown) { $shown -join ', ' } else { '(empty)' })"

    New-Item -ItemType Directory -Force $stateDir | Out-Null
    @{ url = $script:site } | ConvertTo-Json | Set-Content $siteFile -Encoding utf8
    $script:cred | Export-Clixml $credFile
    Write-Host ''
    Write-Host "Saved. Site folder: $($script:site)" -ForegroundColor Green
    Write-Host '  Next: pwsh -File webdisk\deploy.ps1 -All   (the first time), then just deploy.ps1'
    exit 0
}

# ── hooks: deploy after each commit that touches site/ ─────────────────────────

$hookFile = Join-Path $repoRoot '.git\hooks\post-commit'
$hookMark = '# plicbox site deploy'
if ($InstallHook) {
    if ((Test-Path $hookFile) -and -not ((Get-Content $hookFile -Raw) -match [regex]::Escape($hookMark))) {
        throw "$hookFile already exists and isn't this script's. Add a call to webdisk/deploy.ps1 -Yes to it by hand."
    }
    @(
        '#!/bin/sh'
        $hookMark
        '# Publishes the website when a commit changed something in site/. Remove with: pwsh -File webdisk/deploy.ps1 -RemoveHook'
        'if git diff-tree --no-commit-id --name-only -r HEAD | grep -q "^site/"; then'
        '  pwsh -NoProfile -File "$(git rev-parse --show-toplevel)/webdisk/deploy.ps1" -Yes'
        'fi'
    ) -join "`n" | Set-Content $hookFile -NoNewline -Encoding utf8NoBOM
    Write-Host 'Installed: each commit that touches site/ now publishes it. (-RemoveHook to stop.)' -ForegroundColor Green
    exit 0
}
if ($RemoveHook) {
    if ((Test-Path $hookFile) -and ((Get-Content $hookFile -Raw) -match [regex]::Escape($hookMark))) { Remove-Item $hookFile; Write-Host 'Removed.' -ForegroundColor Green }
    else { Write-Host 'No deploy hook installed.' }
    exit 0
}

# ── -MarkDeployed ────────────────────────────────────────────────────────────

if ($MarkDeployed) {
    $head = (Invoke-Git rev-parse HEAD).Trim()
    New-Item -ItemType Directory -Force $stateDir | Out-Null
    Set-Content $markFile $head -Encoding ascii
    Write-Host "Recorded $($head.Substring(0, 7)) as live. Nothing was uploaded." -ForegroundColor Green
    exit 0
}

Import-Site

# Every deployable file git tracks in site/, as paths inside site/.
function Get-TrackedSiteFiles {
    @((Invoke-Git ls-files -z -- $siteRel) -split "`0" | Where-Object { $_ } | ForEach-Object { Get-SitePath $_ } | Where-Object { $_ -and (Test-Deployable $_) })
}

# ── -Check ───────────────────────────────────────────────────────────────────

if ($Check) {
    $files = Get-TrackedSiteFiles
    $byDir = $files | Group-Object { if ($_.Contains('/')) { $_.Substring(0, $_.LastIndexOf('/')) } else { '' } }
    Step "Checking $($files.Count) files against $($script:site)"
    $missing = 0; $differs = 0; $matched = 0
    foreach ($group in @($byDir | Sort-Object Name)) {
        $listing = Get-DavListing $group.Name -Folder
        foreach ($rel in $group.Group) {
            $name = $rel.Substring($rel.LastIndexOf('/') + 1)
            if ($null -eq $listing -or -not $listing.ContainsKey($name)) { Write-Host "  missing          $rel" -ForegroundColor Red; $missing++; continue }
            $localSize = (Get-Item -LiteralPath (Get-EffectiveFile $rel)).Length
            if ($listing[$name] -eq $localSize) { $matched++; continue }
            Write-Host ("  differs          {0}   local {1}, live {2}" -f $rel, $localSize, $listing[$name]) -ForegroundColor Yellow
            $differs++
        }
    }
    Write-Host ''
    Write-Retries
    Write-Host "$matched match, $missing missing, $differs differ in size." -ForegroundColor $(if ($missing -or $differs) { 'Yellow' } else { 'Green' })
    Write-Host '  Size is compared, not content: a same-size edit reads as a match.'
    exit $(if ($missing -or $differs) { 1 } else { 0 })
}

# ── deploy ───────────────────────────────────────────────────────────────────

if (@(@([bool]$All, [bool]$Since, [bool]$Paths) | Where-Object { $_ }).Count -gt 1) { throw '-All, -Since and -Paths don''t combine.' }

$head = (Invoke-Git rev-parse HEAD).Trim()
$removed = @()

if ($Paths) {
    $upload = @($Paths | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().Replace('\', '/') -replace '^\./', '' -replace "^$siteRel/", '' } | Where-Object { $_ })
    $tracked = @{}; foreach ($f in Get-TrackedSiteFiles) { $tracked[$f] = $true }
    foreach ($rel in $upload) { if (-not $tracked[$rel]) { throw "site/$rel is not a deployable file git tracks." } }
    $range = 'named files'
} elseif ($All) {
    $upload = Get-TrackedSiteFiles
    $range = "all of site/ at $($head.Substring(0, 7))"
} else {
    $base = if ($Since) { $Since } elseif (Test-Path $markFile) { (Get-Content $markFile -Raw).Trim() } else { $null }
    if (-not $base) { throw 'No record of which commit is live. The first time, run -All (or -Check, then -MarkDeployed if the server already matches).' }
    $base = (Invoke-Git rev-parse --verify "$base^{commit}").Trim()
    if ($base -eq $head) { Write-Host "Nothing to deploy: $($head.Substring(0, 7)) is already live." -ForegroundColor Green; exit 0 }
    # --no-renames: a rename is an upload of the new path and a deletion of the old, and only the first half happens here.
    $diff = @((Invoke-Git diff --name-status --no-renames -z $base HEAD -- $siteRel) -split "`0" | Where-Object { $_ })
    $upload = @()
    for ($i = 0; $i -lt $diff.Count; $i += 2) {
        $rel = Get-SitePath $diff[$i + 1]
        if (-not $rel -or -not (Test-Deployable $rel)) { continue }
        if ($diff[$i] -eq 'D') { $removed += $rel } else { $upload += $rel }
    }
    $range = "$($base.Substring(0, 7))..$($head.Substring(0, 7))"
}

# What goes up must be what is committed.
if ($upload) {
    $dirty = @(Invoke-Git status --porcelain -- @($upload | ForEach-Object { "$siteRel/$_" }))
    if ($dirty) {
        Write-Host ''; $dirty | ForEach-Object { Write-Host "    $_" }
        throw 'These files have uncommitted changes. Commit or stash them first, so the deploy is exactly a commit.'
    }
}

Step "Deploy $range to $($script:site)"
if (-not $upload) { Write-Host '  Nothing deployable changed in site/.' }
foreach ($rel in $upload) { Write-Host "  upload           $rel" }
if ($DryRun) { Write-Host ''; Write-Host 'Dry run. Nothing was uploaded.' -ForegroundColor Green; exit 0 }

if ($upload -and -not $Yes) {
    $answer = Read-Host "`nUpload $($upload.Count) file(s)? [y/N]"
    if ($answer -notmatch '^(y|yes)$') { Write-Host 'Stopped. Nothing was uploaded.'; exit 1 }
}

if ($upload) {
    Step 'Uploading'
    $n = 0
    foreach ($rel in $upload) { $n++; Write-Host ("  [{0}/{1}] {2}" -f $n, $upload.Count, $rel); Send-DavFile $rel }

    # Read every file back: a 201 says the server accepted a request, not that it holds this file.
    Step 'Verifying'
    $bad = @()
    foreach ($rel in $upload) {
        $localSize = (Get-Item -LiteralPath (Get-EffectiveFile $rel)).Length
        $liveSize = Get-RemoteSize $rel
        if ($liveSize -ne $localSize) { $bad += $rel; Write-Host ("  MISMATCH         {0}   local {1}, live {2}" -f $rel, $localSize, ($liveSize ?? 'missing')) -ForegroundColor Red }
    }
    Write-Retries
    if ($bad) { throw "$($bad.Count) file(s) did not verify. The recorded live commit was not moved; run the deploy again." }
    Write-Host "  all $($upload.Count) verified" -ForegroundColor Green
    Clear-CloudflareCache $upload
}

if (-not $Paths) {
    New-Item -ItemType Directory -Force $stateDir | Out-Null
    Set-Content $markFile $head -Encoding ascii
    Write-Host ''; Write-Host "Live is now $($head.Substring(0, 7))." -ForegroundColor Green
} else {
    Write-Host ''; Write-Host 'Done. The recorded live commit was not moved (-Paths).' -ForegroundColor Green
}

if ($removed) {
    Write-Host ''
    Write-Host 'Deleted from site/, still on the server. This script never deletes; remove them by hand if they should go:' -ForegroundColor Yellow
    foreach ($rel in $removed) { Write-Host "    $rel" }
}

if ($upload) { Test-LiveSite }
