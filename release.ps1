<#
.SYNOPSIS
    Builds a Plicbox release and publishes it to the public releases repo: Setup.exe for new installs, and the
    update that installed copies download on their own.
.EXAMPLE
    .\release.ps1                          # the next patch version (0.1.3 -> 0.1.4)
    .\release.ps1 -Version 0.2.0           # a version you choose
    .\release.ps1 -Notes "Romanian, sync fixes"
    .\release.ps1 -NoUpload                # build and pack only (artifacts\releases), to try Setup.exe first
#>
param(
    [string]$Version,
    [string]$Notes,
    [switch]$NoUpload
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$releasesRepo = 'bogdanspn/plicbox'
$repoUrl = "https://github.com/$releasesRepo"
$project = 'src\Plicbox.App\Plicbox.App.csproj'
# Also the install folder (%LocalAppData%\Plicbox.App). Never "Plicbox": that's where the mail is kept, and
# uninstalling removes the install folder.
$packId = 'Plicbox.App'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$releasesDir = Join-Path $artifacts 'releases'

function Step($text) { Write-Host $text -ForegroundColor Cyan }
function Fail($text) { Write-Host $text -ForegroundColor Red; exit 1 }

# ─── Tools: the packer must match the Velopack library the app is built with ───
$csproj = Get-Content $project -Raw
$velopack = [regex]::Match($csproj, 'Include="Velopack" Version="([^"]+)"').Groups[1].Value
if (-not $velopack) { Fail "Velopack isn't referenced in $project." }
$vpk = (dotnet tool list -g | Select-String '^vpk\s+(\S+)').Matches.Groups[1].Value
if ($vpk -ne $velopack) {
    Step "Installing the Velopack packer $velopack..."
    if ($vpk) { dotnet tool update -g vpk --version $velopack | Out-Null } else { dotnet tool install -g vpk --version $velopack | Out-Null }
}
if (-not $NoUpload) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail 'GitHub CLI (gh) is needed to upload. Install it, or use -NoUpload.' }
    $token = (gh auth token).Trim()
    if (-not $token) { Fail 'Not signed in to GitHub: run "gh auth login" first.' }
}

# ─── Version: given, or one past the latest published release (the project's own for the very first) ───
if (-not $Version) {
    $latest = gh release list --repo $releasesRepo --limit 1 --json tagName --jq '.[0].tagName' 2>$null
    if ($latest) {
        $v = [version]($latest.Trim().TrimStart('v'))
        $Version = "$($v.Major).$($v.Minor).$($v.Build + 1)"
    }
    else {
        $Version = [regex]::Match($csproj, '<Version>([^<]+)</Version>').Groups[1].Value
    }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "Version '$Version' should look like 1.2.3." }

if (git status --porcelain) {
    Write-Host 'Note: there are uncommitted changes; they go into this release.' -ForegroundColor Yellow
}
Write-Host "Releasing Plicbox $Version" -ForegroundColor Green

