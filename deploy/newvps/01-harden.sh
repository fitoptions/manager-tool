#!/usr/bin/env bash
# Phase 4 + prerequisites. Run as root on the fresh Ubuntu 24.04 VPS.
# This is safe (does NOT touch SSH access). The NordVPN SSH lock is a SEPARATE,
# later script (02-ufw-nordvpn.sh) with its own safety checklist.
set -euo pipefail

echo "== apt update/upgrade =="
apt update && apt upgrade -y

echo "== base packages =="
apt install -y ufw nginx certbot

echo "== .NET 8 ASP.NET runtime (required: app is framework-dependent) =="
if ! apt install -y aspnetcore-runtime-8.0 2>/dev/null; then
  echo "  adding Microsoft package feed..."
  wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/pmp.deb
  dpkg -i /tmp/pmp.deb
  apt update
  apt install -y aspnetcore-runtime-8.0
fi
dotnet --info | head -5 || true

echo "== firewall (open 443; keep 22 open for now — locked to NordVPN later) =="
ufw allow 22/tcp
ufw allow 443/tcp
# Optional: open 80 only if you want the http->https redirect / http-01 challenge.
# ufw allow 80/tcp
ufw --force enable
ufw status verbose

echo "== app directories =="
mkdir -p /opt/app/app /opt/app/data /opt/app/recordings /opt/app/app/agent-installer
echo "Done. Next: scp the build + configs (RUNBOOK step 5), then set up TLS (step 6)."
