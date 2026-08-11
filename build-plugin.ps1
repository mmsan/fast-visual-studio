[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$solution = Join-Path $projectRoot 'FastVisualStudioBridge.sln'
$manifest = Join-Path $projectRoot 'FastVisualStudioBridge\source.extension.vsixmanifest'

if (-not (Test-Path -LiteralPath $solution)) {
    throw "Solution not found: $solution"
}

[xml]$manifestXml = [System.IO.File]::ReadAllText($manifest, [System.Text.Encoding]::UTF8)
$version = $manifestXml.PackageManifest.Metadata.Identity.Version

$msbuildCandidates = @()
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $vswhere) {
    $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe'
    if ($found) { $msbuildCandidates += $found }
}
$msbuildCandidates += @(
    'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'
)
$msbuild = $msbuildCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild was not found. Install the Visual Studio extension development workload.' }

& $msbuild $solution /t:Rebuild "/p:Configuration=$Configuration" /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$vsix = Join-Path $projectRoot "FastVisualStudioBridge\bin\$Configuration\net472\FastVisualStudioBridge.vsix"
if (-not (Test-Path -LiteralPath $vsix)) { throw "VSIX was not produced: $vsix" }

$item = Get-Item -LiteralPath $vsix
$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $vsix
Write-Host "VSIX_VERSION=$version"
Write-Host "VSIX_PATH=$($item.FullName)"
Write-Host "VSIX_SIZE=$($item.Length)"
Write-Host "VSIX_SHA256=$($hash.Hash)"
