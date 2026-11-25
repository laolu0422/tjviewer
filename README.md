# TjViewer 远程控制

轻量级 Windows 远程控制软件（服务端 + 客户端），面向小范围、低带宽的使用场景：服务器只有约 10M 上下行带宽时，依然可以低延迟观看桌面、远程键鼠操控、双向音频与文件传输。

## 功能特性

- **远程桌面观看**：DXGI Desktop Duplication 采集（含光标合成，失败自动回退 GDI）→ H.264 编码 → KCP/UDP 通道；支持 NVIDIA NVENC、Intel QSV、AMD AMF 硬件编码与 D3D11VA 硬件解码，探测失败自动回退 libx264 / 软件解码。
- **画质调节**：自适应模式按 UDP 发送队列拥塞升降档（降档快、升档慢），也可手动固定档位。
- **连接方式**：STUN 打洞建立 P2P 直连，UDP 上跑 KCP（低延迟、抗丢包）；单条通道按首字节复用视频、输入、音频、文件、控制消息。
- **远程键鼠**：键盘扫描码注入（支持扩展键）、绝对坐标鼠标、按键、滚轮、文本输入。
- **双向音频**：被控端系统声音回传（WASAPI Loopback + Opus），控制端麦克风可下发至被控端（需用户主动开启）。
- **文件传输**：P2P 分块传输 + SHA256 校验 + 进度回调，保存到 `下载/TjViewer`；另提供服务器小文件中转（AES-GCM 内存暂存、单文件 ≤ 50MB、10 分钟过期、须鉴权）。
- **账户与设备**：邮箱必填注册；用户名或邮箱 + 密码登录；Argon2id 密码哈希；JWT 访问令牌 + 刷新令牌（仅存哈希）；自有 SMTP 发送找回密码邮件。
- **网页管理后台**：用户启用/禁用/删除/重置密码，查看设备、会话、审计日志与系统配置。
- **客户端易用性**：设备列表、一键连接、开机自启（HKCU Run，用户确认后写入）。

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

- **服务端**：ASP.NET Core Minimal API + EF Core + SQLite，数据落在单个 `tjviewer.db` 文件，首次启动自动建库。
- **采集与编解码**：Vortice（DXGI / Direct3D 11）采集，ffmpeg 子进程经标准输入输出管道处理 BGRA 原始帧。
- **音频**：NAudio（WASAPI）+ Concentus（Opus 编解码）。

## 目录结构

```
src/
  TjViewer.Server/    服务端：鉴权、设备、管理后台、信令、文件中转
  TjViewer.Client/    WPF 客户端：登录、设备列表、远程控制窗口、托盘/自启
  TjViewer.Core/      采集、编解码、KCP、网络、音频、键鼠注入、文件传输
  TjViewer.Shared/    客户端与服务端共用的协议、DTO、路由与画质档位
build.ps1             发布脚本（发布 server/client 并捆绑 ffmpeg）
TjViewer.slnx         解决方案
```

## 环境要求

- Windows 10 1809+ / Windows 11，x64
- [.NET 10 SDK](https://dotnet.microsoft.com/)（构建）；运行非自包含发布时需要 .NET 10 Desktop Runtime
- `ffmpeg.exe`（**客户端必需**）：需包含 `h264_nvenc` / `h264_qsv` / `h264_amf` 任一硬件编码器或 `libx264`，以及 `d3d11va` 硬解支持（官方 gpl 构建即可）。放在客户端目录、`tools\` 子目录或 `PATH` 中均可
- 被控端需要已登录的桌面会话（RDP 或无人登录环境下 DXGI 采集可能不可用，会回退 GDI）

## 构建

```powershell
dotnet build TjViewer.slnx

# 发布到 publish/server 与 publish/client，并自动从 ~/.local/share/ffmpeg 或 PATH 捆绑 ffmpeg
pwsh -File build.ps1
```

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

自适应模式下，发送队列积压超过阈值立即降档；连续 3 秒空闲才升一档，避免画质抖动。

## 硬件编解码

启动时按 **`h264_nvenc`（NVIDIA）→ `h264_qsv`（Intel）→ `h264_amf`（AMD）→ `libx264`** 顺序选择编码器，解码优先 `d3d11va`，最终都会回退到软件路径。

- 判定不是简单地读取 `ffmpeg -encoders`，而是用 0.2 秒真实码流跑一次编码初始化 / 硬件解码，确认真正可用后才会启用；结果按 ffmpeg 路径缓存，切换画质档重建编码器时不会重复探测。
- 应急开关（进程环境变量）：
  - `TJVIEWER_ENCODER=libx264`：强制软件编码
  - `TJVIEWER_HWACCEL=none`：强制软件解码
- 诊断：`FFmpegLocator.Diagnostics` 事件会输出实际选用的编码器、解码方式与探测失败原因。

## 安全设计

- 密码使用 Argon2id 哈希；刷新令牌、找回密码令牌只保存 SHA256 摘要，且一次性使用。
- 鉴权接口按 IP 限流（20 次/分钟），业务接口按用户限流（120 次/分钟）。
- EF Core 参数化查询，无字符串拼接 SQL；统一安全响应头（`nosniff`、`X-Frame-Options: DENY`、`no-referrer`）。
- 中转文件在内存中 AES-GCM 加密暂存，不落明文磁盘。
- 运行时秘密（`*.db`、`jwt.key`、`admin.credentials`）已在 `.gitignore` 中排除。

## 已知限制

- 仅支持 Windows 作为被控端与控制端；服务端可跨平台运行。
- 对称型 NAT 下 UDP 打洞可能失败，此时实时音视频无法建立（服务器中转目前仅用于文件）。
- 需要自行提供 `ffmpeg.exe`，未直接打包进仓库。

## 许可

未指定许可证，仅供内部使用。
