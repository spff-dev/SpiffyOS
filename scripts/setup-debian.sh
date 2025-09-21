#!/usr/bin/env bash
set -euo pipefail
sudo apt-get update
sudo apt-get install -y wget apt-transport-https software-properties-common
wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
sudo dpkg -i /tmp/packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0
sudo useradd -r -s /usr/sbin/nologin spiffybot || true
sudo mkdir -p /opt/spiffyOS && sudo chown -R $USER: /opt/spiffyOS
