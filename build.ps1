# TjViewer 打包脚本：发布自包含（self-contained）服务器与客户端，并随客户端捆绑 ffmpeg 与后台服务。
# 产物文件名包含版本号（来源：VERSION 文件，可用 -Version 覆盖）。
#
# 用法：
#   pwsh -File build.ps1
#   pwsh -File build.ps1 -ClientOnly
#   pwsh -File build.ps1 -Version 1.0.6 -SignPfx dist/signing/tjviewer-signing.pfx -SignPassword ***
param(
    [switch]$ClientOnly,
    [switch]$ServerOnly,
    [string]$Version,
    [string]$SignPfx,
    [string]$SignPassword,
    [switch]$SkipSign
)
$ErrorActionPreference = 'Stop'
$custom = Join-Path $env:USERPROFILE '.local\share\dotnet\dotnet.exe'
if (Test-Path $custom) { $dn = $custom } else { $dn = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (-not $dn) { throw "未找到 dotnet SDK" }
$env:DOTNET_ROOT = Split-Path $dn
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

if (-not $Version) {
    $versionFile = Join-Path $root 'VERSION'
    $Version = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { '0.0.0' }
}
$tag = if ($Version -match '^v') { $Version } else { "v$Version" }
Write-Host "版本：$tag"

# 安装/卸载脚本随产物一起分发，便于离线安装与批量部署
Copy-Item (Join-Path $root 'scripts/install-server.sh') (Join-Path $dist 'install-server.sh') -Force
Copy-Item (Join-Path $root 'scripts/start-server.cmd') (Join-Path $dist 'start-server.cmd') -Force
foreach ($s in @('install-client.ps1', 'install-client.cmd', 'uninstall-client.ps1')) {
    Copy-Item (Join-Path $root "scripts/$s") (Join-Path $dist $s) -Force
}

# ---- 代码签名 ----
if (-not $SignPfx) {
    $candidate = Join-Path $dist 'signing/tjviewer-signing.pfx'
    if (Test-Path $candidate) { $SignPfx = $candidate }
}
$certFile = Join-Path $dist 'signing/tjviewer-signing.cer'
if (-not $SignPassword) { $SignPassword = $env:TJVIEWER_SIGN_PASSWORD }

function Invoke-Sign([string]$target) {
    if ($SkipSign) { Write-Host "已跳过签名：$target"; return }
    if (-not $SignPfx) { Write-Warning "未找到签名证书，$target 保持未签名（可运行 scripts/new-signing-cert.ps1 生成）"; return }
    $signArgs = @{ Path = $target; Pfx = $SignPfx }
    if ($SignPassword) { $signArgs.Password = $SignPassword }
    & (Join-Path $root 'scripts/sign.ps1') @signArgs
    if ($LASTEXITCODE -ne 0) { throw "签名失败：$target" }
}

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
    $tar = Join-Path $dist "tjviewer-server-linux-x64-$tag.tar.gz"
    if (Test-Path $tar) { Remove-Item $tar -Force }
    & tar -czf $tar -C $out .
    Write-Host "已生成 $tar"

    Write-Host "发布服务器（win-x64，自包含）…"
    $winOut = Join-Path $root 'publish/server-win-x64'
    if (Test-Path $winOut) { Remove-Item $winOut -Recurse -Force }
    & $dn publish (Join-Path $root 'src/TjViewer.Server') -c Release -r win-x64 --self-contained true -o $winOut
    if ($LASTEXITCODE -ne 0) { throw "服务器 Windows 发布失败" }
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $winOut 'LICENSE') -Force
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $winOut -Force
    Copy-Item (Join-Path $root 'scripts/start-server.cmd') $winOut -Force
    if (Test-Path $certFile) { Copy-Item $certFile (Join-Path $winOut 'tjviewer-signing.cer') -Force }
    Invoke-Sign $winOut
    $winZip = Join-Path $dist "tjviewer-server-win-x64-$tag.zip"
    if (Test-Path $winZip) { Remove-Item $winZip -Force }
    Compress-Archive -Path (Join-Path $winOut '*') -DestinationPath $winZip -CompressionLevel Optimal
    Write-Host "已生成 $winZip"
}

if (-not $ServerOnly) {
    Write-Host "发布客户端（win-x64，自包含）…"
    $out = Join-Path $root 'publish/client-win-x64'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & $dn publish (Join-Path $root 'src/TjViewer.Client') -c Release -r win-x64 --self-contained true -o $out
    if ($LASTEXITCODE -ne 0) { throw "客户端发布失败" }

    # 后台服务：独立子目录发布，避免与客户端运行时文件冲突
    Write-Host "发布后台服务（win-x64，自包含）…"
    $svcOut = Join-Path $out 'service'
    & $dn publish (Join-Path $root 'src/TjViewer.Service') -c Release -r win-x64 --self-contained true -o $svcOut
    if ($LASTEXITCODE -ne 0) { throw "后台服务发布失败" }

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

    # 合规文件与安装脚本随包分发
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE') -Force
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $out -Force
    foreach ($s in @('install-client.ps1', 'install-client.cmd', 'uninstall-client.ps1')) {
        Copy-Item (Join-Path $root "scripts/$s") (Join-Path $out $s) -Force
    }
    if (Test-Path $certFile) { Copy-Item $certFile (Join-Path $out 'tjviewer-signing.cer') -Force }

    Invoke-Sign $out

    $zip = Join-Path $dist "tjviewer-client-win-x64-$tag.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "已生成 $zip"
}

Write-Host "完成：产物位于 $dist"
