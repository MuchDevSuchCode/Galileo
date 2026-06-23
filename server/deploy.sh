#!/usr/bin/env bash
#
# deploy.sh — provision the Galileo relay on a fresh AWS Lightsail (Ubuntu 22.04/24.04) instance.
#
# Installs the relay as a systemd service behind an nginx reverse proxy that terminates TLS and
# upgrades the WebSocket. Idempotent: safe to re-run to pick up code or config changes.
#
# Usage (run on the Lightsail instance, from the repo's server/ directory):
#     sudo DOMAIN=relay.example.com EMAIL=you@example.com ./deploy.sh
#
#   DOMAIN  (required for TLS) public DNS name pointed at this instance's static IP.
#   EMAIL   (required for TLS) contact for Let's Encrypt / certbot.
#   If DOMAIN is omitted, nginx is configured for plain HTTP on port 80 (no TLS) — fine for a quick
#   test, but run with a DOMAIN for production so traffic (and which IDs talk) is encrypted in transit.
#
# Before running: in the Lightsail console open the instance firewall for TCP 80 and 443 (and, if you
# want, 22 for SSH). Point an A record for $DOMAIN at the instance's static IP first so certbot can
# validate.
#
set -euo pipefail

# --- config ---------------------------------------------------------------
APP_USER="galileo"
APP_DIR="/opt/galileo-relay"
SERVICE="galileo-relay"
RELAY_PORT="8765"                 # loopback-only; nginx is the public face
DOMAIN="${DOMAIN:-}"
EMAIL="${EMAIL:-}"
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"   # this server/ directory

if [[ $EUID -ne 0 ]]; then
  echo "Please run as root (sudo $0)." >&2
  exit 1
fi

echo "==> Installing system packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y python3 python3-venv python3-pip nginx
if [[ -n "$DOMAIN" ]]; then
  apt-get install -y certbot python3-certbot-nginx
fi

echo "==> Creating service user and app directory"
id -u "$APP_USER" &>/dev/null || useradd --system --home "$APP_DIR" --shell /usr/sbin/nologin "$APP_USER"
mkdir -p "$APP_DIR"

echo "==> Copying relay code to $APP_DIR"
install -m 0644 "$SRC_DIR/relay.py" "$APP_DIR/relay.py"
install -m 0644 "$SRC_DIR/requirements.txt" "$APP_DIR/requirements.txt"

echo "==> Creating Python virtualenv and installing dependencies"
python3 -m venv "$APP_DIR/.venv"
"$APP_DIR/.venv/bin/pip" install --upgrade pip
"$APP_DIR/.venv/bin/pip" install -r "$APP_DIR/requirements.txt"

# The relay's SQLite DB (peers + audit log) lives in the app dir; the service user owns it.
chown -R "$APP_USER:$APP_USER" "$APP_DIR"

echo "==> Writing systemd unit /etc/systemd/system/$SERVICE.service"
cat > "/etc/systemd/system/$SERVICE.service" <<UNIT
[Unit]
Description=Galileo secure-sharing relay
After=network.target

[Service]
Type=simple
User=$APP_USER
WorkingDirectory=$APP_DIR
Environment=GALILEO_RELAY_PORT=$RELAY_PORT
Environment=GALILEO_RELAY_DB=$APP_DIR/relay.db
# Bind to loopback only — nginx terminates TLS and proxies in.
ExecStart=$APP_DIR/.venv/bin/uvicorn relay:app --host 127.0.0.1 --port $RELAY_PORT
Restart=on-failure
RestartSec=2
# Hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$APP_DIR

[Install]
WantedBy=multi-user.target
UNIT

echo "==> Starting the relay service"
systemctl daemon-reload
systemctl enable "$SERVICE"
systemctl restart "$SERVICE"

# --- nginx reverse proxy --------------------------------------------------
# WebSocket needs the Upgrade/Connection headers passed through; /connect is the WS endpoint, the rest
# are plain HTTP (register/lookup/audit/healthz).
SERVER_NAME="${DOMAIN:-_}"
echo "==> Writing nginx site (server_name: $SERVER_NAME)"
cat > "/etc/nginx/sites-available/$SERVICE" <<NGINX
map \$http_upgrade \$connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    listen [::]:80;
    server_name $SERVER_NAME;

    # Allow large audit responses / streamed envelopes; the payloads are chunked but be generous.
    client_max_body_size 64m;

    location / {
        proxy_pass http://127.0.0.1:$RELAY_PORT;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection \$connection_upgrade;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        # Keep relayed WebSocket sessions alive (peers may idle between requests).
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }
}
NGINX

ln -sf "/etc/nginx/sites-available/$SERVICE" "/etc/nginx/sites-enabled/$SERVICE"
rm -f /etc/nginx/sites-enabled/default
nginx -t
systemctl restart nginx

# --- TLS via certbot ------------------------------------------------------
if [[ -n "$DOMAIN" ]]; then
  if [[ -z "$EMAIL" ]]; then
    echo "DOMAIN set but EMAIL missing — skipping TLS. Re-run with EMAIL=you@example.com to enable HTTPS." >&2
  else
    echo "==> Obtaining/renewing Let's Encrypt certificate for $DOMAIN"
    # certbot edits the nginx site to add the 443 server block + redirect, and installs a renewal timer.
    certbot --nginx -d "$DOMAIN" --non-interactive --agree-tos -m "$EMAIL" --redirect
  fi
fi

# --- summary --------------------------------------------------------------
echo
echo "==> Done."
systemctl --no-pager --full status "$SERVICE" | sed -n '1,5p' || true
echo
if [[ -n "$DOMAIN" && -n "$EMAIL" ]]; then
  echo "Relay URL for Galileo (Settings -> Secure sharing -> Relay server):"
  echo "    wss://$DOMAIN"
  echo "Health check: curl https://$DOMAIN/healthz"
else
  IP="$(curl -s --max-time 5 http://checkip.amazonaws.com || echo '<instance-ip>')"
  echo "Relay URL for Galileo (no TLS — testing only):"
  echo "    ws://$IP"
  echo "Health check: curl http://$IP/healthz"
  echo "For production, re-run with DOMAIN and EMAIL set to enable wss:// (TLS)."
fi
echo
echo "Logs:    journalctl -u $SERVICE -f"
echo "Restart: systemctl restart $SERVICE"
