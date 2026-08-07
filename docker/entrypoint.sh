#!/bin/bash
set -e

GAME_DIR="/home/server/game"
GAME_EXE="${GAME_DIR}/Supermarket Together.exe"
DATA_DIR="/home/server/data"
PLUGINS_DIR="/home/server/plugins"
APP_ID="2709570"
BEPINEX_VERSION="5.4.23.5"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_win_x64_${BEPINEX_VERSION}.zip"
STEAM_INSTALLER_URL="https://cdn.cloudflare.steamstatic.com/client/installer/SteamSetup.exe"
STEAM_DIR="${WINEPREFIX}/drive_c/Program Files (x86)/Steam"
GAME_PID=""

# --- Start virtual display ---
start_xvfb() {
    # Clean up stale lock from previous run
    rm -f /tmp/.X99-lock

    if ! pgrep -x Xvfb > /dev/null; then
        Xvfb :99 -screen 0 64x64x16 -nolisten tcp &
        sleep 1
        echo "[AutoHost] Xvfb started."
    fi

    # Ensure XDG_RUNTIME_DIR exists (required by Steam)
    export XDG_RUNTIME_DIR="/tmp/runtime-server"
    mkdir -p "${XDG_RUNTIME_DIR}"
}

# --- Install/Update game via SteamCMD ---
install_game() {
    if [ -f "${GAME_EXE}" ]; then
        echo "[AutoHost] Game already installed, checking for updates..."
    else
        echo "[AutoHost] Installing Supermarket Together (AppID ${APP_ID})..."
    fi

    if [ -z "${STEAM_USER}" ] || [ -z "${STEAM_PASS}" ]; then
        echo "[AutoHost] ERROR: STEAM_USER and STEAM_PASS must be set!"
        exit 1
    fi

    /opt/steamcmd/steamcmd.sh \
        +@sSteamCmdForcePlatformType windows \
        +force_install_dir "${GAME_DIR}" \
        +login "${STEAM_USER}" "${STEAM_PASS}" \
        +app_update "${APP_ID}" validate \
        +quit

    echo "[AutoHost] Game installed/updated."
}

# --- Install Steam Windows client in Wine prefix ---
install_steam_client() {
    if [ -f "${STEAM_DIR}/steam.exe" ]; then
        echo "[AutoHost] Steam client already installed."
        return
    fi

    echo "[AutoHost] Installing Steam Windows client..."
    local tmpfile
    tmpfile=$(mktemp --suffix=.exe)
    curl -fsSL "${STEAM_INSTALLER_URL}" -o "${tmpfile}"
    wine "${tmpfile}" /S
    rm "${tmpfile}"

    # Wait for install to finish
    wineserver --wait
    echo "[AutoHost] Steam client installed."
}

# --- Register game with Steam client via appmanifest + library folder ---
register_game_with_steam() {
    local steamapps="${STEAM_DIR}/steamapps"
    local manifest="${steamapps}/appmanifest_${APP_ID}.acf"

    # Create steamapps dir if missing
    mkdir -p "${steamapps}/common"

    # Symlink the SteamCMD game install into Steam's library
    if [ ! -e "${steamapps}/common/Supermarket Together" ]; then
        ln -sf "${GAME_DIR}" "${steamapps}/common/Supermarket Together"
        echo "[AutoHost] Game symlinked into Steam library."
    fi

    # Create appmanifest so Steam recognizes the game
    if [ ! -f "${manifest}" ]; then
        cat > "${manifest}" <<EOF
"AppState"
{
	"appid"		"${APP_ID}"
	"Universe"		"1"
	"name"		"Supermarket Together"
	"StateFlags"		"4"
	"installdir"		"Supermarket Together"
	"AutoUpdateBehavior"		"1"
}
EOF
        echo "[AutoHost] App manifest created."
    fi
}

