# Builds a Velopack installer for WindFlow into out/releases.
# Prerequisite (user-scoped, no admin): dotnet tool install -g vpk --version 1.2.158 (match the Velopack package)
# -PackVersion overrides the csproj version for test packs (e.g. 0.2.1-test.1); the real release leaves it out.
param([string]$PackVersion)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "src/WindFlow.App/WindFlow.App.csproj"
$publish = Join-Path $root "out/publish"

[xml]$csproj = Get-Content $proj
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if ($PackVersion) { $version = $PackVersion }
if (-not $version) { $version = "0.1.0" }
# A fresh folder per version, so an older pack can never block or mix into this one.
$releases = Join-Path $root "out/releases/$version"
Write-Host "WindFlow $version"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# Self-contained so a clean machine needs no runtime; trimming stays OFF because Avalonia XAML and the
# Vortice COM wrappers both rely on reflection.
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
# S5-01: the MIT/BSD/NCSA licences require the notices to travel with the binaries.
if (-not (Test-Path (Join-Path $publish "THIRD-PARTY-NOTICES.txt"))) { throw "THIRD-PARTY-NOTICES.txt is missing from the publish output" }
if (-not (Test-Path (Join-Path $publish "LICENSE.txt"))) { throw "LICENSE.txt is missing from the publish output" }

if (Test-Path $releases) { Remove-Item $releases -Recurse -Force }
New-Item -ItemType Directory -Force -Path $releases | Out-Null
# Installed copies find updates through the GitHub release assets (UpdateCheck.cs, GithubSource, channel "win").
vpk pack --packId WindFlow --packVersion $version --channel win --packDir $publish --mainExe WindFlow.App.exe `
    --packTitle "WindFlow" --packAuthors "Bijay Karki" --icon (Join-Path $root "src/WindFlow.App/Assets/windflow.ico") --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host "`nAttach ALL of these to the GitHub release v$version (updates need the feed + nupkg, new users need Setup/Portable):"
Get-ChildItem $releases | Select-Object Name, Length | Format-Table
