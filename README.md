# SpiffyOS Twitch Bot

A modular, config-first Twitch bot and overlay for the **SPIFFgg** channel, built in **.NET 8** (C#).  
Runs locally for development on Windows 11 (VS Code) and in production on **Debian 12** at `/srv/bots/spiffyos`.

> Design goals: **Twitch-compliant**, **config over code**, **hot-reload**, and **clean separation**:
>
> - `SpiffyOS.Bot` — EventSub, command routing, timed announcements
> - `SpiffyOS.Core` — Helix/EventSub wrappers, command & event handlers, config providers
> - `SpiffyOS.Overlay` — lightweight OBS browser source (private, via NGINX path)

---

## Table of Contents

- [Features](#features)
- [Repo layout](#repo-layout)
- [Configuration](#configuration)
  - [Environment variables](#environment-variables)
  - [Twitch settings](#twitch-settings)
  - [Tokens & scopes](#tokens--scopes)
  - [`commands.json`](#commandsjson)
  - [`events.json`](#eventsjson)
  - [`announcements.json`](#announcementsjson)
- [How commands work](#how-commands-work)
  - [Static commands (no code)](#static-commands-no-code)
  - [Dynamic commands (with code)](#dynamic-commands-with-code)
  - [Adding a new dynamic command](#adding-a-new-dynamic-command)
- [Event handling (follows, subs, redemptions, bits, raids)](#event-handling-follows-subs-redemptions-bits-raids)
- [Overlay](#overlay)
- [Local development (Windows 11)](#local-development-windows-11)
- [Production (Debian 12)](#production-debian-12)
  - [Systemd services](#systemd-services)
  - [Deploy script](#deploy-script)
  - [Logs](#logs)
- [Security](#security)
- [Troubleshooting](#troubleshooting)

---

## Features

- **Commands**
  - Static (`!discord`, `!specs`) — reply text in config, no code changes
  - Dynamic (`!uptime`) — logic implemented in `SpiffyOS.Core.Commands`
  - Configurable: permissions, cooldowns, usage limits, aliases, reply-to threading
- **Mod tools (in progress)**
  - `!so <login>` (official shoutout API)
  - `!title <text>` / `!game <name|id>` (update broadcast)
- **Event announcements**
  - Follows (batchable), subs/resubs, channel point redemptions, bits (cheers), raids
  - Rate limiting + per-event cooldowns; message templates in config
- **Timed announcements**
  - Online-only (configurable), weighted rotation, per-message min intervals
  - Optional quiet-hours & “pause if no chat” activity gate
- **Overlay (browser source)**
  - Serves under a private NGINX path (e.g., `https://spff.dev/overlay`)
  - Can react to events/commands (templates; wiring underway)
- **First-class ops**
  - Hot-reload for JSON configs
  - File logging with daily rotation
  - One-liner deploy: `git pull && ./deploy.sh`

---

## Repo layout

```
SpiffyOS/
├─ config/                         # JSON configs (hot-reload)
│  ├─ commands.json
│  ├─ events.json
│  └─ announcements.json
├─ secrets/                        # OAuth tokens (json), not in git
│  ├─ bot.json
│  └─ broadcaster.json
├─ scripts/
│  ├─ deploy.sh                    # publish + restart services + tail
│  └─ logs.sh                      # convenient log tail/grep
├─ src/
│  ├─ SpiffyOS.Bot/
│  │  └─ Program.cs                # DI, hosted services, sockets, router
│  ├─ SpiffyOS.Core/
│  │  ├─ HelixApi.cs              # Helix helpers (app token for /streams + chat)
│  │  ├─ AppTokenProvider.cs
│  │  ├─ TwitchAuth.cs
│  │  ├─ Commands/
│  │  │  ├─ ICommandHandler.cs
│  │  │  ├─ CommandRouter.cs
│  │  │  └─ UptimeCommandHandler.cs
│  │  ├─ EventSubWebSocket.cs     # EventSub WS (chat/follows/subs/reds/bits/raids)
│  │  └─ Events/
│  │     ├─ EventsAnnouncer.cs
│  │     └─ EventsConfig.cs
│  └─ SpiffyOS.Overlay/
│     └─ Program.cs               # Kestrel app for OBS
└─ README.md
```

---

## Configuration

### Environment variables

The bot and overlay read 3 dir paths (set in systemd units):

- `SPIFFYOS_CONFIG=/srv/bots/spiffyos/config`
- `SPIFFYOS_TOKENS=/srv/bots/spiffyos/secrets`
- `SPIFFYOS_LOGS=/srv/bots/spiffyos/logs`

### Twitch settings

Provide these in `appsettings.json` (deployed alongside the bot) **or** via environment variables:

```json
{
  "Twitch": {
    "ClientId": "xxxxxxxxxxxxxxxxxxxxxx",
    "ClientSecret": "yyyyyyyyyyyyyyyyyyyyyy",
    "BroadcasterId": "", // your actual Twitch account
    "BotUserId": "", // your bot account
    "ModeratorUserId": "" // default to broadcaster if omitted
  }
}
```

> The bot uses **app access token** for `/streams` and `/chat/messages` to avoid user-token refresh issues. EventSub WebSockets authenticate with **bot user token** (chat + follows) and **broadcaster user token** (subs/resubs/redemptions/bits/raids).

### Tokens & scopes

Store user tokens as JSON in `secrets/`:

- `secrets/bot.json` — **SpiffyOS** user token with:
  - `user:bot`, `user:write:chat`, `user:read:chat`, `user:manage:chat_color`
  - Moderator scopes (manage/read) for announcements/shoutouts/etc.
- `secrets/broadcaster.json` — **SPIFFgg** user token with:
  - `channel:manage:broadcast`, `clips:edit`, `channel:manage:redemptions`
  - `channel:read:subscriptions`, `channel:read:redemptions`, `channel:read:ads`, …
  - (Full lists were minted earlier and validated.)

### `commands.json`

A single file defines both static and dynamic commands:

```jsonc
{
  "prefix": "!",
  "commands": [
    {
      "name": "discord",
      "type": "static",
      "text": "Join the Discord → https://discord.gg/yourInvite",
      "permission": "everyone", // everyone | subs | vips | mods | broadcaster
      "aliases": ["dc", "voicechat"],
      "replyToUser": false,
      "globalCooldown": 5, // seconds (0 = none)
      "userCooldown": 10, // seconds (0 = none)
      "globalUsage": 0, // per-stream (0 = unlimited)
      "userUsage": 0
    },
    {
      "name": "uptime",
      "type": "dynamic",
      "permission": "everyone",
      "aliases": [],
      "replyToUser": false,
      "globalCooldown": 5,
      "userCooldown": 10,
      "globalUsage": 0,
      "userUsage": 0
    }
  ]
}
```

### `events.json`

Event announcement templates & cooldowns:

```json
{
  "rateLimitSeconds": 0.2,

  "follows": {
    "enabled": true,
    "cooldownSeconds": 2,
    "dedupeWindowSeconds": 60,
    "template": "🆕 Follow from {user.name} — welcome!",
    "batching": {
      "enabled": true,
      "windowSeconds": 20,
      "template": "New followers: {user.list} — welcome in!"
    }
  },

  "subs": {
    "enabled": true,
    "cooldownSeconds": 3,
    "templateNew": "🎉 New sub from {user.name} (Tier {sub.tier}) — thank you!",
    "templateGift": "🎁 Gift sub from {gifter.name} to {user.name} (Tier {sub.tier}) — legend!",
    "templateResub": "🔁 {user.name} resub! {sub.months} months (Tier {sub.tier})",
    "templateMessage": "💬 {user.name}: {message}"
  },

  "redemptions": {
    "enabled": true,
    "cooldownSeconds": 2,
    "template": "🎟️ {user.name} redeemed “{reward.title}”{reward.input}"
  },

  "bits": {
    "enabled": true,
    "cooldownSeconds": 2,
    "template": "✨ {user.name} cheered {bits.amount} bits — thank you!"
  },

  "raids": {
    "enabled": true,
    "cooldownSeconds": 5,
    "template": "🚀 Raid from {raider.name} with {raider.viewers} viewers — welcome in!"
  }
}
```

### `announcements.json`

Timed announcements with rotation & pacing:

```json
{
  "enabled": true,
  "onlineOnly": true,
  "minGapMinutes": 30,
  "quietHours": null, // or { "start": "00:00", "end": "08:00", "timezone": "Europe/London" }
  "activity": { "enabled": false, "noChatMinutes": 10 },
  "messages": [
    {
      "text": "Join the Discord → https://discord.gg/yourInvite",
      "minIntervalMinutes": 45,
      "weight": 2
    },
    {
      "text": "Prime sub is free if you’ve got Amazon Prime. ♥",
      "minIntervalMinutes": 60,
      "weight": 1
    }
  ]
}
```

---

## How commands work

### Static commands (no code)

Add an entry to `commands.json` with `"type": "static"` and a `"text"` reply. Hot-reload applies.

### Dynamic commands (with code)

Set `"type": "dynamic"` in `commands.json`. The router dispatches to a handler class implementing:

```csharp
public interface ICommandHandler
{
    Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct);
}
```

Existing example: `UptimeCommandHandler` → `!uptime`.

### Adding a new dynamic command

1. Create `src/SpiffyOS.Core/Commands/MyThingCommandHandler.cs` implementing `ICommandHandler`.
2. Register it in the router (see `CommandRouter.cs`, search for where handlers are wired; follow existing pattern).
3. Add a command entry to `config/commands.json`:
   ```json
   { "name": "mything", "type": "dynamic", "permission": "everyone", ... }
   ```
4. `git add/commit/push` → `git pull && ./deploy.sh`.

> The router enforces permissions, cooldowns, and usage limits from `commands.json`.

---

## Event handling (follows, subs, redemptions, bits, raids)

- Two EventSub WebSocket connections:
  - **Bot token**: `channel.chat.message` (v1), `channel.follow` (v2)
  - **Broadcaster token**: `channel.subscribe`, `channel.subscription.message`, `channel.channel_points_custom_reward_redemption.add`, `channel.cheer`, `channel.raid`
- `EventsAnnouncer` formats messages from `events.json`; global rate-limit + per-event cooldowns.

---

## Overlay

- Served by `SpiffyOS.Overlay` (Kestrel), reverse-proxied at `https://spff.dev/overlay`
- Keep it **private**:
  - Use NGINX `allow/deny` (IP allowlist), and/or
  - Add HTTP Basic Auth, and/or
  - Require a shared `token` query param/header (currently implemented)
- Upcoming: POST `/overlay/events` from the bot to trigger on-screen animations/templates for raids/subs/bits or `!commands`.

---

## Local development (Windows 11)

```powershell
git clone https://github.com/spff-dev/SpiffyOS
cd SpiffyOS

# Ensure config & secrets exist
mkdir config, secrets, logs

# Put tokens in secrets/  (bot.json, broadcaster.json)
# Put Twitch settings in appsettings.json or environment

dotnet build
dotnet run --project src/SpiffyOS.Bot
```

Logs appear in `logs\bot-YYYYMMDD.log`.  
Static files hot-reload; commands/events/announcements JSONs hot-reload.

---

## Production (Debian 12)

```bash
# one-time:
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0 nginx git

# repo:
sudo mkdir -p /srv/bots/spiffyos && sudo chown -R spiff:spiff /srv/bots/spiffyos
cd /srv/bots/spiffyos
git clone https://github.com/spff-dev/SpiffyOS .
mkdir -p config secrets logs publish/bot publish/overlay

# tokens + config:
#  - secrets/bot.json, secrets/broadcaster.json
#  - config/commands.json, config/events.json, config/announcements.json
#  - appsettings.json with Twitch keys
```

### Systemd services

Two units (environment points at your folders):

- `spiffyos-bot.service` — runs `publish/bot`
- `spiffyos-overlay.service` — runs `publish/overlay` (e.g., port 5100; NGINX proxies `/overlay`)

### Deploy script

```bash
cd /srv/bots/spiffyos
git pull && ./deploy.sh
```

Publishes both apps, restarts services, and tails the bot logs.

### Logs

```bash
scripts/logs.sh bot -n 120
scripts/logs.sh bot --filter 'EventSub|Announcement|CommandRouter|error' -n 200
```

---

## Security

- **Tokens** in `/srv/bots/spiffyos/secrets` only. Never commit.
- Restrict overlay via NGINX (allowlist, auth) — it’s meant for OBS, not public browsing.
- The bot uses **app tokens** for `/streams` and `/chat/messages` to avoid user-token refresh issues.
- Rate-limit chat output to avoid spam and API throttling.

---

## Troubleshooting

- **No chat messages sent**  
  Ensure the bot account **SpiffyOS** is joined to the channel and the app token is valid (logs will show the POST `/chat/messages` status).
- **EventSub 400/403**  
  Check the right token is used for the subscription (bot vs broadcaster) and the required scopes were minted.
- **Announcements “tick error”**  
  Usually means a token/HTTP error; the current build uses app token for `/streams` to avoid refresh races.
- **Commands not loading**  
  See log line: `CommandRouter loaded N commands (prefix '!')`. If `commands.json` has errors, fix and save—hot-reload applies.

---

## License

Private/internal for SPIFFgg/SpiffyOS — adjust as needed.
