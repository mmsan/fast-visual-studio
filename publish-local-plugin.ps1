[CmdletBinding()]
param(
    [string]$RepositoryPath = 'D:\docker\www\vs-extensions',
    [string]$RepositoryUrl = 'http://127.0.0.1:8800/vs-extensions',
    [switch]$NoVersionBump
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$manifestPath = Join-Path $projectRoot 'FastVisualStudioBridge\source.extension.vsixmanifest'
$assemblyInfoPath = Join-Path $projectRoot 'FastVisualStudioBridge\AssemblyInfo.cs'
$originalManifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8)
$originalAssemblyInfo = [System.IO.File]::ReadAllText($assemblyInfoPath, [System.Text.Encoding]::UTF8)
$published = $false

try {
    [xml]$manifestXml = $originalManifest
    $oldVersion = [version]$manifestXml.PackageManifest.Metadata.Identity.Version
    $newVersion = if ($NoVersionBump) { $oldVersion } else { [version]::new($oldVersion.Major, $oldVersion.Minor, $oldVersion.Build + 1) }

    if (-not $NoVersionBump) {
        $newManifest = $originalManifest -replace '(?<=<Identity Id="FastVisualStudioBridge\.27861355-A640-4497-911A-EA99B558E93F" Version=")[^"]+', $newVersion.ToString(3)
        $newAssemblyInfo = $originalAssemblyInfo `
            -replace '(?<=AssemblyVersion\(")[^"]+', ($newVersion.ToString(3) + '.0') `
            -replace '(?<=AssemblyFileVersion\(")[^"]+', ($newVersion.ToString(3) + '.0')
        [System.IO.File]::WriteAllText($manifestPath, $newManifest, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($assemblyInfoPath, $newAssemblyInfo, [System.Text.UTF8Encoding]::new($false))
    }

    & (Join-Path $projectRoot 'build-plugin.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "VSIX build failed with exit code $LASTEXITCODE" }

    $vsix = Join-Path $projectRoot 'dist\FastVisualStudioBridge.vsix'
    if (-not (Test-Path -LiteralPath $vsix)) { throw "Release VSIX was not found: $vsix" }
    New-Item -ItemType Directory -Path $RepositoryPath -Force | Out-Null
    $publishedName = "fast-visual-studio-bridge-$($newVersion.ToString(3)).vsix"
    $publishedPath = Join-Path $RepositoryPath $publishedName
    Copy-Item -LiteralPath $vsix -Destination $publishedPath -Force

    $vsixUtil = Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.vssdk.buildtools') -Recurse -Filter 'VsixUtil.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\lib\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $vsixUtil) { throw 'VsixUtil.exe was not found. Restore Microsoft.VSSDK.BuildTools first.' }

    & $vsixUtil.FullName createVsixFeed -source $RepositoryPath -output $RepositoryPath -recursive -ignoreErrors -fileName 'AtomFeed'
    if ($LASTEXITCODE -ne 0) { throw "Atom Feed generation failed with exit code $LASTEXITCODE" }

    $feedPath = Join-Path $RepositoryPath 'AtomFeed.xml'
    if (-not (Test-Path -LiteralPath $feedPath)) { throw "Atom Feed was not produced: $feedPath" }
    $feedUrl = "$($RepositoryUrl.TrimEnd('/'))/AtomFeed.xml"
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $feedUrl -TimeoutSec 5
        if ($response.StatusCode -ne 200) { throw "HTTP status was $($response.StatusCode)" }
        Write-Host "FEED_VERIFIED=$feedUrl"
    } catch {
        Write-Warning "Files were published, but the HTTP feed could not be verified: $feedUrl; $($_.Exception.Message)"
    }

    $published = $true
    Write-Host "PUBLISHED_VERSION=$($newVersion.ToString(3))"
    Write-Host "PUBLISHED_VSIX=$publishedPath"
    Write-Host "FEED_URL=$feedUrl"
} finally {
    if (-not $published -and -not $NoVersionBump) {
        [System.IO.File]::WriteAllText($manifestPath, $originalManifest, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($assemblyInfoPath, $originalAssemblyInfo, [System.Text.UTF8Encoding]::new($false))
        Write-Warning 'Publishing failed. Original version files were restored.'
    }
}
