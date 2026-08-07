FROM debian:trixie-slim

ARG DEBIAN_FRONTEND=noninteractive

# i386 required only for SteamCMD (32-bit binary)
RUN dpkg --add-architecture i386 && \
    apt-get update && \
    apt-get upgrade -y && \
    apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        gnupg \
        lib32gcc-s1 \
        locales \
        unzip \
        wget \
        xauth \
        xvfb \
    && sed -i -e 's/# en_US.UTF-8 UTF-8/en_US.UTF-8 UTF-8/' \
             -e 's/# cs_CZ.UTF-8 UTF-8/cs_CZ.UTF-8 UTF-8/' /etc/locale.gen \
    && locale-gen \
    && rm -rf /var/lib/apt/lists/*

# WineHQ stable (Wine 11+ WoW64)
RUN mkdir -pm755 /etc/apt/keyrings && \
    wget -O /etc/apt/keyrings/winehq-archive.key https://dl.winehq.org/wine-builds/winehq.key && \
    wget -NP /etc/apt/sources.list.d/ https://dl.winehq.org/wine-builds/debian/dists/trixie/winehq-trixie.sources && \
    apt-get update && \
    apt-get install -y --install-recommends winehq-stable && \
    rm -rf /var/lib/apt/lists/*

# SteamCMD
RUN mkdir -p /opt/steamcmd && \
    curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar xz -C /opt/steamcmd && \
    chmod -R 755 /opt/steamcmd && \
    ln -s /opt/steamcmd/steamcmd.sh /usr/local/bin/steamcmd

# Server user
RUN useradd -m -s /bin/bash server && \
    chown -R server:server /opt/steamcmd

USER server
WORKDIR /home/server

# Wine environment
ENV LANG=en_US.UTF-8
ENV WINEPREFIX=/home/server/.wine
ENV WINEARCH=win64
ENV WINEDEBUG=-all
ENV WINEDLLOVERRIDES="mscoree=d;mshtml=d;winhttp=n,b"
ENV DISPLAY=:99

# Pre-initialize Wine prefix
RUN xvfb-run -a wineboot --init && wineserver --wait

# Create directories that will be volumes (ensures correct ownership)
RUN mkdir -p /home/server/game /home/server/data /home/server/plugins

# Entrypoint
COPY --chown=server:server entrypoint.sh /home/server/entrypoint.sh
RUN chmod +x /home/server/entrypoint.sh

# Server config via environment
ENV STEAM_USER=""
ENV STEAM_PASS=""
ENV SAVE_FILE="StoreFile0.es3"
ENV LAYOUT="0"
ENV GAME_MODE="1"
ENV AUTO_END_DAY="true"
ENV USE_AUTOSAVE="true"
ENV GRANT_PERMISSIONS="true"
ENV AUTOSAVE_MINUTES="5"
ENV TARGET_FPS="60"
ENV GAME_LOCALE="en_US.UTF-8"
ENV DISCORD_WEBHOOK_URL=""

# Game install persisted here (survives restarts, no re-download)
VOLUME /home/server/game

# Save data persisted here
VOLUME /home/server/data

ENTRYPOINT ["/home/server/entrypoint.sh"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=180s --retries=3 \
    CMD pgrep -f "Supermarket Together.exe" > /dev/null && test -f /home/server/data/lobby_id.txt
