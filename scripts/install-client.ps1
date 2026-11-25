# TjViewer 客户端安装脚本（Program Files + Windows 后台服务 + 批量部署）
#
# 交互安装：右键“使用 PowerShell 运行”，或在管理员 PowerShell 中：
#   pwsh -File install-client.ps1
#
# 静默 / 批量部署（SCCM、Intune、域脚本等）：
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File install-client.ps1 -Silent -NoShortcut
#
# 参数：
#   -SourceDir      安装包目录（默认脚本所在目录，须包含 TjViewer.exe）
#   -InstallDir     安装目录（默认 %ProgramFiles%\TjViewer）
#   -NoService      不安装后台服务
#   -NoFirewall     不添加入站防火墙规则
#   -NoCertTrust    不导入随包的代码签名证书
#   -NoShortcut     不创建开始菜单快捷方式
#   -Silent         静默模式（不输出逐项提示）
[CmdletBinding()]
param(
    [string]$SourceDir = '',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'TjViewer'),
    [string]$ServiceName = 'TjViewerService',
    [switch]$NoService,
    [switch]$NoFirewall,
    [switch]$NoCertTrust,
    [switch]$NoShortcut,
    [switch]$Silent
)

#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$dataDir = Join-Path $env:ProgramData 'TjViewer'
$logDir = Join-Path $dataDir 'logs'
$logFile = Join-Path $logDir 'install.log'
if ([string]::IsNullOrWhiteSpace($SourceDir)) { $SourceDir = $PSScriptRoot }
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Write-Log([string]$Message) {
    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -Path $logFile -Value $line -Encoding utf8
    if (-not $Silent) { Write-Host $Message }
}

function Invoke-Sc([string[]]$Arguments) {
    $out = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $($Arguments -join ' ') 失败：$out"
    }
    return $out
}

