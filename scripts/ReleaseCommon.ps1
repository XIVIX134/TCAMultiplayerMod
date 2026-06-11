Set-StrictMode -Version Latest

function Get-TcampRepositoryRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

function Resolve-TcampRepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$Root = (Get-TcampRepositoryRoot)
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $Root $Path))
}

function ConvertTo-TcampReleaseVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $raw = $Version.Trim()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw "Version cannot be empty."
    }

    $tagVersion = if ($raw.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $raw.Substring(1)
    }
    else {
        $raw
    }

    if ($tagVersion -notmatch '^\d+\.\d+(\.\d+)?(-[0-9A-Za-z.-]+)?$') {
        throw "Version '$Version' is invalid. Use values like v0.3, 0.3, or 0.3.1."
    }

    $numericVersion = $tagVersion
    $suffix = ""
    if ($tagVersion -match '^([^-]+)(-.+)?$') {
        $numericVersion = $Matches[1]
        if ($Matches.ContainsKey(2)) {
            $suffix = $Matches[2]
        }
    }

    $packageNumericVersion = $numericVersion
    if ($numericVersion -match '^\d+\.\d+$') {
        $packageNumericVersion = "$numericVersion.0"
    }

    $packageVersion = "$packageNumericVersion$suffix"
    $assemblyVersion = "$packageNumericVersion.0"

    return [pscustomobject]@{
        Tag = "v$tagVersion"
        TagVersion = $tagVersion
        PackageVersion = $packageVersion
        AssemblyVersion = $assemblyVersion
    }
}

function Write-TcampUtf8NoBom {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Invoke-TcampCheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$Arguments = @()
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}
