<#
.SYNOPSIS
    Builds Plicbox and starts it.
.EXAMPLE
    .\run.ps1              # your real accounts
    .\run.ps1 -Demo        # fictional sample mail, throwaway database, no syncing
    .\run.ps1 -GreenMail   # against a local GreenMail test server (sandboxed data)
    .\run.ps1 -Test        # run the unit tests instead
    .\run.ps1 -Release     # optimized build
#>
param(
    [switch]$Demo,
    [switch]$GreenMail,
    [switch]$Test,
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Test) {
    dotnet test
    exit $LASTEXITCODE
}

$config = if ($Release) { 'Release' } else { 'Debug' }
$project = 'src\Plicbox.App\Plicbox.App.csproj'
# Where the build puts the app, asked from the project: a change of target framework can't leave this starting an old copy.
$exe = (dotnet msbuild $project -nologo -getProperty:TargetPath "-p:Configuration=$config").Trim() -replace '\.dll$', '.exe'
$bin = Join-Path $PSScriptRoot 'src\Plicbox.App\bin'

# A running copy locks its files, so the build would fail.
$running = Get-Process Plicbox.App -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($bin, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host "Plicbox is still running. Quit it first (tray icon > Exit), then run this again." -ForegroundColor Yellow
    exit 1
}

Write-Host "Building ($config)..." -ForegroundColor Cyan
dotnet build $project -c $config -nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed (see the errors above)." -ForegroundColor Red
    exit $LASTEXITCODE
}

$appArgs = @()
if ($Demo) { $appArgs += '--demo' }
if ($GreenMail) { $appArgs += '--dev-greenmail' }

if ($appArgs.Count -gt 0) { Start-Process $exe -ArgumentList $appArgs }
else { Start-Process $exe }
Write-Host "Plicbox started." -ForegroundColor Green
