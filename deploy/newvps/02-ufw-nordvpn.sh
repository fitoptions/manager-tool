#!/usr/bin/env bash
# NordVPN admin-lock for SSH. Run as root on the VPS.
#
# =========================  READ BEFORE RUNNING  =========================
# This REMOVES open-to-the-world SSH and only allows SSH from your NordVPN
# dedicated IP. If NordVPN is down or the IP is wrong, you are LOCKED OUT of SSH.
#
# BEFORE running, confirm BOTH:
#   1. Hostinger browser-VNC / "Browser terminal" console works (your out-of-band
#      recovery path — test it now, log in as root there once).
#   2. You are CURRENTLY connected to your NordVPN dedicated IP, and NORD_IP below
#      is exactly that IP (NordVPN app -> Dedicated IP server -> shown IP).
# =========================================================================
set -euo pipefail

NORD_IP="NORD_IP"   # <-- replace with your NordVPN DEDICATED IP

if [ "$NORD_IP" = "NORD_IP" ]; then
  echo "ERROR: set NORD_IP first. Aborting."; exit 1
fi

echo "Current SSH source before change: ${SSH_CLIENT:-unknown}"
ufw allow 443/tcp
ufw allow from "$NORD_IP" to any port 22 proto tcp
ufw delete allow 22/tcp || true
ufw delete allow 22    || true
ufw --force enable
ufw status numbered
echo
echo "SSH is now restricted to $NORD_IP. Open a SECOND terminal (still on NordVPN)"
echo "and confirm 'ssh -i ~/.ssh/newvps root@<VPS_IP>' still works BEFORE closing this one."
