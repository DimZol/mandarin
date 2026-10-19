# Builds Mandarin into dist\Mandarin (framework-dependent: needs the .NET 10 Desktop Runtime).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1 [-SelfContained] [-Zip] [-Output <folder>]
param(
    [switch]$SelfContained,
    [switch]$Zip,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = if ($Output) { $Output } else { Join-Path $root 'dist\Mandarin' }

$free = (Get-PSDrive ($root.Substring(0, 1))).Free / 1GB
if ($free -lt 2) { Write-Warning ("Low disk space: {0:N1} GB free" -f $free) }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish (Join-Path $root 'src\Mandarin\Mandarin.csproj') -c Release -r win-x64 --self-contained $sc -o $out -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

# satellite resource folders of third-party libraries are not needed
Get-ChildItem $out -Directory | Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' -and $_.Name -notin 'ru', 'runtimes' } |
    Remove-Item -Recurse -Force

Copy-Item (Join-Path $root 'LICENSE') $out -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $out -ErrorAction SilentlyContinue

$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Done: {0}  ({1:N0} MB)" -f $out, $size)

if ($Zip) {
    $version = (Select-Xml -Path (Join-Path $root 'src\Mandarin\Mandarin.csproj') -XPath '//Version').Node.InnerText
    $flavor = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
    $zipPath = Join-Path (Split-Path $out) "Mandarin-$version-win-x64-$flavor.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host ("Archive: {0}  ({1:N0} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
}
