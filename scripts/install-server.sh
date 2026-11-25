#!/usr/bin/env bash
# TjViewer 服务器一键安装（Ubuntu / Debian，x86_64）
#
# 默认从 GitHub Release 下载自包含包并安装为 systemd 服务：
#   curl -fsSL https://raw.githubusercontent.com/laolu0422/tjviewer/main/scripts/install-server.sh | sudo bash
#
# 可选参数 / 环境变量：
#   --file <tar.gz>       使用本地安装包（离线安装）
#   --port <端口>         监听端口，默认 5000
#   --uninstall           卸载服务与程序（保留 /var/lib/tjviewer 数据）
#   TJVIEWER_VERSION      指定版本标签，默认 latest
#   TJVIEWER_PUBLIC_URL   对外访问地址，默认自动取本机第一个 IPv4
set -euo pipefail

REPO="laolu0422/tjviewer"
ASSET="tjviewer-server-linux-x64.tar.gz"
INSTALL_DIR="/opt/tjviewer"
DATA_DIR="/var/lib/tjviewer"
CRED_FILE="/etc/tjviewer/admin.credentials"
SERVICE="tjviewer"
RUN_USER="tjviewer"
PORT="${TJVIEWER_PORT:-5000}"
VERSION="${TJVIEWER_VERSION:-latest}"
PROXY="${TJVIEWER_PROXY:-}"
PKG_FILE=""

log() { printf '\033[35m[TjViewer]\033[0m %s\n' "$*"; }
die() { printf '\033[31m[TjViewer] %s\033[0m\n' "$*" >&2; exit 1; }
usage() {
    cat <<'USAGE'
用法: install-server.sh [选项]
  --file <tar.gz>       使用本地安装包（离线安装）
  --port <端口>         监听端口，默认 5000
  --proxy <url>         下载安装包走代理，如 http://127.0.0.1:7890 或 socks5://127.0.0.1:1080
  --uninstall           卸载服务与程序（保留 /var/lib/tjviewer 数据）
环境变量:
  TJVIEWER_VERSION      指定 Release 标签，默认 latest
  TJVIEWER_PUBLIC_URL   对外访问地址，默认自动取本机第一个 IPv4
  TJVIEWER_PROXY        同 --proxy
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --file) PKG_FILE="${2:-}"; shift 2 ;;
        --port) PORT="${2:-}"; shift 2 ;;
        --proxy) PROXY="${2:-}"; shift 2 ;;
        --uninstall) UNINSTALL=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "未知参数：$1" ;;
    esac
done

[ "$(id -u)" -eq 0 ] || die "请使用 sudo 运行"
case "$(uname -m)" in
    x86_64|amd64) ;;
    *) die "仅支持 x86_64 架构（当前：$(uname -m)）" ;;
esac

if [ "${UNINSTALL:-0}" = "1" ]; then
    systemctl disable --now "$SERVICE" 2>/dev/null || true
    rm -f "/etc/systemd/system/$SERVICE.service" /usr/local/bin/tjviewer
    systemctl daemon-reload 2>/dev/null || true
    rm -rf "$INSTALL_DIR"
    log "已卸载；数据库与配置保留在 $DATA_DIR"
    exit 0
fi

# ---- 获取安装包 ----
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
if [ -n "$PKG_FILE" ]; then
    [ -f "$PKG_FILE" ] || die "安装包不存在：$PKG_FILE"
    SRC="$PKG_FILE"
    log "使用本地安装包：$SRC"
else
    if [ "$VERSION" = "latest" ]; then
        URL="https://github.com/$REPO/releases/latest/download/$ASSET"
    else
        URL="https://github.com/$REPO/releases/download/$VERSION/$ASSET"
    fi
    SRC="$TMP_DIR/$ASSET"
    CURL_OPTS=(-fL --retry 3 --connect-timeout 15 --speed-time 60 --speed-limit 1024)
    if [ -n "$PROXY" ]; then
        CURL_OPTS+=(--proxy "$PROXY")
        log "使用代理：$PROXY"
    fi
    log "下载 $URL"
    curl "${CURL_OPTS[@]}" -o "$SRC" "$URL" || die "下载失败（国内网络可加 --proxy http://127.0.0.1:7890，或改用 --file 离线安装）"
fi