# --- Start Steam client ---
start_steam() {
    if pgrep -f "steam.exe" > /dev/null; then
        echo "[AutoHost] Steam client already running."
        return
    fi

    if [ -z "${STEAM_USER}" ] || [ -z "${STEAM_PASS}" ]; then
        echo "[AutoHost] ERROR: STEAM_USER and STEAM_PASS must be set!"
        exit 1
    fi

    echo "[AutoHost] Starting Steam client..."
    wine "${STEAM_DIR}/steam.exe" -silent -nofriendsui -nojoy -noshaders -vrdisable -cef-disable-gpu -cef-disable-gpu-sandbox -cef-force-occlusion -cef-delaypageload -login "${STEAM_USER}" "${STEAM_PASS}" &

    # Wait for Steam to be ready
    local timeout=90
    local elapsed=0
    while ! pgrep -f "steamwebhelper" > /dev/null 2>&1 && [ ${elapsed} -lt ${timeout} ]; do
        sleep 2
        elapsed=$((elapsed + 2))
    done

    if [ ${elapsed} -ge ${timeout} ]; then
        echo "[AutoHost] WARNING: Steam client may not have started properly."
    else
        echo "[AutoHost] Steam client running."
    fi

    # Give Steam a moment to fully initialize
    sleep 10
}

# --- Install BepInEx ---
install_bepinex() {
    if [ ! -f "${GAME_DIR}/BepInEx/core/BepInEx.dll" ]; then
        echo "[AutoHost] Installing BepInEx ${BEPINEX_VERSION}..."
        local tmpfile
        tmpfile=$(mktemp)
        curl -fsSL "${BEPINEX_URL}" -o "${tmpfile}"
        unzip -o "${tmpfile}" -d "${GAME_DIR}"
        rm "${tmpfile}"
        echo "[AutoHost] BepInEx installed."
    else
        echo "[AutoHost] BepInEx already present."
    fi
}

# --- Install plugins ---
install_plugins() {
    local game_plugins="${GAME_DIR}/BepInEx/plugins"

    if [ -d "${PLUGINS_DIR}" ] && [ "$(ls -A "${PLUGINS_DIR}" 2>/dev/null)" ]; then
        rm -rf "${game_plugins}"
        mkdir -p "${game_plugins}"
        cp -r "${PLUGINS_DIR}/." "${game_plugins}/"
        echo "[AutoHost] Plugins synced from ${PLUGINS_DIR}."
    else
        echo "[AutoHost] WARNING: No plugins found in ${PLUGINS_DIR}."
        echo "[AutoHost] Mount your plugins with -v /path/to/plugins:/home/server/plugins"
        exit 1
    fi
}

# --- Generate AutoHost config ---
generate_config() {
    local config_dir="${GAME_DIR}/BepInEx/config"
    local config_file="${config_dir}/com.karelkryda.autohost.cfg"

    mkdir -p "${config_dir}"

    cat > "${config_file}" <<EOF
[Server]
SaveFile = ${SAVE_FILE}
Layout = ${LAYOUT}
GameMode = ${GAME_MODE}
AutoEndDay = ${AUTO_END_DAY}
UseAutosave = ${USE_AUTOSAVE}
GrantAllPermissions = ${GRANT_PERMISSIONS}
LobbyIdFile = lobby_id.txt
AutosaveMinutes = ${AUTOSAVE_MINUTES:-5}
TargetFrameRate = ${TARGET_FPS:-60}
DiscordWebhookUrl = ${DISCORD_WEBHOOK_URL:-}
EOF

    echo "[AutoHost] Config: save=${SAVE_FILE}, layout=${LAYOUT}, mode=${GAME_MODE}"
}

# --- Clear BepInEx log from previous run ---
clear_bepinex_log() {
    : > "${GAME_DIR}/BepInEx/LogOutput.log"
}

# --- Validate save file exists ---
validate_saves() {
    local save_path="${DATA_DIR}/${SAVE_FILE}"
    if [ ! -f "${save_path}" ]; then
        echo "[AutoHost] ERROR: Save file '${SAVE_FILE}' not found in ${DATA_DIR}/"
        echo "[AutoHost]"
        echo "[AutoHost] You need to provide your game saves. Copy them from:"
        echo "[AutoHost]   Windows: %APPDATA%\\LocalLow\\DDTNL\\Supermarket Together\\"
        echo "[AutoHost]   Files needed: ${SAVE_FILE} (and optionally Autosaves/ folder)"
        echo "[AutoHost]"
        echo "[AutoHost] Place them in the mounted saves directory (./saves/ by default)"
        exit 1
    fi
    echo "[AutoHost] Save file found: ${save_path}"
}

