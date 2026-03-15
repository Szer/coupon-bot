#!/usr/bin/env bash
set -euo pipefail

sudo apt-get update && sudo apt-get install -y wireguard resolvconf

echo "$WIREGUARD_CONFIG" | sudo tee /etc/wireguard/wg0.conf > /dev/null
sudo chmod 600 /etc/wireguard/wg0.conf

# Split tunneling: only route internal AKS traffic through VPN.
# Without this, AllowedIPs = 0.0.0.0/0 routes ALL traffic (including
# api.github.com) through the VPN tunnel, which times out.
# Set SPLIT_TUNNEL=false for jobs that need full tunnel (e.g. DB access
# via public endpoint + AKS firewall rule).
SPLIT_TUNNEL="${SPLIT_TUNNEL:-true}"
if [ "$SPLIT_TUNNEL" = "true" ]; then
  ALLOWED="10.0.0.0/8"
  if [ -n "${VPN_EXTRA_CIDRS:-}" ]; then
    ALLOWED="${ALLOWED}, ${VPN_EXTRA_CIDRS}"
  fi
  sudo sed -i "s|AllowedIPs\s*=\s*0\.0\.0\.0/0.*|AllowedIPs = ${ALLOWED}|" /etc/wireguard/wg0.conf
fi

sudo wg-quick up wg0

# When split-tunneling, add public DNS fallback so public hosts
# (api.github.com, etc.) resolve even though the VPN's internal
# DNS server is primary.  Not needed for full-tunnel mode where
# all traffic already routes through the VPN gateway.
if [ "$SPLIT_TUNNEL" = "true" ]; then
  # Append directly — resolvconf -a would trigger a full regeneration that
  # drops the runner's original system DNS (breaking apt/public resolution).
  # Direct append is safe in CI: the VPN connects once and nothing regenerates.
  echo "nameserver 8.8.8.8" | sudo tee -a /etc/resolv.conf > /dev/null
  echo "nameserver 8.8.4.4" | sudo tee -a /etc/resolv.conf > /dev/null
  echo "Added public DNS fallback (8.8.8.8, 8.8.4.4)"
fi

sudo wg show
echo "VPN connected (SPLIT_TUNNEL=${SPLIT_TUNNEL})"
