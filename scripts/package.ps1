[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$semanticVersionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
if ($Version -cnotmatch $semanticVersionPattern)
{
    throw "Version '$Version' is not a valid Semantic Version 2.0.0 value."
}

$scriptDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::Combine($scriptDirectory, '..'))
$repositoryRootPrefix = $repositoryRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

function Get-RepositoryPath
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    $candidate = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($repositoryRoot, $RelativePath))
    if (-not $candidate.StartsWith(
            $repositoryRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Path '$candidate' is outside repository root '$repositoryRoot'."
    }

    return $candidate
}

function Assert-NoReparsePoint
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (-not $Path.StartsWith(
            $repositoryRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Path '$Path' is outside repository root '$repositoryRoot'."
    }

    $relativePath = $Path.Substring($repositoryRootPrefix.Length)
    $currentPath = $repositoryRoot
    foreach ($segment in @($relativePath -split '[\\/]') | Where-Object { $_.Length -gt 0 })
    {
        $currentPath = [System.IO.Path]::Combine($currentPath, $segment)
        if (-not (Test-Path -LiteralPath $currentPath))
        {
            continue
        }

        $item = Get-Item -Force -LiteralPath $currentPath
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
        {
            throw "Refusing to use reparse point '$currentPath' as a package output path."
        }
    }
}

function Reset-RepositoryDirectory
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    Assert-NoReparsePoint -Path $Path
    if (Test-Path -LiteralPath $Path)
    {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    $null = New-Item -ItemType Directory -Path $Path
}

function Assert-NativeCommandSucceeded
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if ($LASTEXITCODE -ne 0)
    {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Assert-JsonFile
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "Expected JSON feed '$Path' was not generated."
    }

    $json = Get-Content -Raw -LiteralPath $Path
    if ([string]::IsNullOrWhiteSpace($json))
    {
        throw "JSON feed '$Path' is empty."
    }

    try
    {
        $null = $json | ConvertFrom-Json -ErrorAction Stop
    }
    catch
    {
        throw "JSON feed '$Path' is invalid: $($_.Exception.Message)"
    }
}

function Assert-ArchiveContents
{
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo] $Archive,

        [switch] $RequirePortableRuntime
    )

    $zip = [System.IO.Compression.ZipFile]::OpenRead($Archive.FullName)
    try
    {
        $entryNames = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\\', '/') })
        if ($entryNames | Where-Object {
                [System.IO.Path]::GetFileName($_) -in @(
                    'steamcmd.exe',
                    'settings.json',
                    'library.db')
            })
        {
            throw "Archive '$($Archive.FullName)' contains forbidden runtime data or SteamCMD."
        }

        if ($RequirePortableRuntime)
        {
            if ($entryNames -notcontains 'Stellaris Downloader V2.exe')
            {
                throw "Portable archive '$($Archive.FullName)' does not contain its root launcher."
            }

            if (-not ($entryNames | Where-Object {
                        [System.IO.Path]::GetFileName($_) -ieq 'StellarisDownloader.App.exe'
                    }))
            {
                throw "Portable archive '$($Archive.FullName)' does not contain the application executable."
            }

            if (-not ($entryNames | Where-Object {
                        [System.IO.Path]::GetFileName($_) -ieq 'coreclr.dll'
                    }))
            {
                throw "Portable archive '$($Archive.FullName)' does not contain the self-contained .NET runtime."
            }
        }
    }
    finally
    {
        $zip.Dispose()
    }
}

$solutionPath = Get-RepositoryPath -RelativePath 'StellarisDownloader.sln'
$appProjectPath = Get-RepositoryPath -RelativePath 'src\StellarisDownloader.App\StellarisDownloader.App.csproj'
$releaseNotesPath = Get-RepositoryPath -RelativePath 'RELEASE_NOTES.md'
$publishDirectory = Get-RepositoryPath -RelativePath 'artifacts\publish'
$releaseDirectory = Get-RepositoryPath -RelativePath 'artifacts\releases'

foreach ($requiredFile in @($solutionPath, $appProjectPath, $releaseNotesPath))
{
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf))
    {
        throw "Required repository file '$requiredFile' does not exist."
    }
}

