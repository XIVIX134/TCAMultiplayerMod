[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Message,

    [string[]]$Closes = @(),

    [switch]$StagedOnly,

    [switch]$RunTests,

    [switch]$NoPush,

    [switch]$Draft,

    [switch]$Prerelease,

    [switch]$AllowNonMain
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "ReleaseCommon.ps1")

function Test-LastCommand {
    param([string]$Description)
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-IssueReference {
    param([string]$Issue)

    $trimmed = $Issue.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        return $null
    }

    if ($trimmed.StartsWith("#")) {
        return $trimmed
    }

    return "#$trimmed"
}

$root = Get-TcampRepositoryRoot
Push-Location $root
try {
    $versionInfo = ConvertTo-TcampReleaseVersion -Version $Version
    $tag = $versionInfo.Tag
    $packageVersion = $versionInfo.PackageVersion
    $assemblyVersion = $versionInfo.AssemblyVersion

    $branch = (& git branch --show-current).Trim()
    Test-LastCommand "Reading current branch"
    if (!$AllowNonMain -and $branch -ne "main") {
        throw "Release-Version.ps1 must run on main. Current branch is '$branch'. Pass -AllowNonMain to override."
    }

    & git rev-parse -q --verify "refs/tags/$tag" *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Local tag $tag already exists."
    }

    $remoteTag = (& git ls-remote --tags origin "refs/tags/$tag")
    Test-LastCommand "Checking remote tag $tag"
    if ($remoteTag) {
        throw "Remote tag $tag already exists on origin."
    }

    if (!$NoPush) {
        Invoke-TcampCheckedCommand -FilePath "gh" -Arguments @("auth", "status")
    }

    $projectPath = Resolve-TcampRepositoryPath -Path "src\TCAMP.csproj" -Root $root
    $pluginMetadataPath = Resolve-TcampRepositoryPath -Path "src\Core\PluginMetadata.cs" -Root $root
    $readmePath = Resolve-TcampRepositoryPath -Path "README.md" -Root $root
    $changelogPath = Resolve-TcampRepositoryPath -Path "CHANGELOG.md" -Root $root

    if (!(Test-Path -LiteralPath $changelogPath)) {
        throw "CHANGELOG.md not found. Add release notes under an '## Unreleased' section before releasing."
    }

    $changelogText = [System.IO.File]::ReadAllText($changelogPath)
    $releaseNotes = Get-TcampChangelogSection -Content $changelogText -Heading "Unreleased"
    if (!(Test-TcampMeaningfulChangelogBody -Body $releaseNotes)) {
        throw "CHANGELOG.md needs real notes under '## Unreleased' before releasing $tag."
    }

    # Strip the "Add new changes here" placeholder so it never lands in the
    # release notes or the generated changelog section.
    $releaseNotes = (($releaseNotes -split "\r?\n") |
        Where-Object { $_ -notmatch '^\s*-\s*Add new changes here before running' }) -join "`r`n"
    $releaseNotes = $releaseNotes.Trim()

    $releaseDate = Get-Date -Format "yyyy-MM-dd"
    $escapedTag = [regex]::Escape($tag)
    if ($changelogText -match "(?m)^## $escapedTag(\s|-|$)") {
        throw "CHANGELOG.md already has a section for $tag."
    }

    $projectText = [System.IO.File]::ReadAllText($projectPath)
    $projectText = $projectText -replace '<Version>[^<]+</Version>', "<Version>$packageVersion</Version>"
    $projectText = $projectText -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$assemblyVersion</AssemblyVersion>"
    $projectText = $projectText -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$assemblyVersion</FileVersion>"
    $projectText = $projectText -replace '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$packageVersion</InformationalVersion>"
    Write-TcampUtf8NoBom -Path $projectPath -Content $projectText

    $pluginMetadataText = [System.IO.File]::ReadAllText($pluginMetadataPath)
    $pluginMetadataText = $pluginMetadataText -replace 'public const string Version = "[^"]+";', "public const string Version = `"$packageVersion`";"
    Write-TcampUtf8NoBom -Path $pluginMetadataPath -Content $pluginMetadataText

    $readmeText = [System.IO.File]::ReadAllText($readmePath)
    $readmeText = [regex]::Replace(
        $readmeText,
        '(current public source release is `)v[^`]+(`)',
        { param($m) $m.Groups[1].Value + $tag + $m.Groups[2].Value })
    $readmeText = [regex]::Replace(
        $readmeText,
        '(first public source release is `)v[^`]+(`)',
        { param($m) $m.Groups[1].Value + $tag + $m.Groups[2].Value })
    Write-TcampUtf8NoBom -Path $readmePath -Content $readmeText

    $newReleaseSection = "## $tag - $releaseDate`r`n`r`n$releaseNotes`r`n`r`n"
    $changelogText = [regex]::Replace(
        $changelogText,
        "(?ms)^## Unreleased\s*\r?\n(?<body>.*?)(?=^## |\z)",
        "## Unreleased`r`n`r`n- Add new changes here before running ``.\scripts\Release-Version.ps1``.`r`n`r`n$newReleaseSection",
        1)
    Write-TcampUtf8NoBom -Path $changelogPath -Content $changelogText

    Invoke-TcampCheckedCommand -FilePath "git" -Arguments @("add", "CHANGELOG.md", "README.md", "src/TCAMP.csproj", "src/Core/PluginMetadata.cs")
    if (!$StagedOnly) {
        Invoke-TcampCheckedCommand -FilePath "git" -Arguments @("add", "-A")
    }

    & git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
        throw "No staged changes to commit for $tag."
    }
    elseif ($LASTEXITCODE -ne 1) {
        throw "Checking staged changes failed with exit code $LASTEXITCODE."
    }

    Invoke-TcampCheckedCommand -FilePath (Join-Path $PSScriptRoot "AssertPublishable.ps1")

    $packageMetadataPath = Join-Path ([System.IO.Path]::GetTempPath()) "tcamp-release-$([System.Guid]::NewGuid()).json"
    & (Join-Path $PSScriptRoot "PackageRelease.ps1") -Version $tag -MetadataPath $packageMetadataPath
    if (!(Test-Path -LiteralPath $packageMetadataPath)) {
        throw "Release package metadata was not written by PackageRelease.ps1."
    }
    $package = Get-Content -LiteralPath $packageMetadataPath -Raw | ConvertFrom-Json
    Remove-Item -LiteralPath $packageMetadataPath -Force -ErrorAction SilentlyContinue

    if ($RunTests) {
        Invoke-TcampCheckedCommand -FilePath "dotnet" -Arguments @("test", ".\TCAMP.sln", "-c", "Release")
    }

    if ([string]::IsNullOrWhiteSpace($Message)) {
        $Message = "Release $tag"
    }

    $commitArgs = @("commit", "-m", $Message)
    foreach ($issue in $Closes) {
        $issueRef = Get-IssueReference -Issue $issue
        if ($issueRef) {
            $commitArgs += @("-m", "Fixes $issueRef")
        }
    }
    Invoke-TcampCheckedCommand -FilePath "git" -Arguments $commitArgs

    Invoke-TcampCheckedCommand -FilePath "git" -Arguments @("tag", "-a", $tag, "-m", $tag)

    if ($NoPush) {
        Write-Host "Created local commit, tag, and package for $tag. -NoPush was set, so nothing was pushed or uploaded."
        return
    }

    Invoke-TcampCheckedCommand -FilePath "git" -Arguments @("push", "origin", $branch)
    Invoke-TcampCheckedCommand -FilePath "git" -Arguments @("push", "origin", $tag)

    & gh release view $tag *> $null
    $releaseExists = $LASTEXITCODE -eq 0

    if ($releaseExists) {
        Invoke-TcampCheckedCommand -FilePath "gh" -Arguments @(
            "release", "upload", $tag, $package.ZipPath, $package.DllSha256Path, "--clobber")
        Invoke-TcampCheckedCommand -FilePath "gh" -Arguments @(
            "release", "edit", $tag, "--notes", $releaseNotes)
    }
    else {
        $releaseArgs = @(
            "release", "create", $tag,
            $package.ZipPath,
            $package.DllSha256Path,
            "--title", $tag,
            "--notes", $releaseNotes,
            "--verify-tag")

        if ($Draft) { $releaseArgs += "--draft" }
        if ($Prerelease) { $releaseArgs += "--prerelease" }

        Invoke-TcampCheckedCommand -FilePath "gh" -Arguments $releaseArgs
    }

    gh release view $tag --json tagName,url,assets
}
finally {
    Pop-Location
}
