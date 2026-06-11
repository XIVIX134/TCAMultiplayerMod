[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Configuration = "Release",

    [string]$ProjectPath = "src\TCAMP.csproj",

    [string]$OutputRoot = "release",

    [string]$MetadataPath,

    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "ReleaseCommon.ps1")

$root = Get-TcampRepositoryRoot
$versionInfo = ConvertTo-TcampReleaseVersion -Version $Version
$projectPathFull = Resolve-TcampRepositoryPath -Path $ProjectPath -Root $root
$outputRootFull = Resolve-TcampRepositoryPath -Path $OutputRoot -Root $root

if (!(Test-Path -LiteralPath $projectPathFull)) {
    throw "Project file not found: $projectPathFull"
}

if (!$NoBuild) {
    Invoke-TcampCheckedCommand -FilePath "dotnet" -Arguments @("build", $projectPathFull, "-c", $Configuration)
}

[xml]$projectXml = Get-Content -LiteralPath $projectPathFull
$propertyGroup = $projectXml.Project.PropertyGroup | Where-Object { $_.TargetFramework } | Select-Object -First 1
$targetFramework = if ($propertyGroup -and $propertyGroup.TargetFramework) { $propertyGroup.TargetFramework } else { "net472" }
$assemblyName = if ($propertyGroup -and $propertyGroup.AssemblyName) { $propertyGroup.AssemblyName } else { "TCAMP" }

$projectDir = Split-Path -Parent $projectPathFull
$dllPath = Join-Path $projectDir "bin\$Configuration\$targetFramework\$assemblyName.dll"
if (!(Test-Path -LiteralPath $dllPath)) {
    throw "Built plugin DLL not found: $dllPath"
}

$packageName = "TCAMP-$($versionInfo.Tag)-plugin"
$packageDir = Join-Path $outputRootFull $packageName
$zipPath = Join-Path $outputRootFull "$packageName.zip"
$shaPath = "$zipPath.sha256"

Remove-Item -LiteralPath $packageDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $shaPath -Force -ErrorAction SilentlyContinue

$pluginsDir = Join-Path $packageDir "BepInEx\plugins"
New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null
Copy-Item -LiteralPath $dllPath -Destination (Join-Path $pluginsDir "$assemblyName.dll") -Force

$readme = @"
TCAMP $($versionInfo.Tag)

Install:
1. Install BepInEx for Tiny Combat Arena if you have not already.
2. Copy the BepInEx folder from this zip into your Tiny Combat Arena game folder.
3. Launch the game.

This package contains only the TCAMP plugin DLL. It does not include Tiny Combat Arena, Unity, BepInEx, Harmony, or other third-party/runtime DLLs.
"@

Set-Content -LiteralPath (Join-Path $packageDir "README.txt") -Value $readme -Encoding ASCII

$items = Get-ChildItem -LiteralPath $packageDir
Compress-Archive -Path $items.FullName -DestinationPath $zipPath -Force

$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
$hashLine = "$($hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $zipPath)"
Set-Content -LiteralPath $shaPath -Value $hashLine -Encoding ASCII

Write-Host "Created package: $zipPath"
Write-Host "Created checksum: $shaPath"
Write-Host "SHA256: $($hash.Hash)"

$metadata = [pscustomobject]@{
    Tag = $versionInfo.Tag
    PackageVersion = $versionInfo.PackageVersion
    AssemblyVersion = $versionInfo.AssemblyVersion
    DllPath = $dllPath
    ZipPath = $zipPath
    Sha256Path = $shaPath
    Sha256 = $hash.Hash
}

if (![string]::IsNullOrWhiteSpace($MetadataPath)) {
    $metadataPathFull = Resolve-TcampRepositoryPath -Path $MetadataPath -Root $root
    $metadataDir = Split-Path -Parent $metadataPathFull
    if (![string]::IsNullOrWhiteSpace($metadataDir)) {
        New-Item -ItemType Directory -Path $metadataDir -Force | Out-Null
    }
    $metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPathFull -Encoding ASCII
}

$metadata
