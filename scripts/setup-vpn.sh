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
  sudo sed -i 's|AllowedIPs\s*=\s*0\.0\.0\.0/0.*|AllowedIPs = 10.0.0.0/8|' /etc/wireguard/wg0.conf
fi

sudo wg-quick up wg0
sudo wg show
echo "VPN connected with split tunneling"
