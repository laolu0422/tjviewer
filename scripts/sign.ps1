# 对 TjViewer 的构建产物做 Authenticode 签名。
# 证书来源优先级：-Pfx/-Password > -Thumbprint > dist/signing/tjviewer-signing.pfx > 当前用户 My 存储中的 TjViewer 证书。
#
# 用法：
#   pwsh -File scripts/sign.ps1 -Path publish/client-win-x64
#   pwsh -File scripts/sign.ps1 -Path publish/client-win-x64 -Pfx cert.pfx -Password ***
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Pfx,
    [string]$Password,
    [string]$Thumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

$defaultPwdFile = Join-Path $PSScriptRoot '..\dist\signing\password.txt'
if (-not $Password) { $Password = $env:TJVIEWER_SIGN_PASSWORD }
if (-not $Password -and (Test-Path $defaultPwdFile)) {
    $Password = (Get-Content $defaultPwdFile -Raw).Trim()
}

function Load-Pfx([string]$path, [string]$pwd) {
    # Windows PowerShell 5.1 的 Get-PfxCertificate 不支持 -Password，直接用 .NET 加载
    if ($pwd) {
        return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($path, $pwd)
    }
    return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($path)
}

function Resolve-SigningCert {
    if ($Pfx) {
        $c = Load-Pfx $Pfx $Password
        if ($c) { return $c }
    }
    if ($Thumbprint) {
        $c = Get-ChildItem Cert:\CurrentUser\My\$Thumbprint -ErrorAction SilentlyContinue
        if ($c) { return $c }
        throw "当前用户证书存储中找不到指纹 $Thumbprint"
    }

    $defaultPfx = Join-Path $PSScriptRoot '..\dist\signing\tjviewer-signing.pfx'
    $defaultPwdFile = Join-Path $PSScriptRoot '..\dist\signing\password.txt'
    if (-not $Pfx -and (Test-Path $defaultPfx)) {
        if (-not $Password -and (Test-Path $defaultPwdFile)) {
            $Password = (Get-Content $defaultPwdFile -Raw).Trim()
        }
        $c = Load-Pfx $defaultPfx $Password
        if ($c) { return $c }
    }

    $c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -like '*TjViewer*' } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    return $c
}

$cert = Resolve-SigningCert
if (-not $cert) {
    Write-Warning '未找到代码签名证书，跳过签名。可先运行 scripts/new-signing-cert.ps1 或指定 -Pfx。'
    exit 0
}
Write-Host "使用签名证书：$($cert.Subject)（$($cert.Thumbprint)）"

$root = (Resolve-Path $Path).Path
$targets = if (Test-Path $root -PathType Container) {
    Get-ChildItem $root -Recurse -File |
        Where-Object { $_.Name -like 'TjViewer*' -and $_.Extension -in '.exe', '.dll' }
} else {
    Get-Item $root
}
$targets = @($targets)
if ($targets.Count -eq 0) {
    Write-Warning "没有找到可签名的文件：$root"
    exit 0
}

$failed = 0
foreach ($file in $targets) {
    try {
        $sig = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -TimestampServer $TimestampUrl
        if ($sig.Status -ne 'Valid' -and $TimestampUrl) {
            # 时间戳服务器不可达时退化为不带时间戳签名（仅影响签名证书过期后的有效期）
            Write-Warning "时间戳失败（$($sig.StatusMessage)），改为无时间戳签名：$($file.Name)"
            $sig = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert
        }
        # 自签名证书未导入信任存储时状态为 UnknownError/NotTrusted，但签名本身已写入；
        # 以“签名者证书指纹”判定是否成功，避免构建机误报失败。
        $verify = Get-AuthenticodeSignature -FilePath $file.FullName
        $signedByUs = $verify.SignerCertificate -and $verify.SignerCertificate.Thumbprint -eq $cert.Thumbprint
        if (-not $signedByUs -or $verify.Status -eq 'HashMismatch') {
            $failed++
            Write-Warning "签名失败（$($verify.Status)）：$($file.FullName) - $($verify.StatusMessage)"
        } elseif ($verify.Status -ne 'Valid') {
            Write-Host "已签名（证书未在本机受信任，安装器导入 .cer 后显示有效）：$($file.FullName)"
        } else {
            Write-Host "已签名：$($file.FullName)"
        }
    }
    catch {
        $failed++
        Write-Warning "签名失败：$($file.FullName) - $($_.Exception.Message)"
    }
}

if ($failed -gt 0) {
    Write-Warning "完成，但有 $failed 个文件签名失败。"
    exit 1
}
Write-Host "签名完成：共 $($targets.Count) 个文件。"
