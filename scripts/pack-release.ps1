[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string] $UpdateOwner,
    [string] $UpdateRepository,
    [switch] $SkipTests,
    [switch] $NoArchive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'DivaModManager.sln'
$projectPath = Join-Path $repositoryRoot 'DivaModManager/DivaModManager.csproj'
$releaseRoot = Join-Path $repositoryRoot 'artifacts/release'
$publishRoot = Join-Path $repositoryRoot 'artifacts/publish'
$packageName = "DivaModManager-v$Version-win-x64"
$packageRoot = Join-Path $releaseRoot $packageName
$thirdPartyLibrary = Join-Path $repositoryRoot 'DivaModManager/ThirdParty/MikuMikuLibrary/MikuMikuLibrary.dll'
$expectedThirdPartyHash = '2EE3FC37A0024D3B8AE7D108A6AB0FD0E093C495D4090B14C834C87883DA4D58'
$releaseNotesPath = Join-Path $repositoryRoot "docs/RELEASE_NOTES_$Version.md"
$numericVersion = [regex]::Match($Version, '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)')
$assemblyVersion = '{0}.{1}.{2}.0' -f
    $numericVersion.Groups['major'].Value,
    $numericVersion.Groups['minor'].Value,
    $numericVersion.Groups['patch'].Value

if ([string]::IsNullOrWhiteSpace($UpdateOwner) -xor
    [string]::IsNullOrWhiteSpace($UpdateRepository)) {
    throw 'UpdateOwner and UpdateRepository must be supplied together.'
}

$thirdPartyHash = (Get-FileHash -LiteralPath $thirdPartyLibrary -Algorithm SHA256).Hash
if ($thirdPartyHash -ne $expectedThirdPartyHash) {
    throw "Unexpected MikuMikuLibrary.dll SHA-256: $thirdPartyHash"
}

$thirdPartyBytes = [System.IO.File]::ReadAllBytes($thirdPartyLibrary)
$thirdPartyText = [System.Text.Encoding]::ASCII.GetString($thirdPartyBytes)
if ($thirdPartyText -match '[A-Za-z]:\\Users\\[^\\]+\\') {
    throw 'MikuMikuLibrary.dll contains a local user profile path. Rebuild it with deterministic PathMap settings before release.'
}

if (-not (Test-Path -LiteralPath $releaseNotesPath -PathType Leaf)) {
    $releaseNotesPath = Join-Path $repositoryRoot 'CHANGELOG.md'
}

Push-Location $repositoryRoot
try {
    if (Test-Path -LiteralPath $releaseRoot) {
        Remove-Item -LiteralPath $releaseRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $publishRoot) {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

    dotnet restore $solutionPath `
        -p:NuGetAudit=true `
        -p:NuGetAuditMode=all `
        -p:WarningsAsErrors=NU1901%3BNU1902%3BNU1903%3BNU1904
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet restore or dependency audit failed.'
    }

    if (-not $SkipTests) {
        dotnet test $solutionPath -c Release --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw 'Release tests failed.'
        }
    }

    $publishArguments = @(
        'publish', $projectPath,
        '-c', 'Release',
        '--no-restore',
        '--self-contained', 'false',
        '-r', 'win10-x64',
        '-o', $publishRoot,
        "-p:Version=$Version",
        "-p:AssemblyVersion=$assemblyVersion",
        "-p:FileVersion=$assemblyVersion",
        '-p:PublishSingleFile=true',
        '-p:DebugType=portable',
        '-p:DebugSymbols=true',
        '-p:ContinuousIntegrationBuild=true',
        "-p:PathMap=$repositoryRoot=/_/DivaModManager"
    )
    if (-not [string]::IsNullOrWhiteSpace($UpdateOwner)) {
        $publishArguments += "-p:DmmUpdateOwner=$UpdateOwner"
        $publishArguments += "-p:DmmUpdateRepository=$UpdateRepository"
    }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }

    $requiredFiles = @(
        'DivaModManager.exe',
        'DivaModManager.pdb',
        'x64/7z.dll',
        'x86/7z.dll'
    )
    foreach ($relativePath in $requiredFiles) {
        $sourcePath = Join-Path $publishRoot $relativePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Required publish output is missing: $relativePath"
        }
    }

    Copy-Item -LiteralPath (Join-Path $publishRoot 'DivaModManager.exe') -Destination $packageRoot
    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'x64') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'x86') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishRoot 'x64/7z.dll') -Destination (Join-Path $packageRoot 'x64/7z.dll')
    Copy-Item -LiteralPath (Join-Path $publishRoot 'x86/7z.dll') -Destination (Join-Path $packageRoot 'x86/7z.dll')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $packageRoot
    Copy-Item -LiteralPath $releaseNotesPath -Destination (Join-Path $packageRoot 'RELEASE-NOTES.md')

    $mikuLicenseRoot = Join-Path $packageRoot 'licenses/MikuMikuLibrary'
    New-Item -ItemType Directory -Path $mikuLicenseRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'DivaModManager/ThirdParty/MikuMikuLibrary/LICENSE.md') -Destination $mikuLicenseRoot
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'DivaModManager/ThirdParty/MikuMikuLibrary/README.md') -Destination $mikuLicenseRoot

    $commit = (git rev-parse HEAD).Trim()
    $buildInfo = @(
        "Diva Mod Manager $Version",
        "Commit: $commit",
        'Target: net6.0-windows / win10-x64',
        'Deployment: framework-dependent single-file application',
        'Runtime: Microsoft .NET 6 Desktop Runtime (x64)',
        '',
        'Keep the x64 and x86 directories beside DivaModManager.exe.'
    )
    Set-Content -LiteralPath (Join-Path $packageRoot 'BUILD-INFO.txt') -Value $buildInfo -Encoding utf8NoBOM

    if ($NoArchive) {
        Write-Host "Package directory: $packageRoot"
        return
    }

    $runtimeArchive = Join-Path $releaseRoot "$packageName.zip"
    $symbolsArchive = Join-Path $releaseRoot "DivaModManager-v$Version-symbols.zip"
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $runtimeArchive -CompressionLevel Optimal
    Compress-Archive -LiteralPath (Join-Path $publishRoot 'DivaModManager.pdb') -DestinationPath $symbolsArchive -CompressionLevel Optimal

    $hashLines = Get-ChildItem -LiteralPath $releaseRoot -Filter '*.zip' |
        Sort-Object Name |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $($_.Name)"
        }
    Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Value $hashLines -Encoding ascii

    Write-Host "Runtime archive: $runtimeArchive"
    Write-Host "Symbols archive: $symbolsArchive"
    Write-Host "Checksums: $(Join-Path $releaseRoot 'SHA256SUMS.txt')"
}
finally {
    Pop-Location
}
