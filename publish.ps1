param([string]$ArtifactDirectory = (Join-Path $PSScriptRoot 'artifacts'))

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath($ArtifactDirectory)
$destination = Join-Path $artifactRoot 'BilibiliUploader-win-x64'
$project = Join-Path $PSScriptRoot 'BilibiliUploader.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '客户端版本号无效。' }
dotnet publish $project -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' '-p:DebugType=None' -o $destination
if ($LASTEXITCODE -ne 0) { throw '客户端构建失败。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $destination '使用说明.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PRIVACY.md') -Destination (Join-Path $destination 'PRIVACY.md')
$licenseDir = Join-Path $destination 'licenses'
New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$copies = @(
    @('microsoft.web.webview2\1.0.4258.31\LICENSE.txt', 'WebView2-LICENSE.txt'),
    @('microsoft.web.webview2\1.0.4258.31\NOTICE.txt', 'WebView2-NOTICE.txt'),
    @('microsoft.netcore.app.runtime.win-x64\8.0.31\LICENSE.TXT', 'dotnet-LICENSE.txt'),
    @('microsoft.netcore.app.runtime.win-x64\8.0.31\THIRD-PARTY-NOTICES.TXT', 'dotnet-THIRD-PARTY-NOTICES.txt'),
    @('microsoft.windowsdesktop.app.runtime.win-x64\8.0.31\LICENSE', 'WindowsDesktop-LICENSE.txt')
)
foreach ($copy in $copies) {
    Copy-Item -LiteralPath (Join-Path $nugetRoot $copy[0]) -Destination (Join-Path $licenseDir $copy[1])
}
$exe = Join-Path $destination 'BilibiliUploader.exe'
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $destination 'SHA256.txt'), "$hash  BilibiliUploader.exe`n")
$archive = Join-Path $artifactRoot "BilibiliUploader-$version-win-x64.zip"
Compress-Archive -LiteralPath $exe,(Join-Path $destination '使用说明.md'),(Join-Path $destination 'PRIVACY.md'),(Join-Path $destination 'SHA256.txt'),$licenseDir -DestinationPath $archive -Force
Get-Item -LiteralPath $exe,$archive | Select-Object FullName,Length
