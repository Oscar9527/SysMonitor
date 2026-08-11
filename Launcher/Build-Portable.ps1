param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$version = '1.2.15'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $repositoryRoot 'artifacts'
$corePath = Join-Path $artifactDirectory "SysMonitor.Core.$version.exe"
$launcherPath = Join-Path $artifactDirectory 'SysMonitor.exe'
$projectPath = Join-Path $repositoryRoot 'SysMonitor\SysMonitor.csproj'
$iconPath = Join-Path $repositoryRoot 'SysMonitor\Assets\sysmonitor.ico'
$sourcePath = Join-Path $PSScriptRoot 'SysMonitorLauncher.cs'

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

dotnet publish $projectPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $artifactDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Move-Item -LiteralPath (Join-Path $artifactDirectory 'SysMonitor.exe') `
    -Destination $corePath -Force

$sdkLine = dotnet --list-sdks | Select-Object -Last 1
if ($sdkLine -notmatch '^(\S+)\s+\[(.+)\]$')
{
    throw "Unable to locate the active .NET SDK: $sdkLine"
}

$sdkVersion = $Matches[1]
$sdkRoot = $Matches[2]
$compiler = Join-Path $sdkRoot "$sdkVersion\Roslyn\bincore\csc.dll"
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

dotnet $compiler `
    /noconfig /nostdlib+ /langversion:latest /nullable:enable `
    /target:winexe /platform:anycpu /optimize+ /deterministic+ `
    "/out:$launcherPath" `
    "/win32icon:$iconPath" `
    "/resource:$corePath,SysMonitor.Core.$version.exe" `
    "/reference:$framework\mscorlib.dll" `
    "/reference:$framework\System.dll" `
    "/reference:$framework\System.Core.dll" `
    "/reference:$framework\System.Windows.Forms.dll" `
    $sourcePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-Item -LiteralPath $launcherPath | Select-Object FullName, Length
Get-FileHash -Algorithm SHA256 -LiteralPath $launcherPath
