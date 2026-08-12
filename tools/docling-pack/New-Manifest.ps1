[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $StagingRoot,
    [Parameter(Mandatory = $true)] [string] $PackVersion,
    [Parameter(Mandatory = $true)] [string] $PythonVersion,
    [Parameter(Mandatory = $true)] [string] $DoclingVersion,
    [Parameter(Mandatory = $true)] [string] $DoclingServeVersion,
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,
    [Parameter(Mandatory = $true)] [string] $EntryPoint,
    [Parameter(Mandatory = $true)] [string[]] $RequiredFiles
)

$resolvedStagingRoot = (Resolve-Path -LiteralPath $StagingRoot).Path
$allRelativeFiles = @($EntryPoint) + $RequiredFiles
foreach ($relativeFile in $allRelativeFiles) {
    if ([System.IO.Path]::IsPathRooted($relativeFile) -or $relativeFile -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Processing Pack paths must be relative and remain beneath the staging root: $relativeFile"
    }

    $candidate = Join-Path -Path $resolvedStagingRoot -ChildPath $relativeFile
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Required staged file does not exist: $relativeFile"
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    commandContractVersion = 1
    packVersion = $PackVersion
    pythonVersion = $PythonVersion
    doclingVersion = $DoclingVersion
    doclingServeVersion = $DoclingServeVersion
    runtimeIdentifier = $RuntimeIdentifier
    entryPoint = $EntryPoint.Replace('\\', '/')
    requiredFiles = @($RequiredFiles | ForEach-Object { $_.Replace('\\', '/') })
}

$manifestPath = Join-Path -Path $resolvedStagingRoot -ChildPath 'manifest.json'
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Output $manifestPath