try {
    $source = (Resolve-Path $SourceDir).Path
    if ($source.TrimEnd('\') -ieq $InstallDir.TrimEnd('\')) {
        throw '请从解压后的客户端目录运行安装脚本，不要直接运行已安装目录中的副本'
    }
    $clientExe = Join-Path $source 'TjViewer.exe'
    if (-not (Test-Path $clientExe)) {
        throw "安装包不完整：$source 下缺少 TjViewer.exe"
    }

    $version = (Get-Item $clientExe).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        $vf = Join-Path (Split-Path $source -Parent) 'VERSION'
        $version = if (Test-Path $vf) { (Get-Content $vf -Raw).Trim() } else { '0.0.0' }
    }

    Write-Log "开始安装 TjViewer $version"
    Write-Log "源目录：$source"
    Write-Log "安装目录：$InstallDir"

    # ---- 停止旧版本（服务 + 客户端）----
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Log '停止并删除旧后台服务…'
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        & sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Milliseconds 500
    }
    Get-Process -Name 'TjViewer' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    # ---- 复制程序文件 ----
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $InstallDir -Recurse -Force
    # 去掉从网络下载解压带来的“来自 Internet”标记，避免 SmartScreen 额外拦截
    Get-ChildItem $InstallDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    Write-Log '程序文件已复制'

    # ---- 代码签名证书信任（自签名证书需要，否则签名显示“未知发布者”）----
    $cerPath = Join-Path $InstallDir 'tjviewer-signing.cer'
    if (-not $NoCertTrust -and (Test-Path $cerPath)) {
        try {
            $cer = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerPath)
            if ($cer.Subject -like '*TjViewer*') {
                foreach ($storeName in @('TrustedPublisher', 'Root')) {
                    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'LocalMachine')
                    $store.Open('ReadWrite')
                    if (-not ($store.Certificates | Where-Object Thumbprint -eq $cer.Thumbprint)) {
                        $store.Add($cer)
                    }
                    $store.Close()
                }
                Write-Log "已导入签名证书：$($cer.Thumbprint)"
            } else {
                Write-Log "跳过证书导入（主题不匹配）：$($cer.Subject)"
            }
        }
        catch { Write-Log "证书导入失败（不影响运行）：$($_.Exception.Message)" }
    }

    # ---- 系统调度优先级：High（CPU/IO），ffmpeg 子进程继承 ----
    try {
        $ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\TjViewer.exe\PerfOptions'
        New-Item -Path $ifeo -Force | Out-Null
        Set-ItemProperty -Path $ifeo -Name 'CpuPriorityClass' -Value 3 -Type DWord
        Set-ItemProperty -Path $ifeo -Name 'IoPriority' -Value 3 -Type DWord
        Write-Log '已配置调度优先级：High（CpuPriorityClass=3 / IoPriority=3）'
    }
    catch { Write-Log "优先级配置失败：$($_.Exception.Message)" }

    # ---- 防火墙：P2P 需要入站 UDP/TCP ----
    if (-not $NoFirewall) {
        try {
            foreach ($rule in @('TjViewer (TCP-In)', 'TjViewer (UDP-In)')) {
                Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            }
            New-NetFirewallRule -DisplayName 'TjViewer (TCP-In)' -Direction Inbound -Action Allow `
                -Program (Join-Path $InstallDir 'TjViewer.exe') -Protocol TCP -Profile Any | Out-Null
            New-NetFirewallRule -DisplayName 'TjViewer (UDP-In)' -Direction Inbound -Action Allow `
                -Program (Join-Path $InstallDir 'TjViewer.exe') -Protocol UDP -Profile Any | Out-Null
            Write-Log '已添加入站防火墙规则（TCP/UDP）'
        }
        catch { Write-Log "防火墙配置失败（可手动放行）：$($_.Exception.Message)" }
    }

    # ---- ProgramData：日志目录 + 用户退出标记（Users 可写）----
    try {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        & icacls.exe $dataDir /grant '*S-1-5-32-545:(OI)(CI)M' /T /C | Out-Null
    }
    catch { Write-Log "ProgramData 权限配置失败：$($_.Exception.Message)" }

    # ---- Windows 后台服务：开机自启、无需用户登录、崩溃自动重启 ----
    if (-not $NoService) {
        $serviceExe = Join-Path $InstallDir 'service\TjViewer.Service.exe'
        if (-not (Test-Path $serviceExe)) {
            Write-Log "警告：缺少后台服务程序 $serviceExe，跳过服务安装"
        } else {
            try {
                Invoke-Sc @('create', $ServiceName, 'binPath=', "`"$serviceExe`"", 'start=', 'auto',
                    'obj=', 'LocalSystem', 'DisplayName=', 'TjViewer Background Service') | Out-Null
                Invoke-Sc @('description', $ServiceName, 'TjViewer 后台服务：开机自启，用户登录后拉起客户端并自动恢复') | Out-Null
                Invoke-Sc @('failure', $ServiceName, 'reset=', '86400',
                    'actions=', 'restart/5000/restart/5000/restart/5000') | Out-Null
                Start-Service -Name $ServiceName
                Write-Log '后台服务已安装并启动（LocalSystem / 自动启动）'
            }
            catch { Write-Log "后台服务安装失败：$($_.Exception.Message)" }
        }
    }

    # ---- 开始菜单快捷方式 ----
    if (-not $NoShortcut) {
        try {
            $lnk = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\TjViewer.lnk'
            $ws = New-Object -ComObject WScript.Shell
            $s = $ws.CreateShortcut($lnk)
            $s.TargetPath = Join-Path $InstallDir 'TjViewer.exe'
            $s.WorkingDirectory = $InstallDir
            $s.IconLocation = (Join-Path $InstallDir 'TjViewer.exe') + ',0'
            $s.Description = 'TjViewer 远程控制'
            $s.Save()
            Write-Log '已创建开始菜单快捷方式'
        }
        catch { Write-Log "快捷方式创建失败：$($_.Exception.Message)" }
    }

    # ---- 卸载项（控制面板 / 应用和功能）----
    try {
        $uninst = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TjViewer'
        New-Item -Path $uninst -Force | Out-Null
        $uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\uninstall-client.ps1`""
        Set-ItemProperty -Path $uninst -Name 'DisplayName' -Value 'TjViewer 远程控制'
        Set-ItemProperty -Path $uninst -Name 'DisplayVersion' -Value $version
        Set-ItemProperty -Path $uninst -Name 'Publisher' -Value 'laolu0422'
        Set-ItemProperty -Path $uninst -Name 'InstallLocation' -Value $InstallDir
        Set-ItemProperty -Path $uninst -Name 'DisplayIcon' -Value (Join-Path $InstallDir 'TjViewer.exe')
        Set-ItemProperty -Path $uninst -Name 'UninstallString' -Value $uninstallCmd
        Set-ItemProperty -Path $uninst -Name 'QuietUninstallString' -Value ($uninstallCmd + ' -Quiet')
        Set-ItemProperty -Path $uninst -Name 'InstallDate' -Value (Get-Date -Format 'yyyyMMdd')
        Set-ItemProperty -Path $uninst -Name 'NoModify' -Value 1 -Type DWord
        Set-ItemProperty -Path $uninst -Name 'NoRepair' -Value 1 -Type DWord
        Write-Log '已写入卸载项'
    }
    catch { Write-Log "卸载项写入失败：$($_.Exception.Message)" }

    Write-Log "安装完成：TjViewer $version → $InstallDir"
    Write-Log "后台服务：$(if ($NoService) { '未安装' } else { $ServiceName + '（自动启动）' })"
    Write-Log "日志目录：$logDir"
    exit 0
}
catch {
    Write-Log "安装失败：$($_.Exception.Message)"
    if (-not $Silent) { Write-Error $_ }
    exit 1
}
