# TjViewer 远程控制

轻量级 Windows 远程控制软件（服务端 + 客户端），面向小范围、低带宽的使用场景：服务器只有约 10M 上下行带宽时，依然可以低延迟观看桌面、远程键鼠操控、双向音频与文件传输。

## 功能特性

- **远程桌面观看**：DXGI Desktop Duplication 采集（失败自动回退 GDI）→ H.264 编码 → KCP/UDP 通道；**静止画面也按设定帧率持续推流**，操作反馈接近实时；画面不再合成远端光标，由控制端渲染本地鼠标。支持 NVIDIA NVENC、Intel QSV、AMD AMF 硬件编码与 D3D11VA 硬件解码，探测失败自动回退 libx264 / 软件解码。
- **画质调节**：自适应模式按发送队列拥塞升降档（积压持续满 1 秒才降档）；手动模式用滑块独立调节**帧率（15/30/40/60/90/120）**与**码率（2/4/6/8/10 Mbps）**，可一键锁定；服务端硬限制 4K / 120fps / 10Mbps，超限请求会被钳制或拦截。
- **连接方式**：STUN 打洞建立 P2P 直连，UDP 上跑 KCP（低延迟、抗丢包）；打洞失败自动回退**服务器全功能中转**（WebSocket 转发视频/音频/输入/文件/控制全部消息）；单条通道按首字节复用各类消息。
- **远程键鼠**：键盘扫描码注入（支持扩展键）、绝对坐标鼠标、按键、滚轮、文本输入；控制窗口激活时通过低级键盘钩子接管键盘，**Alt+Tab、Win 组合等系统快捷键完整透传到被控端**。
- **陌生设备连接**：无需同账号，被控端自动获得服务端分配的「观看」「控制」两套 **9 位数字连接码 + 8 位验证码**；验证码每次连接后轮换，可选用固定连接码；控制端可记住连接码但绝不保存验证码。
- **双向音频**：被控端系统声音回传（WASAPI Loopback + Opus），控制端麦克风可下发至被控端（需用户主动开启）。
- **文件传输**：P2P 分块传输 + SHA256 校验 + 进度回调，保存到 `下载/TjViewer`；另提供服务器小文件中转（AES-GCM 内存暂存、单文件 ≤ 50MB、10 分钟过期、须鉴权）。
- **账户与设备**：邮箱必填注册；用户名或邮箱 + 密码登录；Argon2id 密码哈希；JWT 访问令牌 + 刷新令牌（仅存哈希）；自有 SMTP 发送找回密码邮件；**记住账号密码 / 自动登录 / 退出登录**（密码经 DPAPI 加密存储）。
- **网页管理后台**：用户启用/禁用/删除/重置密码，查看设备、会话、审计日志与系统配置。
- **客户端易用性**：设备列表、一键连接、开机自启（HKCU Run，用户确认后写入）；紫白 / 紫黑主题跟随系统切换，自绘标题栏与 TJV 应用图标。
- **Windows 安装与后台服务**：一键安装到 `%ProgramFiles%\TjViewer`，注册 `TjViewerService` 系统服务（LocalSystem、开机自启、无需用户登录即可运行、崩溃自动重启、用户登录后自动拉起客户端）；安装器配置 High 调度优先级、防火墙入站规则与代码签名证书信任，支持静默/批量部署。
- **代码签名**：提供自签名证书生成与 Authenticode 签名脚本；安装包随附 `.cer` 并由安装器导入本机信任存储（生产环境可替换为 CA 签发的 PFX）。

## 架构

```
┌──────────────┐    HTTPS / WebSocket（鉴权、信令、管理）   ┌────────────────┐
│  控制端客户端  │ ───────────────────────────────────────▶ │  TjViewer 服务端  │
└──────┬───────┘                                          └────────┬───────┘
       │        STUN 打洞 + KCP/UDP（视频/输入/音频/文件）             │
       ▼                                                           │
┌──────────────┐ ◀──────────────────────────────────────────────────┘
│  被控端客户端  │
└──────────────┘
```

