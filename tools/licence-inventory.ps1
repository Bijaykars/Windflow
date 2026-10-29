<#
.SYNOPSIS
    Automated licence inventory of all shipped dependencies (PLAN.md Phase 4).

.DESCRIPTION
    For each shipped project (App, Core, Viz, Report, Revit):
      - runs `dotnet list <proj> package --include-transitive --format json` (Release)
      - collects every package id + resolved version
      - reads each package's licence from the local NuGet cache nuspec
      - classifies against an allow-list / deny-list
      - prints a table (package, version, licence, status, which project(s) pull it in)
      - exits 1 if any package is denied or unknown
      - checks that every shipped (non-Microsoft-runtime) package id appears in
        THIRD-PARTY-NOTICES.txt, listing any that are missing

    Does not touch src/. Does not run the Sandbox.

.EXAMPLE
    pwsh -File tools/licence-inventory.ps1
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$NuGetCache = (Join-Path $env:USERPROFILE ".nuget\packages")
)

$ErrorActionPreference = 'Stop'

$ShippedProjects = @(
    'src/WindFlow.App/WindFlow.App.csproj',
    'src/WindFlow.Core/WindFlow.Core.csproj',
    'src/WindFlow.Viz/WindFlow.Viz.csproj',
    'src/WindFlow.Report/WindFlow.Report.csproj',
    'src/WindFlow.Revit/WindFlow.Revit.csproj'
)

$AllowList = @(
    'MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', 'Zlib',
    'NCSA', '0BSD', 'Unlicense'
)

# Substring patterns checked against the raw licence text/expression to catch variant spellings.
$DenyPatterns = @(
    'GPL', 'LGPL', 'AGPL', 'MPL', 'CDDL', 'EPL', 'SSPL', 'BUSL', 'non-commercial', 'noncommercial'
)

