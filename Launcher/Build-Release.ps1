param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'SysMonitor\SysMonitor.csproj'
[xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
$version = [string](
    $projectXml.Project.PropertyGroup |
        ForEach-Object { $_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$')
{
    throw 'SysMonitor.csproj must define a three-part Version value.'
}

$artifactDirectory = Join-Path $repositoryRoot 'artifacts'
$standalonePublishDirectory = Join-Path $repositoryRoot "work\standalone-core-$version"
$lightPath = Join-Path $artifactDirectory "SysMonitor-v$version-Light.exe"
$standalonePath = Join-Path $artifactDirectory "SysMonitor-v$version-Standalone.exe"
$legacyLauncherPath = Join-Path $artifactDirectory 'SysMonitor.exe'

if (Test-Path -LiteralPath $legacyLauncherPath -PathType Leaf)
{
    Remove-Item -LiteralPath $legacyLauncherPath -Force
}

& (Join-Path $PSScriptRoot 'Build-Portable.ps1') `
    -Configuration $Configuration `
    -RuntimeIdentifier $RuntimeIdentifier
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not (Test-Path -LiteralPath $lightPath -PathType Leaf))
{
    throw "Light build did not produce the expected artifact: $lightPath"
}

New-Item -ItemType Directory -Force -Path $standalonePublishDirectory | Out-Null

dotnet publish $projectPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $standalonePublishDirectory `
    --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$publishedCore = Join-Path $standalonePublishDirectory 'SysMonitor.exe'
if (-not (Test-Path -LiteralPath $publishedCore -PathType Leaf))
{
    throw "Standalone publish did not produce the expected core: $publishedCore"
}

Copy-Item -LiteralPath $publishedCore -Destination $standalonePath -Force

Get-Item -LiteralPath $lightPath, $standalonePath |
    Select-Object Name, FullName, Length
Get-FileHash -Algorithm SHA256 -LiteralPath $lightPath, $standalonePath
