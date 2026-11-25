# TjViewer 打包脚本：发布服务器与客户端，并随客户端捆绑 ffmpeg。
# 用法：pwsh -File build.ps1
$ErrorActionPreference = 'Stop'
$custom = Join-Path $env:USERPROFILE '.local\share\dotnet\dotnet.exe'
if (Test-Path $custom) { $dn = $custom } else { $dn = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (-not $dn) { throw "未找到 dotnet SDK" }
$env:DOTNET_ROOT = Split-Path $dn
$root = $PSScriptRoot

Write-Host "发布服务器…"
& $dn publish (Join-Path $root 'src/TjViewer.Server') -c Release -r win-x64 --self-contained false -o (Join-Path $root 'publish/server')
Write-Host "发布客户端…"
& $dn publish (Join-Path $root 'src/TjViewer.Client') -c Release -r win-x64 --self-contained false -o (Join-Path $root 'publish/client')

# 捆绑 ffmpeg（H.264 编解码依赖）
$ff = Get-ChildItem (Join-Path $env:USERPROFILE '.local\share\ffmpeg') -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if (-not $ff) { $ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source }
if ($ff) {
    Copy-Item $ff (Join-Path $root 'publish/client/ffmpeg.exe') -Force
    Write-Host "已捆绑 ffmpeg: $ff"
} else {
    Write-Warning "未找到 ffmpeg，请手动放置 ffmpeg.exe 到 publish/client/ 或加入 PATH"
}
Write-Host "完成：publish/server 与 publish/client"
