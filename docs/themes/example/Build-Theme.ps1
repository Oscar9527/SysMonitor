$ErrorActionPreference = 'Stop'

$exampleRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$temporaryZip = Join-Path $exampleRoot 'ocean-night.zip'
$themePackage = Join-Path $exampleRoot 'ocean-night.smonitor-theme'
$sourceFiles = @(
    (Join-Path $exampleRoot 'manifest.json'),
    (Join-Path $exampleRoot 'theme.json'),
    (Join-Path $exampleRoot 'README.md'),
    (Join-Path $exampleRoot 'LICENSE.txt')
)

Remove-Item -LiteralPath $temporaryZip -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $themePackage -Force -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $sourceFiles -DestinationPath $temporaryZip -CompressionLevel Optimal
Move-Item -LiteralPath $temporaryZip -Destination $themePackage

Write-Output "Created: $themePackage"