# --- Symlink persistent save data ---
setup_saves() {
    local wine_saves="${WINEPREFIX}/drive_c/users/server/AppData/LocalLow/DDTNL/Supermarket Together"
    mkdir -p "${DATA_DIR}"
    mkdir -p "$(dirname "${wine_saves}")"

    if [ ! -L "${wine_saves}" ]; then
        rm -rf "${wine_saves}"
        ln -sf "${DATA_DIR}" "${wine_saves}"
    fi

    echo "[AutoHost] Saves: ${wine_saves} -> ${DATA_DIR}"
}

# --- Shutdown handling ---
shutdown() {
    echo "[AutoHost] Shutting down..."

    # Signal plugin to save before exit
    touch "${DATA_DIR}/.save_and_quit"
    echo "[AutoHost] Save signal sent, waiting for plugin to acknowledge..."

    # Wait for plugin to pick up the file (max 5s)
    local elapsed=0
    while [ -f "${DATA_DIR}/.save_and_quit" ] && [ ${elapsed} -lt 5 ]; do
        sleep 1
        elapsed=$((elapsed + 1))
    done

    # Give the save coroutine time to finish
    sleep 5

    # wineserver -k gracefully terminates all Wine processes
    wineserver -k 2>/dev/null

    # Kill Xvfb
    pkill -x Xvfb 2>/dev/null
    rm -f /tmp/.X99-lock

    # Clean up trigger file
    rm -f "${DATA_DIR}/.save_and_quit"

    echo "[AutoHost] Shutdown complete."
    exit 0
}

trap shutdown SIGTERM SIGINT

# --- Launch game ---
launch_game() {
    echo "[AutoHost] Launching server via Steam..."

    # Set locale to match save file origin (affects .NET decimal separator in ES3)
    export LANG="${GAME_LOCALE:-en_US.UTF-8}"
    export LC_ALL="${GAME_LOCALE:-en_US.UTF-8}"

    # Clean stale files from previous runs
    rm -f "${DATA_DIR}/lobby_id.txt"
    rm -f "${DATA_DIR}/.save_and_quit"

    wine "${STEAM_DIR}/steam.exe" -applaunch "${APP_ID}" -batchmode -screen-width 64 -screen-height 64 -screen-quality Fastest 2>&1 &

    # Wait for game process to appear
    local timeout=60
    local elapsed=0
    while ! pgrep -f "Supermarket Together.exe" > /dev/null 2>&1 && [ ${elapsed} -lt ${timeout} ]; do
        sleep 2
        elapsed=$((elapsed + 2))
    done

    if pgrep -f "Supermarket Together.exe" > /dev/null 2>&1; then
        GAME_PID=$(pgrep -f "Supermarket Together.exe" | head -1)
        echo "[AutoHost] Game started (PID: ${GAME_PID})"

        # Stream BepInEx log to stdout (visible in docker logs)
        local bepinex_log="${GAME_DIR}/BepInEx/LogOutput.log"
        if [ -f "${bepinex_log}" ]; then
            tail -f "${bepinex_log}" &
        fi
    else
        echo "[AutoHost] ERROR: Game did not start within ${timeout}s."
        exit 1
    fi

    # Wait for lobby ID (written to save dir which is symlinked to DATA_DIR)
    timeout=120
    elapsed=0
    while [ ! -f "${DATA_DIR}/lobby_id.txt" ] && [ ${elapsed} -lt ${timeout} ]; do
        sleep 2
        elapsed=$((elapsed + 2))
    done

    if [ -f "${DATA_DIR}/lobby_id.txt" ]; then
        echo "[AutoHost] =============================="
        echo "[AutoHost] SERVER READY!"
        echo "[AutoHost] Lobby ID: $(cat "${DATA_DIR}/lobby_id.txt")"
        echo "[AutoHost] =============================="
    else
        echo "[AutoHost] WARNING: Lobby ID not found after ${timeout}s."
    fi

    # Keep alive until game exits
    # Uses 'sleep &; wait' so bash can process SIGTERM trap immediately
    while kill -0 "${GAME_PID}" 2>/dev/null; do
        sleep 1 &
        wait $!
    done
    echo "[AutoHost] Server exited."
}

# --- Main ---
echo "[AutoHost] Supermarket Together Dedicated Server"
echo "[AutoHost] ======================================"

start_xvfb
install_game
install_steam_client
register_game_with_steam
start_steam
install_bepinex
install_plugins
generate_config
clear_bepinex_log
validate_saves
setup_saves
launch_game
