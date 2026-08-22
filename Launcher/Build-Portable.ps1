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
$assemblyVersion = [string](
    $projectXml.Project.PropertyGroup |
        ForEach-Object { $_.AssemblyVersion } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $assemblyVersion -notmatch '^\d+\.\d+\.\d+\.\d+$')
{
    throw 'SysMonitor.csproj must define three-part Version and four-part AssemblyVersion values.'
}
$assemblyProductVersion = ([version]$assemblyVersion).ToString(3)
if ($assemblyProductVersion -ne $version)
{
    throw "Version ($version) must match the first three AssemblyVersion components ($assemblyProductVersion)."
}

$artifactDirectory = Join-Path $repositoryRoot 'artifacts'
$publishDirectory = Join-Path $repositoryRoot "work\portable-core-$version"
$corePath = Join-Path $artifactDirectory "SysMonitor.Core.$version.exe"
$launcherPath = Join-Path $artifactDirectory "SysMonitor-v$version-Light.exe"
$iconPath = Join-Path $repositoryRoot 'SysMonitor\Assets\sysmonitor.ico'
$sourcePath = Join-Path $PSScriptRoot 'SysMonitorLauncher.cs'
$versionSourcePath = Join-Path $publishDirectory 'LauncherVersion.g.cs'

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

@"
using System.Reflection;
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$version")]
"@ | Set-Content -LiteralPath $versionSourcePath -Encoding UTF8

# Remove obsolete intermediate cores left by older build scripts. The release
# directory must not retain obsolete intermediate cores.
Get-ChildItem -LiteralPath $artifactDirectory -Filter 'SysMonitor.Core.*.exe' -File |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

# WPF-generated sources are stored under a runtime-specific obj directory.
# Clear them before publishing so switching branches or restoring an older
# snapshot cannot compile stale x:Name fields from a previous XAML layout.
dotnet restore $projectPath `
    -r $RuntimeIdentifier `
    --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet clean $projectPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish $projectPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Move-Item -LiteralPath (Join-Path $publishDirectory 'SysMonitor.exe') `
    -Destination $corePath -Force

$sdkLine = dotnet --list-sdks | Select-Object -Last 1
if ($sdkLine -notmatch '^(\S+)\s+\[(.+)\]$')
{
    throw "Unable to locate the active .NET SDK: $sdkLine"
}

$sdkVersion = $Matches[1]
$sdkRoot = $Matches[2]
if ([version]$sdkVersion -lt [version]'8.0.100')
{
    throw "Building SysMonitor requires .NET 8 SDK or newer; selected SDK is $sdkVersion."
}

$compiler = Join-Path $sdkRoot "$sdkVersion\Roslyn\bincore\csc.dll"
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

dotnet $compiler `
    /noconfig /nostdlib+ /langversion:latest /nullable:enable `
    /target:winexe /platform:anycpu /optimize+ /deterministic+ `
    "/out:$launcherPath" `
    "/win32icon:$iconPath" `
    "/resource:$corePath,SysMonitor.Core.exe" `
    "/reference:$framework\mscorlib.dll" `
    "/reference:$framework\System.dll" `
    "/reference:$framework\System.Core.dll" `
    "/reference:$framework\System.Windows.Forms.dll" `
    $sourcePath `
    $versionSourcePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The core is embedded as a resource in the launcher. Keep the distributable
# artifact directory genuinely single-file after compilation succeeds.
Remove-Item -LiteralPath $corePath -Force

Get-Item -LiteralPath $launcherPath | Select-Object FullName, Length
Get-FileHash -Algorithm SHA256 -LiteralPath $launcherPath
