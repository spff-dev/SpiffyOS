# SpiffyOS (high-level)

![.NET 8](https://img.shields.io/badge/.NET-8-512BD4?style=for-the-badge&logo=dotnet)
![C#](https://img.shields.io/badge/C%23-11-512BD4?style=for-the-badge&logo=c-sharp&logoColor=white)
![Twitch](https://img.shields.io/badge/Twitch-API-9146FF?style=for-the-badge&logo=twitch&logoColor=white)
![Linux](https://img.shields.io/badge/Linux-FCC624?style=for-the-badge&logo=linux&logoColor=black)
![Version](https://img.shields.io/badge/version-v1.0.1-blue?style=for-the-badge)

This repo hosts **my Twitch bot** + a tiny overlay. It’s designed to be simple, config‑first, and safe to deploy on my VPS.

## What it does
- **Chat commands** via a router:
  - *Static* commands (e.g. `!discord`, `!specs`) – responses from config.
  - *Dynamic* commands – code handlers (e.g. `!uptime`, mod tools `!title`, `!game`, `!so`).
- **Mod tools**: `!title`, `!game` (+ alias `!category`), `!so` (Helix shoutout + optional chat announcement). Mutating forms are mod/broadcaster‑only.
- **Event announcer** (EventSub WebSocket): follows, subs/resubs, cheers (bits), raids, and channel point redemptions with simple cooldowns + templates.
- **Overlay**: minimal Kestrel app (token‑protected `/overlay`), optional.

## Folders (VPS layout)
- `/srv/bots/spiffyos/config/`
  - `commands.json` – all commands (static/dynamic) and options.
  - `events.json` – toggles & templates for announcer.
- `/srv/bots/spiffyos/secrets/` – `bot.json`, `broadcaster.json` token files.
- `/srv/bots/spiffyos/logs/` – daily rolling logs.
- `/srv/bots/spiffyos/publish/(bot|overlay)/` – published binaries from deploy.
- Source lives in `src/…` (standard .NET solution layout).

## Running (VPS)
- **Single source of truth**: `systemd` unit `/etc/systemd/system/spiffybot.service`.
  - Uses env: `SPIFFYOS_CONFIG`, `SPIFFYOS_TOKENS`, `SPIFFYOS_LOGS`.
  - Pre/Post kill guards prevent duplicate bot processes.
- **Deploy**: from `/srv/bots/spiffyos`
  ```bash
  git pull && ./deploy.sh
  # helper scripts
  scripts/health.sh
  scripts/logs.sh bot -n 200
  ```

## Local dev (Windows)
- Build: `dotnet build`
- Run bot: `dotnet run --project src/SpiffyOS.Bot`
- Config/logs/secrets default to local repo folders unless overridden by the env vars above.
- Required files:
  - `src/SpiffyOS.Bot/appsettings.json` → `Twitch.{ClientId, ClientSecret, BroadcasterId, BotUserId, ModeratorUserId}`
  - `secrets/bot.json` and `secrets/broadcaster.json` (token JSON the `TwitchAuth` class reads).

## Commands
- **Add / edit static commands**: edit `config/commands.json`.
  - Example item:
    ```json
    {
      "name": "discord",
      "type": "static",
      "data": { "text": "Join the Discord → https://discord.gg/yourInvite" },
      "permission": "everyone",
      "aliases": ["dc", "voicechat"],
      "replyToUser": false,
      "globalCooldown": 5,
      "userCooldown": 10
    }
    ```
- **Dynamic commands**: implement in `src/SpiffyOS.Core/Commands/` (handlers) and reference by `name` in `commands.json` with `"type": "dynamic"`.

## Events
- Configure follow/sub/cheer/raid/redemption messages in `config/events.json` (enable/disable, cooldowns, templates).

## Notes
- Uses EventSub **websocket**; Helix for chat, shoutouts, title/category updates, etc.
- Systemd unit: `spiffybot.service` is the only supported way to run in prod.
- Tags: `v1.0.x` represent stable baselines (e.g., **v1.0.1** after systemd cleanup).

