# TjViewer 打包脚本：发布自包含（self-contained）服务器与客户端，并随客户端捆绑 ffmpeg。
# 用法：pwsh -File build.ps1 [-ClientOnly] [-ServerOnly]
param(
    [switch]$ClientOnly,
    [switch]$ServerOnly
)
$ErrorActionPreference = 'Stop'
$custom = Join-Path $env:USERPROFILE '.local\share\dotnet\dotnet.exe'
if (Test-Path $custom) { $dn = $custom } else { $dn = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (-not $dn) { throw "未找到 dotnet SDK" }
$env:DOTNET_ROOT = Split-Path $dn
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

if (-not $ClientOnly) {
    Write-Host "发布服务器（linux-x64，自包含）…"
    $out = Join-Path $root 'publish/server-linux-x64'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & $dn publish (Join-Path $root 'src/TjViewer.Server') -c Release -r linux-x64 --self-contained true -o $out
    if ($LASTEXITCODE -ne 0) { throw "服务器发布失败" }
    # 合规文件随包分发
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE') -Force
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $out -Force
    # 打包时保留可执行权限（tar 在 Windows 上不记录 Unix 权限，解包脚本会自行 chmod）
    $tar = Join-Path $dist 'tjviewer-server-linux-x64.tar.gz'
    if (Test-Path $tar) { Remove-Item $tar -Force }
    & tar -czf $tar -C $out .
    Write-Host "已生成 $tar"
}

if (-not $ServerOnly) {
    Write-Host "发布客户端（win-x64，自包含）…"
    $out = Join-Path $root 'publish/client-win-x64'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & $dn publish (Join-Path $root 'src/TjViewer.Client') -c Release -r win-x64 --self-contained true -o $out
    if ($LASTEXITCODE -ne 0) { throw "客户端发布失败" }

    # 捆绑 ffmpeg（H.264 编解码依赖）
    $ff = Get-ChildItem (Join-Path $env:USERPROFILE '.local\share\ffmpeg') -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    if (-not $ff) { $ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source }
    if ($ff) {
        Copy-Item $ff (Join-Path $out 'ffmpeg.exe') -Force
        # ffmpeg 分发包根目录的 LICENSE.txt（GPLv3 全文），合规要求随二进制分发
        $ffLic = Join-Path (Split-Path (Split-Path $ff -Parent) -Parent) 'LICENSE.txt'
        if (Test-Path $ffLic) { Copy-Item $ffLic (Join-Path $out 'ffmpeg-LICENSE.txt') -Force }
        Write-Host "已捆绑 ffmpeg: $ff"
    } else {
        Write-Warning "未找到 ffmpeg，请手动放置 ffmpeg.exe 到 $out 或加入 PATH"
    }

    # 合规文件随包分发
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE') -Force
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $out -Force

    $zip = Join-Path $dist 'tjviewer-client-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "已生成 $zip"
}

Write-Host "完成：产物位于 $dist"
