# 生成 TjViewer 代码签名证书（自签名，适用于内部/批量部署）。
# 生产环境请改用受信任 CA 签发的代码签名证书（PFX），并通过 -Pfx/-Password 传给 sign.ps1。
#
# 用法：
#   pwsh -File scripts/new-signing-cert.ps1
#   pwsh -File scripts/new-signing-cert.ps1 -Subject "CN=YourOrg Code Signing" -Years 3
[CmdletBinding()]
param(
    [string]$Subject = 'CN=TjViewer Self-Signed Code Signing',
    [string]$OutDir = '',
    [string]$Password,
    [int]$Years = 5
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $PSScriptRoot '..\dist\signing'
}
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if ([string]::IsNullOrWhiteSpace($Password)) {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $Password = [Convert]::ToBase64String($bytes)
}

Write-Host "创建自签名代码签名证书：$Subject"
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable `
    -KeyLength 3072 `
    -KeyAlgorithm RSA `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears($Years)

$secure = ConvertTo-SecureString $Password -AsPlainText -Force
$pfx = Join-Path $OutDir 'tjviewer-signing.pfx'
$cer = Join-Path $OutDir 'tjviewer-signing.cer'
$pwdFile = Join-Path $OutDir 'password.txt'

Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
# dist/ 已在 .gitignore 中；该文件仅用于本机自动构建，请勿提交或外传
Set-Content -Path $pwdFile -Value $Password -Encoding ascii

Write-Host ''
Write-Host '已生成：'
Write-Host "  PFX        $pfx"
Write-Host "  CER        $cer"
Write-Host "  口令文件   $pwdFile"
Write-Host "  指纹       $($cert.Thumbprint)"
Write-Host ''
Write-Host '说明：自签名证书只有被目标机信任后签名才算“有效”。'
Write-Host '     install-client.ps1 会把随包的 tjviewer-signing.cer 导入本机'
Write-Host '     “受信任的发布者”和“受信任的根证书颁发机构”。'
