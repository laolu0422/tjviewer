# TjViewer 客户端卸载脚本（后台服务、防火墙、优先级、快捷方式、证书、程序文件）
#
# 用法：
#   pwsh -File uninstall-client.ps1
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File uninstall-client.ps1 -Quiet
#
# 参数：
#   -KeepData    保留 %ProgramData%\TjViewer（日志与退出标记）
#   -KeepCert    保留已导入的 TjViewer 自签名证书
#   -Quiet       静默模式
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'TjViewer'),
    [string]$ServiceName = 'TjViewerService',
    [switch]$KeepData,
    [switch]$KeepCert,
    [switch]$Quiet
)

#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$logFile = Join-Path $env:TEMP 'tjviewer-uninstall.log'
function Write-Log([string]$Message) {
    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -Path $logFile -Value $line -Encoding utf8
    if (-not $Quiet) { Write-Host $Message }
}

try {
    Write-Log '开始卸载 TjViewer'

    # ---- 后台服务 ----
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        & sc.exe delete $ServiceName | Out-Null
        Write-Log "已删除服务 $ServiceName"
    }

    # ---- 运行中的客户端 ----
    Get-Process -Name 'TjViewer' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    # ---- 防火墙 ----
    foreach ($rule in @('TjViewer (TCP-In)', 'TjViewer (UDP-In)')) {
        try { Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule } catch { }
    }
    Write-Log '已清理防火墙规则'

    # ---- IFEO 优先级 ----
    $ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\TjViewer.exe'
    if (Test-Path $ifeo) {
        Remove-Item (Join-Path $ifeo 'PerfOptions') -Recurse -Force -ErrorAction SilentlyContinue
        # 仅当该 IFEO 项是本软件创建（没有其它值）时删除
        $remaining = Get-ChildItem $ifeo -ErrorAction SilentlyContinue
        if (-not $remaining) { Remove-Item $ifeo -Force -ErrorAction SilentlyContinue }
    }

    # ---- 卸载项 / 快捷方式 ----
    Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TjViewer' -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\TjViewer.lnk') -Force -ErrorAction SilentlyContinue

    # ---- 证书（仅移除本脚本安装的 TjViewer 自签名证书）----
    if (-not $KeepCert) {
        foreach ($storeName in @('TrustedPublisher', 'Root')) {
            try {
                $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'LocalMachine')
                $store.Open('ReadWrite')
                $found = @($store.Certificates | Where-Object { $_.Subject -like '*TjViewer*' })
                foreach ($c in $found) { $store.Remove($c) }
                $store.Close()
                if ($found.Count -gt 0) { Write-Log "已移除 $storeName 中的 $($found.Count) 个 TjViewer 证书" }
            }
            catch { Write-Log "清理证书失败（$storeName）：$($_.Exception.Message)" }
        }
    }

    # ---- 程序文件与数据 ----
    if (Test-Path $InstallDir) {
        Remove-Item $InstallDir -Recurse -Force -ErrorAction Stop
        Write-Log "已删除 $InstallDir"
    }
    if (-not $KeepData) {
        $dataDir = Join-Path $env:ProgramData 'TjViewer'
        if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    Write-Log '卸载完成'
    exit 0
}
catch {
    Write-Log "卸载失败：$($_.Exception.Message)"
    if (-not $Quiet) { Write-Error $_ }
    exit 1
}