- **服务端**：ASP.NET Core Minimal API + EF Core + SQLite，数据落在单个 `tjviewer.db` 文件，首次启动自动建库；`/ws/signal` 负责鉴权信令，`/ws/relay` 负责会话全功能中转。
- **采集与编解码**：Vortice（DXGI / Direct3D 11）采集，ffmpeg 子进程经标准输入输出管道处理 BGRA 原始帧。
- **音频**：NAudio（WASAPI）+ Concentus（Opus 编解码）。

## 目录结构

```
src/
  TjViewer.Server/    服务端：鉴权、设备、管理后台、信令、文件中转
  TjViewer.Client/    WPF 客户端：登录、设备列表、远程控制窗口、托盘/自启
  TjViewer.Service/   Windows 后台服务：开机自启、会话拉起、崩溃恢复
  TjViewer.Core/      采集、编解码、KCP、网络、音频、键鼠注入、文件传输
  TjViewer.Shared/    客户端与服务端共用的协议、DTO、路由与画质档位
scripts/              安装/卸载、签名、服务器安装脚本
VERSION               版本号唯一来源（构建产物文件名与程序集版本）
build.ps1             发布脚本（server/client/service + ffmpeg + 签名）
TjViewer.slnx         解决方案
```

## 环境要求

- Windows 10 1809+ / Windows 11，x64
- [.NET 10 SDK](https://dotnet.microsoft.com/)（构建）；运行非自包含发布时需要 .NET 10 Desktop Runtime
- `ffmpeg.exe`（**客户端必需**）：需包含 `h264_nvenc` / `h264_qsv` / `h264_amf` 任一硬件编码器或 `libx264`，以及 `d3d11va` 硬解支持（官方 gpl 构建即可）。放在客户端目录、`tools\` 子目录或 `PATH` 中均可
- 被控端需要已登录的桌面会话（无人登录时 DXGI 采集不可用，会回退 GDI；后台服务可在用户登录后自动拉起客户端）

## 构建

版本号来自根目录 `VERSION`（同时写入程序集版本与产物文件名），也可用 `-Version` 覆盖。

```powershell
dotnet build TjViewer.slnx

# 自包含发布（无需目标机安装 .NET），产物文件名带版本号：
#   publish/server-linux-x64  → dist/tjviewer-server-linux-x64-v1.0.6.tar.gz
#   publish/server-win-x64    → dist/tjviewer-server-win-x64-v1.0.6.zip（含 start-server.cmd）
#   publish/client-win-x64    → dist/tjviewer-client-win-x64-v1.0.6.zip
#                               （含 ffmpeg、service/、安装/卸载脚本、证书）
pwsh -File build.ps1

# 带签名构建（默认自动使用 dist/signing 下的证书）
pwsh -File scripts/new-signing-cert.ps1
pwsh -File build.ps1 -SignPfx dist/signing/tjviewer-signing.pfx -SignPassword ***
```

## 部署到 Windows 服务器

Windows 服务器包同样是自包含发布，目标机无需安装 .NET：

1. 解压 `tjviewer-server-win-x64-v1.0.6.zip` 到任意目录
2. 双击 `start-server.cmd`（默认 5000 端口，也可执行 `start-server.cmd 8080`）

脚本会以 `0.0.0.0:5000` 启动服务；数据库 `tjviewer.db` 与首次启动生成的管理员口令 `admin.credentials` 都在解压目录。管理后台地址 `http://127.0.0.1:5000/admin.html`。局域网内其他电脑访问时，需在 Windows 防火墙放行 TCP 5000。

## 部署到 Ubuntu 服务器

服务器包为自包含发布，目标机无需安装 .NET；需要 x86_64 与 systemd。

**一行安装（自动选择线路）**：优先直连 GitHub；直连失败时会自动改用公共加速镜像（`gh-proxy.com` / `ghproxy.net` / `ghfast.top`）下载安装包，无需手动配置代理：

```bash
rm -f /tmp/tjviewer-install.sh; U=https://raw.githubusercontent.com/laolu0422/tjviewer/main/scripts/install-server.sh; curl -fsSL "$U" -o /tmp/tjviewer-install.sh || curl -fsSL "https://gh-proxy.com/$U" -o /tmp/tjviewer-install.sh; sudo bash /tmp/tjviewer-install.sh
```

> 加速镜像为第三方服务，仅用于无法直连 GitHub 的网络环境；对来源校验要求较高时请使用下方离线安装。

安装脚本会：解压到 `/opt/tjviewer`、数据放 `/var/lib/tjviewer`、生成随机管理员口令与 JWT 密钥、写入 `tjviewer.service` 并开机自启、安装 `tjviewer` 管理命令。默认监听 `0.0.0.0:5000`，安装结束会打印访问地址与管理员口令（也可用 `sudo tjviewer config` 查看）。

**一行启动 / 管理**：

```bash
sudo tjviewer start        # 启动（安装时已自动启动并设为开机自启）
sudo tjviewer status       # 查看状态
sudo tjviewer logs         # 实时日志
sudo tjviewer restart      # 重启
```

**离线安装**（服务器无法访问 GitHub）：把 `dist/` 下的 `install-server.sh` 与 `tjviewer-server-linux-x64-v1.0.6.tar.gz` 一起上传后执行：

```bash
sudo bash install-server.sh --file /tmp/tjviewer-server-linux-x64-v1.0.6.tar.gz
```

可选参数：`--port 5000` 指定端口、`--uninstall` 卸载（保留数据）、`TJVIEWER_PUBLIC_URL` 指定对外地址。若启用 ufw，脚本会自动放行端口；其他防火墙请手动放行 TCP 5000。

> 公网部署建议在 5000 端口前加 Nginx/Caddy 反向代理并启用 HTTPS（客户端服务器地址填 `https://域名`）。

## Windows 客户端安装（Program Files + 后台服务）

客户端发行包（`tjviewer-client-win-x64-v1.0.6.zip`）已包含 `TjViewer.exe`、`service\TjViewer.Service.exe`、`ffmpeg.exe`、安装/卸载脚本与签名证书。解压后以**管理员权限**运行：

```powershell
# 交互安装（复制到 %ProgramFiles%\TjViewer，注册并启动服务）
powershell -ExecutionPolicy Bypass -File .\install-client.ps1

# 静默 / 批量部署（SCCM、Intune、域脚本）
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-client.ps1 -Silent -NoShortcut
```

安装器会完成：

| 项目 | 说明 |
| --- | --- |
| 安装目录 | `%ProgramFiles%\TjViewer`，同时写入“应用和功能”卸载项 |
| 后台服务 | `TjViewerService`：`LocalSystem` 身份、`start=auto`、崩溃自动重启；无用户登录也持续运行，用户登录后自动在会话中拉起客户端（`--minimized`） |
| 调度优先级 | `IFEO\TjViewer.exe\PerfOptions`：`CpuPriorityClass=3`（High）、`IoPriority=3`；客户端启动时再次兜底提升，ffmpeg 子进程继承 |
| 防火墙 | 按程序放行入站 TCP/UDP（P2P 打洞必需），不开放固定端口 |
| 证书信任 | 将随包 `tjviewer-signing.cer` 导入本机“受信任的发布者”和“受信任的根证书颁发机构” |
| 其他 | 开始菜单快捷方式、`%ProgramData%\TjViewer\logs` 日志目录 |

常用参数：`-NoService`（不装服务）、`-NoFirewall`、`-NoCertTrust`（证书由域策略统一管理时）、`-NoShortcut`、`-Silent`。安装日志：`%ProgramData%\TjViewer\logs\install.log`。

卸载：

```powershell
powershell -ExecutionPolicy Bypass -File "%ProgramFiles%\TjViewer\uninstall-client.ps1"
```

> 能力边界：Windows 在无人登录/锁屏时使用独立的安全桌面（Winlogon），普通进程无法采集该桌面。本服务保证“无需登录即可运行 + 登录后立即接管 + 异常自动恢复”，**不等同于**锁屏前画面采集；后者需单独部署带 `uiAccess` 的签名 SecureAgent。Ctrl+Alt+Del 也无法通过普通输入注入模拟。

## 代码签名

```powershell
# 1) 生成自签名代码签名证书（写入 dist/signing，dist 已被 gitignore）
pwsh -File scripts/new-signing-cert.ps1

# 2) 对已发布目录签名（也可由 build.ps1 自动调用）
pwsh -File scripts/sign.ps1 -Path publish/client-win-x64
pwsh -File scripts/sign.ps1 -Path publish/client-win-x64 -Pfx cert.pfx -Password ***
```

自签名证书只有在目标机信任后签名才显示“有效”——安装器会自动导入随包 `.cer`。对外分发建议使用 CA 签发的代码签名证书：CI 配置 `SIGNING_PFX_BASE64`、`SIGNING_PFX_PASSWORD` 两个 Secrets 后，Release 工作流会自动签名并附带 `.cer`。

## 运行

**服务端**

```powershell
dotnet run --project src/TjViewer.Server
# 默认监听 http://localhost:5000，健康检查 /api/health
```

首次启动会自动创建管理员账号：优先读取配置 `Admin:Username` / `Admin:Password`；未配置密码时随机生成强口令并写入程序目录下的 `admin.credentials`（已 gitignore）。

**客户端**

```powershell
dotnet run --project src/TjViewer.Client
```

登录后进入设备列表，选中设备点击「连接选中设备」打开远程控制窗口。窗口内可切换画质、开启麦克风回传、发送文件。

**管理后台**

浏览器打开 `http://<服务器地址>:5000/admin.html`，使用管理员账号登录。

## 配置（`src/TjViewer.Server/appsettings.json`）

| 配置项 | 说明 |
| --- | --- |
| `ConnectionStrings:Default` | SQLite 连接串，默认 `Data Source=tjviewer.db` |
| `Urls` | 服务端监听地址，默认 `http://localhost:5000` |
| `PublicBaseUrl` | 对外访问地址，用于拼装找回密码邮件中的链接 |
| `Smtp:Host/Port/User/Password/From` | 找回密码邮件所用的 SMTP 服务 |
| `Cors:Origins` | 管理后台允许的来源列表（留空则不启用跨域） |
| `Jwt:Key` | JWT 签名密钥（Base64，32 字节以上）。未配置时自动生成并保存到 `jwt.key` |
| `Admin:Username/Password/Email` | 首个管理员账号（仅首次建库时生效） |

> 生产部署建议：配置固定的 `Jwt:Key` 与 SMTP 凭据，服务置于 HTTPS 反向代理之后，并限制管理后台访问来源。

## 画质档位

| 档位 | 分辨率 | 最大帧率 | 最大码率 |
| --- | --- | --- | --- |
| 高 | 1920×1080 | 30 | 4000 kbps |
| 中 | 1280×720 | 25 | 1500 kbps |
| 低 | 854×480 | 20 | 600 kbps |
| 极低 | 640×360 | 15 | 300 kbps |

自适应模式下，发送队列积压持续满 1 秒才降一档；连续 3 秒空闲才升一档，避免画质抖动。手动模式使用滑块独立设置帧率（15/30/40/60/90/120）与码率（2/4/6/8/10 Mbps），「锁定」可强制固定当前组合。服务端通过 `/api/limits` 下发硬限制（4K/120fps/10Mbps），中继路径会直接拦截超限参数。

## 陌生设备连接

被控端登录后，主界面右侧会显示两组凭证（由服务端统一分配，避免不同用户连接码冲突）：

- **观看**：9 位连接码 + 8 位验证码（仅画面与声音，不允许操作）
- **远程控制**：独立的 9 位连接码 + 8 位验证码（完整键鼠与文件能力）

控制端点击「输入连接码连接」，输入对方的连接码与验证码并选择模式即可。验证码在每次连接成功后立即轮换并推送到被控端；控制端可记住上次的连接码，但绝不会保存验证码。被控端可勾选「使用固定连接码」，重启后连接码保持不变。

## 硬件编解码

启动时按 **`h264_nvenc`（NVIDIA）→ `h264_qsv`（Intel）→ `h264_amf`（AMD）→ `libx264`** 顺序选择编码器，解码优先 `d3d11va`，最终都会回退到软件路径。

- 判定不是简单地读取 `ffmpeg -encoders`，而是用 0.2 秒真实码流跑一次编码初始化 / 硬件解码，确认真正可用后才会启用；结果按 ffmpeg 路径缓存，切换画质档重建编码器时不会重复探测。
- 应急开关（进程环境变量）：
  - `TJVIEWER_ENCODER=libx264`：强制软件编码
  - `TJVIEWER_HWACCEL=none`：强制软件解码
- 诊断：`FFmpegLocator.Diagnostics` 事件会输出实际选用的编码器、解码方式与探测失败原因。

## 低延迟与流畅度优化

以“操作跟手、画面不积压”为目标，链路各环节都做了针对性调优：

- **KCP**：`NoDelay(1, 5, 2, 1)`（5ms 快节拍、快速重传、关闭拥塞控制）+ 512 段收发窗口，避免 10Mbps 高码率下窗口不足；UDP 收发缓冲提升到 4MB，降低突发丢包。
- **采集**：DXGI 仅等待 2–4ms，无桌面更新时复用上一帧；发送端按设定帧率精确节拍、静止画面持续推流，保证任意时刻操作都能立即反馈。
- **编码**：NVENC `-preset p1 -tune ull -delay 0`、QSV `-async_depth 1`、AMF `ultralowlatency`，统一 `-flags low_delay`、无 B 帧、`-flush_packets 1`，缩短编码器内部排队。
- **解码**：`-flags low_delay -fflags nobuffer -analyzeduration 0`；软件解码使用 slice 线程，避免 frame 线程引入整帧时延。
- **系统调度**：客户端启动即 `timeBeginPeriod(1)` 提升时钟精度（默认 15.6ms 粒度会直接抬高排队时延），进程优先级 High（安装器 IFEO + 运行时兜底，ffmpeg 继承）。
- **渲染**：控制端渲染忙时只保留最新帧丢弃旧帧，120fps 下不会积压 WPF UI 线程。

## 安全设计

- 密码使用 Argon2id 哈希；刷新令牌、找回密码令牌只保存 SHA256 摘要，且一次性使用。
- 鉴权接口按 IP 限流（20 次/分钟），业务接口按用户限流（120 次/分钟）。
- EF Core 参数化查询，无字符串拼接 SQL；统一安全响应头（`nosniff`、`X-Frame-Options: DENY`、`no-referrer`）。
- 中转文件在内存中 AES-GCM 加密暂存，不落明文磁盘。
- 运行时秘密（`*.db`、`jwt.key`、`admin.credentials`）已在 `.gitignore` 中排除。

## 已知限制

- 仅支持 Windows 作为被控端与控制端；服务端可跨平台运行。
- 对称型 NAT 下 UDP 打洞可能失败，此时自动回退服务器中转（音视频/输入/文件均可用，延迟与带宽受服务器限制）。
- 需要自行提供 `ffmpeg.exe`，未直接打包进仓库（官方发行包已自动捆绑）。
- 无人登录/锁屏时 Windows 使用独立安全桌面，普通应用无法采集与操作；后台服务只保证进程常驻与登录后自动接管。Ctrl+Alt+Del 无法通过普通输入注入模拟。
- 自签名代码签名证书只在导入了该证书的机器上显示为“有效”；对外分发请使用 CA 签发的证书。

## 许可

未指定许可证，仅供内部使用。
