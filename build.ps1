# Builds Mandarin.
#   Windows 10/11 (WPF):   build.ps1 [-SelfContained] [-Zip] [-Output <folder>]   -> dist\Mandarin (needs the .NET 10 Desktop Runtime)
#   Linux x64:             build.ps1 -Linux [-Zip]                                -> dist\Mandarin-linux (self-contained, .tar.gz)
#   Windows 7/8 x64:       build.ps1 -Win7 [-Zip]                                 -> dist\Mandarin-win7 (self-contained .NET 6)
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1 ...
param(
    [switch]$SelfContained,
    [switch]$Zip,
    [switch]$Linux,
    [switch]$Win7,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$free = (Get-PSDrive ($root.Substring(0, 1))).Free / 1GB
if ($free -lt 2) { Write-Warning ("Low disk space: {0:N1} GB free" -f $free) }

$version = (Select-Xml -Path (Join-Path $root 'src\Mandarin\Mandarin.csproj') -XPath '//Version').Node.InnerText

if ($Linux -or $Win7) {
    # the cross-platform build: Avalonia interface, the same engines
    $project = Join-Path $root 'src\Mandarin.Cross\Mandarin.Cross.csproj'
    $tfm = if ($Linux) { 'net10.0' } else { 'net6.0' }
    $rid = if ($Linux) { 'linux-x64' } else { 'win-x64' }
    $out = if ($Output) { $Output } elseif ($Linux) { Join-Path $root 'dist\Mandarin-linux' } else { Join-Path $root 'dist\Mandarin-win7' }
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish $project -c Release -f $tfm -r $rid --self-contained true -o $out -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    Get-ChildItem $out -Directory | Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' -and $_.Name -notin 'ru', 'runtimes' } |
        Remove-Item -Recurse -Force
    Copy-Item (Join-Path $root 'LICENSE') $out -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $out -ErrorAction SilentlyContinue
    $size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("Done: {0}  ({1:N0} MB)" -f $out, $size)
    if ($Zip) {
        if ($Linux) {
            $archive = Join-Path (Split-Path $out) "Mandarin-$version-linux-x64.tar.gz"
            dotnet run (Join-Path $root 'tools\MakeTar.cs') -- $out $archive "Mandarin-$version"
            if ($LASTEXITCODE -ne 0) { throw 'Packing failed' }
        }
        else {
            $archive = Join-Path (Split-Path $out) "Mandarin-$version-win7-x64.zip"
            if (Test-Path $archive) { Remove-Item $archive -Force }
            Compress-Archive -Path (Join-Path $out '*') -DestinationPath $archive -CompressionLevel Optimal
        }
        Write-Host ("Archive: {0}  ({1:N0} MB)" -f $archive, ((Get-Item $archive).Length / 1MB))
    }
    return
}

$out = if ($Output) { $Output } else { Join-Path $root 'dist\Mandarin' }
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
    $flavor = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
    $zipPath = Join-Path (Split-Path $out) "Mandarin-$version-win-x64-$flavor.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host ("Archive: {0}  ({1:N0} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
}
