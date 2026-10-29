# Builds a Velopack installer for WindFlow into out/releases.
# Prerequisite (user-scoped, no admin): dotnet tool install -g vpk
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "src/WindFlow.App/WindFlow.App.csproj"
$publish = Join-Path $root "out/publish"
$releases = Join-Path $root "out/releases"

[xml]$csproj = Get-Content $proj
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { $version = "0.1.0" }
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

New-Item -ItemType Directory -Force -Path $releases | Out-Null
# ponytail: no update feed yet. When there is one, add  --channel win  and publish the generated
# RELEASES file to it, and give VelopackApp an UpdateManager pointed at the same URL (see Program.cs).
vpk pack --packId WindFlow --packVersion $version --packDir $publish --mainExe WindFlow.App.exe `
    --packTitle "WindFlow" --packAuthors "Bijay Karki" --icon (Join-Path $root "src/WindFlow.App/Assets/windflow.ico") --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Get-ChildItem $releases | Select-Object Name, Length | Format-Table