function Get-AllowedMatch([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    foreach ($allowed in $AllowList) {
        # NCSA / University of Illinois licence appears under several names in nuspecs.
        if ($allowed -eq 'NCSA') {
            if ($text -match '\bNCSA\b' -or $text -match 'University of Illinois') { return 'NCSA/University of Illinois' }
            continue
        }
        # Word-boundary match: a naked substring match on "MIT" also fires inside "LIMITED"
        # (routine disclaimer boilerplate in BSD/NCSA licence bodies).
        if ($text -match "\b$([regex]::Escape($allowed))\b") { return $allowed }
    }
    return $null
}

function Get-LicenceFileHeuristic([string]$text) {
    # Full licence file bodies rarely contain the SPDX id itself, so match on
    # characteristic phrasing instead.
    if ($text -match 'University of Illinois' -and $text -match 'NCSA') { return 'NCSA/University of Illinois' }
    if ($text -match 'Redistribution and use in source and binary forms') {
        # The no-endorsement "Neither the name..." clause is what distinguishes 3-clause
        # from 2-clause BSD; wording wraps across lines in some vendored files, so match
        # loosely with a dotall regex rather than a single-line literal phrase.
        if ($text -match '(?s)Neither the name.*?(endorse|promote)') { return 'BSD-3-Clause' }
        return 'BSD-2-Clause'
    }
    if ($text -match 'Permission is hereby granted, free of charge') { return 'MIT' }
    if ($text -match 'Apache License' -and $text -match 'Version 2\.0') { return 'Apache-2.0' }
    if ($text -match 'GNU GENERAL PUBLIC LICENSE') { return $null } # handled by deny check upstream
    return $null
}

function Get-DenyMatch([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    foreach ($pattern in $DenyPatterns) {
        # Word-boundary match only: a naked substring match on e.g. "MPL" also fires inside
        # "EXEMPLARY" (BSD disclaimer boilerplate) and "GPL" inside other tokens.
        if ($text -match "\b$pattern\b") { return $pattern }
    }
    return $null
}

function Get-PackageLicence([string]$id, [string]$version) {
    $idLower = $id.ToLowerInvariant()
    $nuspecPath = Join-Path $NuGetCache "$idLower\$version\$idLower.nuspec"
    if (-not (Test-Path $nuspecPath)) {
        return [pscustomobject]@{ Licence = $null; Source = 'nuspec not found'; Raw = $null }
    }
    try {
        [xml]$xml = Get-Content -Raw -LiteralPath $nuspecPath
    } catch {
        return [pscustomobject]@{ Licence = $null; Source = 'nuspec parse error'; Raw = $null }
    }
    $meta = $xml.package.metadata
    if ($null -eq $meta) {
        return [pscustomobject]@{ Licence = $null; Source = 'no metadata'; Raw = $null }
    }

    if ($meta.license -and $meta.license.type -eq 'expression' -and $meta.license.'#text') {
        return [pscustomobject]@{ Licence = $meta.license.'#text'; Source = 'license expression'; Raw = $meta.license.'#text' }
    }
    if ($meta.license -and $meta.license.type -eq 'file') {
        # File-based licence: read the bundled licence file itself (same package folder,
        # already in the local NuGet cache) and pattern-match its text.
        $licenceFileName = $meta.license.'#text'
        $licenceFilePath = Join-Path (Split-Path $nuspecPath -Parent) $licenceFileName
        if ($licenceFileName -and (Test-Path $licenceFilePath)) {
            $fileText = Get-Content -Raw -LiteralPath $licenceFilePath
            $denyHit = Get-DenyMatch $fileText
            if ($denyHit) {
                return [pscustomobject]@{ Licence = $denyHit; Source = "license file ($licenceFileName)"; Raw = $fileText }
            }
            $allowHit = Get-AllowedMatch $fileText
            if (-not $allowHit) { $allowHit = Get-LicenceFileHeuristic $fileText }
            if ($allowHit) {
                return [pscustomobject]@{ Licence = $allowHit; Source = "license file ($licenceFileName)"; Raw = $fileText }
            }
        }
        return [pscustomobject]@{ Licence = $null; Source = "license file ($licenceFileName, unclassified text)"; Raw = $null }
    }
    if ($meta.licenseUrl) {
        return [pscustomobject]@{ Licence = $meta.licenseUrl; Source = 'licenseUrl'; Raw = $meta.licenseUrl }
    }
    return [pscustomobject]@{ Licence = $null; Source = 'no licence field'; Raw = $null }
}

function Test-IsMicrosoftRuntime([string]$id) {
    return ($id -like 'Microsoft.*' -or $id -like 'System.*')
}

Push-Location $RepoRoot
try {
    $packageProjects = @{}   # id -> hashset of projects
    $packageVersions = @{}   # id -> version (last one wins; NuGet resolves to a single version per solution normally)

    foreach ($relProj in $ShippedProjects) {
        $projPath = Join-Path $RepoRoot $relProj
        if (-not (Test-Path $projPath)) {
            Write-Warning "Project not found, skipping: $relProj"
            continue
        }
        Write-Host "Restoring/listing packages for $relProj ..." -ForegroundColor DarkGray
        # `dotnet list package` needs an up-to-date project.assets.json; restore first so the
        # command works even if this is the first run against a project.
        dotnet restore $projPath -v q | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Error "dotnet restore failed for $relProj"
            Pop-Location
            exit 1
        }
        # `dotnet list package` has no --configuration switch; it reports the resolved
        # package graph from the last restore/build, independent of build configuration.
        $json = dotnet list $projPath package --include-transitive --format json 2>&1
        if ($LASTEXITCODE -ne 0) {
            $jsonAsText = ($json | Out-String)
            if ($jsonAsText -match 'Unable to read a package reference') {
                # Stale obj/project.assets.json (build-artifact only, not a src/ source edit):
                # delete and force a clean restore once, then retry.
                Write-Warning "Stale project.assets.json for $relProj, forcing a clean restore..."
                $objDir = Join-Path (Split-Path $projPath -Parent) 'obj'
                Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $objDir 'project.assets.json')
                Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $objDir 'project.nuget.cache')
                dotnet restore $projPath -v q --force | Out-Null
                $json = dotnet list $projPath package --include-transitive --format json 2>&1
            }
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Error "dotnet list failed for $relProj`:`n$json"
            Pop-Location
            exit 1
        }
        $jsonText = ($json | Out-String)
        $data = $jsonText | ConvertFrom-Json

        foreach ($frameworkResult in $data.projects) {
            foreach ($fw in $frameworkResult.frameworks) {
                $buckets = @()
                if ($fw.topLevelPackages) { $buckets += $fw.topLevelPackages }
                if ($fw.transitivePackages) { $buckets += $fw.transitivePackages }
                foreach ($pkg in $buckets) {
                    $id = $pkg.id
                    $ver = $pkg.resolvedVersion
                    if (-not $id -or -not $ver) { continue }
                    if (-not $packageProjects.ContainsKey($id)) { $packageProjects[$id] = New-Object System.Collections.Generic.HashSet[string] }
                    [void]$packageProjects[$id].Add((Split-Path $relProj -Leaf))
                    $packageVersions[$id] = $ver
                }
            }
        }
    }

    $rows = @()
    foreach ($id in ($packageVersions.Keys | Sort-Object)) {
        $version = $packageVersions[$id]
        $lic = Get-PackageLicence -id $id -version $version
        $isRuntime = Test-IsMicrosoftRuntime $id

        $status = 'UNKNOWN'
        $licenceLabel = $lic.Licence
        if ($lic.Licence) {
            $denyHit = Get-DenyMatch $lic.Licence
            $allowHit = Get-AllowedMatch $lic.Licence
            if ($denyHit) {
                $status = 'DENY'
            } elseif ($allowHit) {
                $status = 'ALLOW'
                $licenceLabel = $allowHit
            } else {
                $status = 'UNKNOWN'
            }
        } else {
            $licenceLabel = "($($lic.Source))"
            $status = 'UNKNOWN'
        }

        # Microsoft runtime/framework packages are allowed without further checks even if the
        # nuspec licence field could not be resolved (they are MIT under the .NET Foundation).
        if ($isRuntime -and $status -eq 'UNKNOWN') {
            $status = 'ALLOW'
            if (-not $lic.Licence) { $licenceLabel = 'MIT (assumed, .NET runtime/framework)' }
        }

        $rows += [pscustomobject]@{
            Package  = $id
            Version  = $version
            Licence  = $licenceLabel
            Status   = $status
            Runtime  = $isRuntime
            Projects = ($packageProjects[$id] | Sort-Object) -join ', '
        }
    }

    $rows | Format-Table -AutoSize -Property Package, Version, Licence, Status, Projects | Out-String -Width 300 | Write-Host

    $denyRows = $rows | Where-Object { $_.Status -eq 'DENY' }
    $unknownRows = $rows | Where-Object { $_.Status -eq 'UNKNOWN' }

    if ($denyRows) {
        Write-Host ""
        Write-Host "DENIED licences:" -ForegroundColor Red
        $denyRows | ForEach-Object { Write-Host "  $($_.Package) $($_.Version) -> $($_.Licence)" -ForegroundColor Red }
    }
    if ($unknownRows) {
        Write-Host ""
        Write-Host "UNKNOWN / unresolved licences:" -ForegroundColor Yellow
        $unknownRows | ForEach-Object { Write-Host "  $($_.Package) $($_.Version) -> $($_.Licence)" -ForegroundColor Yellow }
    }

    # THIRD-PARTY-NOTICES.txt coverage check for shipped, non-runtime packages.
    $noticesPath = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.txt'
    $noticesText = if (Test-Path $noticesPath) { Get-Content -Raw -LiteralPath $noticesPath } else { '' }

    $shippedNonRuntime = $rows | Where-Object { -not $_.Runtime }
    $missingFromNotices = @()
    foreach ($row in $shippedNonRuntime) {
        if ($noticesText -notmatch [regex]::Escape($row.Package)) {
            $missingFromNotices += $row.Package
        }
    }

    Write-Host ""
    if ($missingFromNotices.Count -gt 0) {
        Write-Host "Missing from THIRD-PARTY-NOTICES.txt:" -ForegroundColor Yellow
        $missingFromNotices | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    } else {
        Write-Host "All shipped non-Microsoft-runtime packages are covered in THIRD-PARTY-NOTICES.txt." -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "Total packages: $($rows.Count)  Allow: $(($rows | Where-Object Status -eq 'ALLOW').Count)  Deny: $($denyRows.Count)  Unknown: $($unknownRows.Count)"

    if ($denyRows.Count -gt 0 -or $unknownRows.Count -gt 0) {
        exit 1
    }
    exit 0
}
finally {
    Pop-Location
}