# ---- 运行依赖：.NET 自包含包仍需系统 ICU ----
if ! ldconfig -p 2>/dev/null | grep -q 'libicu'; then
    log "安装 libicu（.NET 全球化依赖）"
    if command -v apt-get >/dev/null 2>&1; then
        apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq libicu-dev >/dev/null || die "libicu 安装失败"
    else
        die "缺少 libicu，请先安装（如 libicu-dev）"
    fi
fi

# ---- 用户与目录 ----
if ! id -u "$RUN_USER" >/dev/null 2>&1; then
    useradd --system --user-group --home-dir "$DATA_DIR" --shell /usr/sbin/nologin "$RUN_USER"
fi
mkdir -p "$INSTALL_DIR" "$DATA_DIR" "$(dirname "$CRED_FILE")"
log "解压到 $INSTALL_DIR"
rm -rf "${INSTALL_DIR:?}"/*
tar -xzf "$SRC" -C "$INSTALL_DIR"
# Windows 打出的 tar 不保留 Unix 权限，这里统一补上可读/可执行
chown -R root:root "$INSTALL_DIR"
chmod -R a+rX "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/TjViewer.Server"

# ---- 生成配置（管理员口令、JWT 密钥仅本机保存）----
LOCAL_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
[ -n "$LOCAL_IP" ] || LOCAL_IP="127.0.0.1"
PUBLIC_URL="${TJVIEWER_PUBLIC_URL:-http://$LOCAL_IP:$PORT}"
JWT_KEY="$(head -c 32 /dev/urandom | base64 | tr -d '\n')"
ADMIN_PASSWORD="$(head -c 18 /dev/urandom | base64 | tr -d '\n')"
cat > "$INSTALL_DIR/appsettings.Production.json" <<EOF
{
  "Urls": "http://0.0.0.0:$PORT",
  "PublicBaseUrl": "$PUBLIC_URL",
  "ConnectionStrings": { "Default": "Data Source=$DATA_DIR/tjviewer.db" },
  "Admin": {
    "Username": "admin",
    "Password": "$ADMIN_PASSWORD",
    "Email": "admin@tjviewer.local"
  }
}
EOF
chown root:"$RUN_USER" "$INSTALL_DIR/appsettings.Production.json"
chmod 640 "$INSTALL_DIR/appsettings.Production.json"
printf 'username=admin\npassword=%s\n' "$ADMIN_PASSWORD" > "$CRED_FILE"
chmod 600 "$CRED_FILE"
chown -R "$RUN_USER":"$RUN_USER" "$DATA_DIR"

# ---- systemd 服务 ----
cat > "/etc/systemd/system/$SERVICE.service" <<EOF
[Unit]
Description=TjViewer Server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$RUN_USER
Group=$RUN_USER
WorkingDirectory=$INSTALL_DIR
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=Jwt__Key=$JWT_KEY
Environment=Urls=http://0.0.0.0:$PORT
ExecStart=$INSTALL_DIR/TjViewer.Server
Restart=always
RestartSec=3
LimitNOFILE=65535
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF

# ---- 管理命令 ----
cat > /usr/local/bin/tjviewer <<'EOF'
#!/usr/bin/env bash
set -e
cmd="${1:-status}"
case "$cmd" in
    start|stop|restart|status) systemctl "$cmd" tjviewer ;;
    logs) journalctl -u tjviewer -f --no-pager ;;
    config) cat /etc/tjviewer/admin.credentials ;;
    *) echo "用法: tjviewer {start|stop|restart|status|logs|config}" ;;
esac
EOF
chmod +x /usr/local/bin/tjviewer

systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null 2>&1
systemctl restart "$SERVICE"
sleep 2
if ! systemctl is-active --quiet "$SERVICE"; then
    journalctl -u "$SERVICE" -n 30 --no-pager || true
    die "服务启动失败，请检查上方日志"
fi

log "安装完成，服务已启动"
log "访问地址：$PUBLIC_URL"
log "管理后台：$PUBLIC_URL/admin.html"
log "管理员账号：admin"
log "管理员口令：$ADMIN_PASSWORD（也保存在 $CRED_FILE，可用 tjviewer config 查看）"
log "常用命令：sudo tjviewer start|stop|restart|status|logs"
log "注意：如启用防火墙，请放行 TCP $PORT"
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    log "检测到 ufw 已启用，正在放行 TCP $PORT"
    ufw allow "$PORT/tcp" >/dev/null || true
fi
