param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$version = '1.4.0'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $repositoryRoot 'artifacts'
$publishDirectory = Join-Path $repositoryRoot "work\portable-core-$version"
$corePath = Join-Path $artifactDirectory "SysMonitor.Core.$version.exe"
$launcherPath = Join-Path $artifactDirectory 'SysMonitor.exe'
$projectPath = Join-Path $repositoryRoot 'SysMonitor\SysMonitor.csproj'
$iconPath = Join-Path $repositoryRoot 'SysMonitor\Assets\sysmonitor.ico'
$sourcePath = Join-Path $PSScriptRoot 'SysMonitorLauncher.cs'

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

# Remove obsolete intermediate cores left by older build scripts. The release
# directory must contain only the portable launcher requested by users.
Get-ChildItem -LiteralPath $artifactDirectory -Filter 'SysMonitor.Core.*.exe' -File |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

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

# The core is embedded as a resource in the launcher. Keep the distributable
# artifact directory genuinely single-file after compilation succeeds.
Remove-Item -LiteralPath $corePath -Force

Get-Item -LiteralPath $launcherPath | Select-Object FullName, Length
Get-FileHash -Algorithm SHA256 -LiteralPath $launcherPath
