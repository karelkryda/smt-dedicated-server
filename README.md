# Supermarket Together - Dedicated Server

Headless dedicated server for [Supermarket Together](https://store.steampowered.com/app/2709570/Supermarket_Together/) running in Docker.

The game has no official dedicated server support. This project provides a BepInEx plugin and Docker image that auto-hosts a lobby without manual interaction.

## Quick Start

1. Create a directory for the server:

```bash
mkdir smt-server && cd smt-server
```

2. Download the compose file:

```bash
curl -O https://raw.githubusercontent.com/karelkryda/smt-dedicated-server/main/docker/docker-compose.yml
```

3. Create `.env` with your Steam credentials (use a secondary account, game is F2P):

```bash
cat > .env <<EOF
STEAM_USER=your_dedicated_account
STEAM_PASS=your_password
EOF
```

4. Copy your save files from `%APPDATA%\LocalLow\DDTNL\Supermarket Together\`:

```bash
mkdir -p saves/Autosaves
cp /path/to/StoreFile0.es3 saves/
cp /path/to/Autosaves/Autosave001.es3 saves/Autosaves/
```

5. Download the plugin from [Releases](https://github.com/karelkryda/smt-dedicated-server/releases) and place it in a `plugins/` folder:

```bash
mkdir plugins
curl -L -o plugins/AutoHost.dll https://github.com/karelkryda/smt-dedicated-server/releases/latest/download/AutoHost.dll
```

6. Start the server:

```bash
docker compose up -d
```

First start takes ~5 minutes (downloads the game, installs Steam client). After that, restarts take ~1 minute.

7. Get the lobby ID:

```bash
docker logs smt-server | grep "Lobby ID"
```

Players join via the Steam lobby ID.

## Requirements

- Docker host with **16 GB RAM**
- Second Steam account (free - game is F2P) with Steam Guard **disabled**
- Save files from an existing game

## Configuration

All settings via environment variables in `docker-compose.yml`:

| Variable              | Default          | Description                                   |
| --------------------- | ---------------- | --------------------------------------------- |
| `STEAM_USER`          | -                | Dedicated Steam account username              |
| `STEAM_PASS`          | -                | Dedicated Steam account password              |
| `SAVE_FILE`           | `StoreFile0.es3` | Which save to load                            |
| `LAYOUT`              | `0`              | Map layout (0=Classic, 3=Plaza)               |
| `GAME_MODE`           | `1`              | 1=Friends Only, 2=Public                      |
| `AUTO_END_DAY`        | `true`           | Auto-skip day-end screen                      |
| `USE_AUTOSAVE`        | `true`           | Load from autosave if newer                   |
| `GRANT_PERMISSIONS`   | `true`           | Grant all permissions to joining players      |
| `AUTOSAVE_MINUTES`    | `5`              | Autosave interval in game-minutes             |
| `TARGET_FPS`          | `60`             | Server frame rate (30-60 recommended)         |
| `GAME_LOCALE`         | `en_US.UTF-8`    | Locale for save file compatibility            |
| `DISCORD_WEBHOOK_URL` | -                | Discord webhook URL for lobby ID notification |

## Updating

```bash
docker compose pull
docker compose up -d
```

Download the latest `AutoHost.dll` from [Releases](https://github.com/karelkryda/smt-dedicated-server/releases) and replace it in `plugins/`.

## Resource Usage

- **RAM**: ~11 GB (Unity loads all assets)
- **CPU**: ~1 core at 60fps
- **Disk**: ~15 GB (game + Steam + Wine, stored in Docker volumes)

## Known Limitations

- No graceful save-on-shutdown (relies on periodic autosave)
- Host player spawns but is uncontrolled (stands idle in the shop)
- Requires the full graphics pipeline (cannot use Unity's `-nographics`)

## How It Works

The Docker container runs the game inside Wine with a virtual display (Xvfb). A BepInEx plugin bypasses the game menu and triggers the game's own initialization flow - loading saves, spawning employees, creating a Steam lobby. The server then auto-continues days and autosaves periodically.

## License

MIT