# The project carries the version, so the app (Settings, logs) and the next release know it.
$csproj = [regex]::Replace($csproj, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText((Resolve-Path $project), $csproj)

# ─── Third-party notices: every shipped package's licence, as they ask (follows dependency changes) ───
& (Join-Path $PSScriptRoot 'licenses\update-notices.ps1')

# ─── Build: self-contained (no .NET install needed), compiled ahead for a quicker start ───
Step 'Building...'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o $publishDir -nologo -v q
if ($LASTEXITCODE -ne 0) { Fail 'Build failed (see the errors above).' }

# ─── Pack: the installer, the full package and a delta against the previous release ───
# Starts empty: the releases repo is the record of what's out, and leftovers from a -NoUpload trial would clash.
if (Test-Path $releasesDir) { Remove-Item $releasesDir -Recurse -Force }
New-Item -ItemType Directory -Force $releasesDir | Out-Null
if (-not $NoUpload) {
    Step 'Fetching the previous release (for a small update)...'
    # GitHub sometimes stops sending part-way (once at 0 bytes for ten minutes): a download that hasn't grown for a
    # minute and a half is stopped and started again, three times at most. Slow is fine, as long as it moves.
    $fetched = $false
    for ($try = 1; $try -le 3 -and -not $fetched; $try++) {
        Get-ChildItem $releasesDir -Filter *.incomplete -ErrorAction SilentlyContinue | Remove-Item -Force
        $vpkDownload = Start-Process vpk -ArgumentList @('download', 'github', '--repoUrl', $repoUrl, '--token', $token, '-o', $releasesDir) -NoNewWindow -PassThru
        $null = $vpkDownload.Handle # keeps the exit code readable afterwards
        $size = -1; $still = 0
        while (-not $vpkDownload.WaitForExit(5000)) {
            $now = (Get-ChildItem $releasesDir -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
            if ($now -gt $size) { $size = $now; $still = 0 } else { $still += 5 }
            if ($still -ge 90) {
                Write-Host "  The download stopped moving; starting it again ($try of 3)..." -ForegroundColor Yellow
                $vpkDownload.Kill()
                $vpkDownload.WaitForExit()
                break
            }
        }
        if ($still -lt 90) {
            # It ended on its own: done, or there's no previous release (the very first one).
            if ($vpkDownload.ExitCode -eq 0) { $fetched = $true } else { break }
        }
    }
    if (-not $fetched) {
        Get-ChildItem $releasesDir -Filter *.incomplete -ErrorAction SilentlyContinue | Remove-Item -Force
        Write-Host 'No previous release to compare with; this one is a full download only.' -ForegroundColor Yellow
    }
}

Step 'Packing...'
$packArgs = @('pack', '-u', $packId, '-v', $Version, '-p', $publishDir, '-e', 'Plicbox.App.exe', '-o', $releasesDir,
    '--packTitle', 'Plicbox', '--packAuthors', 'Bogdan Rosu', '-i', 'src\Plicbox.App\Assets\plicbox\windows\plicbox.ico',
    '--shortcuts', 'Desktop,StartMenuRoot')
if ($Notes) {
    $notesFile = Join-Path $artifacts 'notes.md'
    Set-Content $notesFile $Notes -Encoding utf8
    $packArgs += @('--releaseNotes', $notesFile)
}
vpk @packArgs
if ($LASTEXITCODE -ne 0) { Fail 'Packing failed.' }

# ─── Size, start time and memory, next to the last release's (warnings only; recorded in perf\history.csv) ───
Step 'Measuring...'
try { & (Join-Path $PSScriptRoot 'perf\measure.ps1') -Version $Version -PublishDir $publishDir -ReleasesDir $releasesDir -Record:(-not $NoUpload) }
catch { Write-Host "  Measuring failed: $_" -ForegroundColor Yellow }

if ($NoUpload) {
    Write-Host "Packed (not uploaded): $releasesDir\$packId-win-Setup.exe" -ForegroundColor Green
    exit 0
}

# ─── Publish ───
Step 'Uploading...'
vpk upload github --repoUrl $repoUrl --token $token -o $releasesDir --publish --releaseName "Plicbox $Version" --tag "v$Version"
if ($LASTEXITCODE -ne 0) { Fail 'Upload failed.' }

# The website's What's new page and the version under its Download buttons.
Step 'Updating the website...'
& (Join-Path $PSScriptRoot 'site\update-versions.ps1')

Write-Host "Plicbox $Version is out: $repoUrl/releases/tag/v$Version" -ForegroundColor Green
Write-Host 'Installed copies pick it up within a few hours (Settings > Check for updates for now).' -ForegroundColor Green
Write-Host "Commit the version change in $project (and site\, perf\history.csv, src\Plicbox.App\THIRD-PARTY-NOTICES.txt) so the next release counts on from it, then publish the site: pwsh -File webdisk\deploy.ps1" -ForegroundColor Yellow
