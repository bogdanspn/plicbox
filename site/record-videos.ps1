# Records the website's videos from the app's demo data and encodes them into media/.
# Mail (hero), calendar, contacts, tasks and settings, in light and dark. Takes about four minutes; keep the app window on screen
# while it films itself (other windows in front of it are fine). Needs ffmpeg: winget install Gyan.FFmpeg.
# Run it in the afternoon or evening: the demo mail is dated hours before "now", so in the early morning most of it falls
# under Yesterday and the inbox's Today group is nearly empty.

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) "plicbox-site-videos"
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) {
    $ffmpeg = (Get-ChildItem -Recurse -Filter ffmpeg.exe "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}
if (-not $ffmpeg) { throw "ffmpeg not found. Install it with: winget install Gyan.FFmpeg" }
$env:PLICBOX_FFMPEG = $ffmpeg

# A separate build, so a running Plicbox (or one open in the IDE) doesn't lock anything.
dotnet build "$repo\src\Plicbox.App" -nologo -v q "-p:OutDir=$work/bin/" "-p:IntermediateOutputPath=$work/obj/"
if ($LASTEXITCODE) { throw "Build failed" }

foreach ($theme in "light", "dark") {
    $out = "$work\raw\$theme"
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
    $arguments = @("--demo", "--record-site", $out) + $(if ($theme -eq "dark") { "--dark" } else { @() })
    Write-Host "Recording $theme..."
    $app = Start-Process "$work\bin\Plicbox.App.exe" -ArgumentList $arguments -PassThru
    if (-not $app.WaitForExit(300000)) { $app.Kill(); throw "Recording $theme took too long" }

    foreach ($module in "mail", "calendar", "contacts", "tasks", "settings") {
        $raw = "$out\$module.mp4"
        $name = if ($module -eq "mail") { "hero" } else { "video-$module" }
        $d = (& $ffmpeg -hide_banner -i $raw 2>&1 | Select-String "Duration: (\d+):(\d+):([\d.]+)").Matches[0].Groups
        $seconds = [double]$d[1].Value * 3600 + [double]$d[2].Value * 60 + [double]$d[3].Value
        # 1920 px wide; the last 0.7 s fades into the first frame, so the loop restarts seamlessly.
        $offset = [math]::Round($seconds - 0.7, 2).ToString([Globalization.CultureInfo]::InvariantCulture)
        $loop = "[0:v]scale=1920:-2:flags=lanczos,format=yuv420p,split[a][b];[b]trim=end_frame=1,loop=loop=30:size=1,setpts=N/30/TB[first];" +
                "[a][first]xfade=transition=fade:duration=0.7:offset=$offset,format=yuv420p[v]"
        & $ffmpeg -hide_banner -loglevel error -y -i $raw -filter_complex $loop -map "[v]" -c:v libx264 -preset slow -crf 25 -profile:v high -movflags +faststart -an "$PSScriptRoot\media\$name-$theme.mp4"
        # The first frame, shown before the video plays (and instead of it, for reduced motion).
        & $ffmpeg -hide_banner -loglevel error -y -i $raw -vf "select=eq(n\,0),scale=1920:-2:flags=lanczos" -frames:v 1 -q:v 3 "$PSScriptRoot\media\$name-$theme.jpg"
        Write-Host "  $name-$theme ($([math]::Round($seconds, 1)) s)"
    }
}