Reset-RepositoryDirectory -Path $publishDirectory
Reset-RepositoryDirectory -Path $releaseDirectory

Push-Location $repositoryRoot
try
{
    $publishArguments = @(
        'publish',
        $appProjectPath,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-o', $publishDirectory,
        "-p:Version=$Version",
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false'
    )
    & dotnet @publishArguments
    Assert-NativeCommandSucceeded -Description 'dotnet publish'

    $applicationExecutable = Join-Path $publishDirectory 'StellarisDownloader.App.exe'
    $coreRuntime = Join-Path $publishDirectory 'coreclr.dll'
    $hostRuntime = Join-Path $publishDirectory 'hostfxr.dll'
    if (-not (Test-Path -LiteralPath $applicationExecutable -PathType Leaf))
    {
        throw "Published application executable '$applicationExecutable' is missing."
    }

    if (-not (Test-Path -LiteralPath $coreRuntime -PathType Leaf) -or
        -not (Test-Path -LiteralPath $hostRuntime -PathType Leaf))
    {
        throw 'Published output is not a complete self-contained .NET runtime.'
    }

    $publishedSteamCmd = Get-ChildItem -LiteralPath $publishDirectory -File -Recurse |
        Where-Object Name -IEQ 'steamcmd.exe'
    if ($publishedSteamCmd)
    {
        throw "Published output contains forbidden SteamCMD binary '$($publishedSteamCmd[0].FullName)'."
    }

    $vpkArguments = @(
        'vpk',
        '--version', '1.2.0',
        '--yes',
        'pack',
        '--packId', 'StellarisDownloaderV2',
        '--packVersion', $Version,
        '--packDir', $publishDirectory,
        '--mainExe', 'StellarisDownloader.App.exe',
        '--packTitle', 'Stellaris Downloader V2',
        '--runtime', 'win-x64',
        '--channel', 'win',
        '--outputDir', $releaseDirectory,
        '--releaseNotes', $releaseNotesPath,
        '--noInst', 'true'
    )
    & dnx @vpkArguments
    Assert-NativeCommandSucceeded -Description 'Velopack packaging'
}
finally
{
    Pop-Location
}

$portableArchives = @(Get-ChildItem -LiteralPath $releaseDirectory -File |
        Where-Object Name -Like '*-Portable.zip')
if ($portableArchives.Count -ne 1)
{
    throw "Expected exactly one Portable ZIP, but found $($portableArchives.Count)."
}

$fullPackages = @(Get-ChildItem -LiteralPath $releaseDirectory -File |
        Where-Object Name -Like '*-full.nupkg')
if ($fullPackages.Count -lt 1)
{
    throw 'Expected at least one Velopack full nupkg, but none was generated.'
}

$releaseFeedPath = Join-Path $releaseDirectory 'releases.win.json'
$assetsFeedPath = Join-Path $releaseDirectory 'assets.win.json'
Assert-JsonFile -Path $releaseFeedPath
Assert-JsonFile -Path $assetsFeedPath

$installerArtifacts = @(Get-ChildItem -LiteralPath $releaseDirectory -File -Recurse |
        Where-Object {
            $_.Name -match '(?i)(setup|installer)' -or
            $_.Extension -in @('.msi', '.msix', '.msixbundle')
        })
if ($installerArtifacts.Count -gt 0)
{
    throw "Installer output is forbidden, but '$($installerArtifacts[0].FullName)' was generated."
}

Assert-ArchiveContents -Archive $portableArchives[0] -RequirePortableRuntime
foreach ($package in $fullPackages)
{
    Assert-ArchiveContents -Archive $package
}

$deltaPackages = @(Get-ChildItem -LiteralPath $releaseDirectory -File |
        Where-Object Name -Like '*-delta.nupkg')
foreach ($package in $deltaPackages)
{
    Assert-ArchiveContents -Archive $package
}

Write-Host "Packaged StellarisDownloader V2 $Version successfully."
Write-Host "Portable archive: $($portableArchives[0].FullName)"
Write-Host "Full packages: $($fullPackages.Count); delta packages: $($deltaPackages.Count)"
Write-Host "Feed directory: $releaseDirectory"
