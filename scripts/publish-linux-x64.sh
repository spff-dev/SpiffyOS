#!/usr/bin/env bash
set -euo pipefail
rm -rf publish && mkdir -p publish

dotnet publish src/SpiffyOS.Bot/SpiffyOS.Bot.csproj -c Release -r linux-x64 --self-contained false -o publish/bot
dotnet publish src/SpiffyOS.Overlay/SpiffyOS.Overlay.csproj -c Release -r linux-x64 --self-contained false -o publish/overlay
