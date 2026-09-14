#!/usr/bin/env bash
# Supervises the CWGaming BBS host so a sysop can deploy + restart from inside the game.
#
# Loop: pull latest (both repos) -> build -> run. When the host exits with code 42 (sysop RESTART),
# the loop pulls + rebuilds + relaunches, so a committed code change goes live. Exit 0 (sysop
# SHUTDOWN, Ctrl+C, or SIGTERM/`docker stop` — all of which save state first) stops the loop. Any
# other (crash) relaunches after a short pause.
#
# Usage: run-game-supervised.sh [PORT]   (default 2323)
set -uo pipefail

port="${1:-2323}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bbs_dir="$script_dir"
mmud_dir="$(cd "$script_dir/../mmudreborn" 2>/dev/null && pwd || true)"

# The BBS host no longer references the door at build time (it loads doors at runtime by reflection over
# CWGaming.Shared's IBbsDoorFactory). So we build the mmudreborn door separately and hand the host its
# DLL path via CWGAMING_DOOR_ASSEMBLIES. Drop another door's DLL path here (or a doors/ dir) to mount it.
door_project="$mmud_dir/src/mmudreborn.Server/mmudreborn.Server.csproj"
door_assembly="$mmud_dir/src/mmudreborn.Server/bin/Release/net8.0/mmudreborn.Server.dll"
export CWGAMING_DOOR_ASSEMBLIES="$door_assembly"

# ------------------------------------------------------------------------------------------------
# MULTI-REALM (opt-in): set MMUDREBORN_MULTIREALM=1 to run the separate-process topology instead of
# one monolithic host — a world-less FRONT on $port (public), plus one BACKEND game process per realm
# on loopback ports, all sharing the one cwgaming_bbs login. Players still connect to $port; they get
# a realm menu and are proxied to the chosen backend. Leave it unset and this script behaves EXACTLY
# as before (single in-process realm on $port). See MULTI-REALM.md.
MULTIREALM="${MMUDREBORN_MULTIREALM:-0}"
# Realm list: "id|displayName|gameDbName". Add a line to add a realm. Infra ids are stable; the display
# name is only the menu label (rename freely). Backend telnet/API ports are auto-assigned on loopback and
# are GUARANTEED never to equal the public front port ($port) — so passing any front port is always safe.
#
# NON-DESTRUCTIVE: this list only decides which realms are SERVED. Removing a realm here just stops
# launching its backend — its game DB and all characters persist untouched until you manually DROP it.
# Nothing here (or in the backend bootstrap) ever wipes or reseeds a realm DB.
REALMS=(
  "main|Main|mmudreborn"
  "two|PvP|mmudreborn_two"
)
# Override the realm list without editing this file: MMUDREBORN_REALMS="id|Display|db;id2|Display2|db2"
if [[ -n "${MMUDREBORN_REALMS:-}" ]]; then
  IFS=';' read -r -a REALMS <<< "$MMUDREBORN_REALMS"
fi
# Base loopback ports for auto-assignment (only matter internally; bumped past $port automatically).
BACKEND_PORT_BASE="${REALM_BACKEND_PORT_BASE:-2601}"
BACKEND_API_BASE="${REALM_BACKEND_API_BASE:-5601}"
FRONT_API_PORT="${REALM_FRONT_API_PORT:-5600}"
run_dir="$script_dir/realms-run"
bbs_db="${REALM_BBS_DB:-cwgaming_bbs}"
pg_host="${REALM_PGHOST:-localhost}"; pg_port="${REALM_PGPORT:-5432}"
pg_user="${REALM_PGUSER:-mmudreborn}"; pg_pass="${REALM_PGPASS:-mmudreborn}"
conn() { echo "Host=$pg_host;Port=$pg_port;Database=$1;Username=$pg_user;Password=$pg_pass"; }
# A shared secret gates the front->backend handshake; persist one so front + backends always match.
resolve_secret() {
  mkdir -p "$run_dir"
  local f="$run_dir/proxy.secret"
  if [[ -n "${MMUDREBORN_REALM_PROXY_SECRET:-}" ]]; then echo "$MMUDREBORN_REALM_PROXY_SECRET" > "$f"
  elif [[ ! -s "$f" ]]; then head -c 32 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' > "$f"; fi
  cat "$f"
}
# ------------------------------------------------------------------------------------------------

RESTART_CODE=42

child_pid=""
realm_pids=()
shutting_down=0
forward_signal() {
  # A terminal/stop signal to the supervisor means STOP — not relaunch. Mark it, ask the host to perform
  # its graceful save (its own SIGTERM/SIGINT handler), and arm a watchdog that hard-kills the host if the
  # save hangs, so Ctrl+C always escapes and the telnet/API ports are released.
  shutting_down=1
  # Multi-realm: TERM every process (each backend saves its own world). Monolithic: just the one host.
  local targets=()
  if [[ "${#realm_pids[@]}" -gt 0 ]]; then targets=("${realm_pids[@]}"); elif [[ -n "$child_pid" ]]; then targets=("$child_pid"); fi
  if [[ "${#targets[@]}" -gt 0 ]]; then
    echo ""
    echo "[supervisor] signal received; asking host(s) to save + shut down (pid ${targets[*]})..."
    kill -TERM "${targets[@]}" 2>/dev/null || true
    ( sleep 20; kill -KILL "${targets[@]}" 2>/dev/null ) &
  fi
}
trap forward_signal TERM INT

pull_repo() {
  local dir="$1"
  [[ -n "$dir" && -d "$dir/.git" ]] || return 0
  echo "[supervisor] git pull $dir"
  git -C "$dir" pull --ff-only || echo "[supervisor] warning: git pull failed in $dir (continuing with local code)"
}

# Pre-flight: if a stale/orphaned CWGaming host is still holding our telnet port (it also holds the
# BBS API port — same process), kill it BEFORE launching rather than crash-looping on "Address already
# in use". Gated to CWGaming* processes and to THIS supervisor's port, so it never touches an unrelated
# process or another realm/instance on a different port (e.g. staging).
ensure_ports_free() {
  local p="${1:-$port}"
  local pids
  pids=$(ss -ltnpH 2>/dev/null | grep -E ":$p[[:space:]]" | grep -i 'CWGaming' | grep -oP 'pid=\K[0-9]+' | sort -u)
  [[ -z "$pids" ]] && return 0

  echo "[supervisor] port $p already held by stale CWGaming host (pid: $pids) — killing before launch"
  kill -TERM $pids 2>/dev/null || true
  local i
  for i in $(seq 1 20); do  # up to ~10s for a graceful release
    ss -ltnH 2>/dev/null | grep -qE ":$p[[:space:]]" || { echo "[supervisor] port $p released"; return 0; }
    sleep 0.5
  done

  echo "[supervisor] port $p still held; force-killing (pid: $pids)"
  kill -KILL $pids 2>/dev/null || true
  sleep 1
}

# Multi-realm: launch every backend + the front, generate the front's realms.json from the SAME list,
# wait for ANY process to exit, then tear the whole set down. Sets `code` to the exit status of whichever
# process exited first (so a sysop RESTART=42 from inside a realm rebuilds+relaunches the whole topology).
launch_realms() {
  local secret; secret="$(resolve_secret)"
  local host_bin="$bbs_dir/bin/Release/net8.0/CWGamingServ"
  mkdir -p "$run_dir"
  realm_pids=()

  # The front's own API port must never be the public telnet port either.
  local front_api="$FRONT_API_PORT"; while [[ "$front_api" == "$port" ]]; do front_api=$((front_api + 1)); done

  ensure_ports_free "$port"

  # Auto-assign each backend a loopback telnet + API port, ALWAYS skipping the front port (and each
  # other) — so no realm ever lands on the port a player connects to, whatever port is passed in.
  local realms_json="$run_dir/realms.json"; local first=1
  local bport="$BACKEND_PORT_BASE" api="$BACKEND_API_BASE"
  local -a back_ports=()
  echo '{ "realms": [' > "$realms_json"
  local e id disp gamedb
  for e in "${REALMS[@]}"; do
    IFS='|' read -r id disp gamedb <<< "$e"
    while [[ "$bport" == "$port" ]]; do bport=$((bport + 1)); done
    while [[ "$api" == "$port" || "$api" == "$front_api" ]]; do api=$((api + 1)); done
    ensure_ports_free "$bport"
    back_ports+=("$bport")
    echo "[supervisor] backend '$id' ($disp) loopback:$bport db=$gamedb api=$api"
    MMUDREBORN_ROLE=backend MMUDREBORN_REALM_ID="$id" \
      MMUDREBORN_POSTGRES_CONNECTION="$(conn "$gamedb")" \
      CWGAMING_BBS_POSTGRES_CONNECTION="$(conn "$bbs_db")" \
      CWGAMING_BBS_API_URL="http://127.0.0.1:$api/" \
      CWGAMING_DOOR_ASSEMBLIES="$door_assembly" \
      MMUDREBORN_REALM_PROXY_SECRET="$secret" \
      "$host_bin" "$bport" &
    realm_pids+=("$!")
    [[ $first -eq 0 ]] && echo ',' >> "$realms_json"; first=0
    printf '  {"id":"%s","displayName":"%s","backendHost":"127.0.0.1","backendPort":%s,"gameDb":"%s","enabled":true,"order":%s}' \
      "$id" "$disp" "$bport" "$(conn "$gamedb")" "$api" >> "$realms_json"
    bport=$((bport + 1)); api=$((api + 1))
  done
  echo '] }' >> "$realms_json"

  # Give backends a moment to bind their loopback ports before the front starts routing to them.
  local bp i
  for bp in "${back_ports[@]}"; do
    for i in $(seq 1 60); do (exec 3<>"/dev/tcp/127.0.0.1/$bp") 2>/dev/null && { exec 3>&-; break; }; sleep 1; done
  done

  echo "[supervisor] starting FRONT on public port $port (api $front_api, realms: ${#REALMS[@]})"
  MMUDREBORN_ROLE=front MMUDREBORN_REALMS_CONFIG="$realms_json" \
    CWGAMING_BBS_POSTGRES_CONNECTION="$(conn "$bbs_db")" \
    CWGAMING_BBS_API_URL="http://127.0.0.1:$front_api/" \
    MMUDREBORN_REALM_PROXY_SECRET="$secret" \
    "$host_bin" "$port" &
  local front_pid="$!"
  realm_pids+=("$front_pid")
  child_pid="$front_pid"   # forward_signal falls back to this; realm_pids drives the multi-kill

  # Wait for ANY process to exit; capture its code.
  code=0
  while true; do
    local p
    for p in "${realm_pids[@]}"; do
      if ! kill -0 "$p" 2>/dev/null; then wait "$p" 2>/dev/null; code=$?; break 2; fi
    done
    [[ "$shutting_down" == "1" ]] && { wait "$front_pid" 2>/dev/null; code=$?; break; }
    sleep 1
  done

  # One process ended (crash, or a realm's sysop RESTART=42, or shutdown) — stop the rest, saving worlds.
  echo "[supervisor] a realm process exited ($code); stopping the rest of the topology"
  local p
  for p in "${realm_pids[@]}"; do kill -TERM "$p" 2>/dev/null || true; done
  ( sleep 20; for p in "${realm_pids[@]}"; do kill -KILL "$p" 2>/dev/null || true; done ) &
  for p in "${realm_pids[@]}"; do while kill -0 "$p" 2>/dev/null; do wait "$p" 2>/dev/null || true; done; done
  realm_pids=(); child_pid=""
}

# Startup takeover: if another (e.g. headless) supervisor is already running THIS realm, stop it and
# its host and claim control, so the copy you launch in your terminal — with visible logging — becomes
# the one in charge instead of crash-looping against the old one. Keyed on the telnet port, so it never
# disturbs another realm/instance (e.g. staging) on a different port.
claim_control() {
  local self=$$
  local host_pids
  host_pids=$(ss -ltnpH 2>/dev/null | grep -E ":$port[[:space:]]" | grep -i 'CWGaming' | grep -oP 'pid=\K[0-9]+' | sort -u)
  [[ -z "$host_pids" ]] && return 0

  echo "[supervisor] a CWGaming host is already running on port $port (pid: $host_pids) — taking over"
  local hp sup
  for hp in $host_pids; do
    # The host's parent is its supervisor; stop it first so it can't relaunch the host after we kill it.
    sup=$(ps -o ppid= -p "$hp" 2>/dev/null | tr -d ' ')
    if [[ -n "$sup" && "$sup" != "$self" && "$sup" != "1" ]] && ps -o args= -p "$sup" 2>/dev/null | grep -q 'run-game-supervised'; then
      echo "[supervisor] stopping previous supervisor (pid $sup)"
      kill -TERM "$sup" 2>/dev/null || true
    fi
    # Ask the old host to save state + exit gracefully.
    kill -TERM "$hp" 2>/dev/null || true
  done

  # Wait for the port to release (a graceful save can take a few seconds); force-kill as a last resort.
  local i
  for i in $(seq 1 50); do  # up to ~25s, enough for the host's graceful save
    ss -ltnH 2>/dev/null | grep -qE ":$port[[:space:]]" || { echo "[supervisor] previous host stopped; port $port is free"; return 0; }
    sleep 0.5
  done
  echo "[supervisor] previous host still holding port $port after 25s; force-killing (pid: $host_pids)"
  kill -KILL $host_pids 2>/dev/null || true
  sleep 1
}

# Claim control before entering the build/run loop.
claim_control

while true; do
  pull_repo "$mmud_dir"
  pull_repo "$bbs_dir"

  echo "[supervisor] building door + host (Release)..."
  # Build the door first (the host no longer pulls it in via a project reference), then the host.
  if [[ -z "$mmud_dir" || ! -f "$door_project" ]]; then
    echo "[supervisor] cannot find the mmudreborn door project at $door_project" >&2
    sleep 10
    continue
  fi
  if ! dotnet build -c Release "$door_project" >/dev/null; then
    echo "[supervisor] door build failed; retrying in 10s" >&2
    sleep 10
    continue
  fi
  if ! dotnet build -c Release "$bbs_dir" >/dev/null; then
    echo "[supervisor] host build failed; retrying in 10s" >&2
    sleep 10
    continue
  fi

  if [[ "$MULTIREALM" == "1" ]]; then
    # Separate-process topology: launch the front + every backend and supervise the whole set.
    # launch_realms sets `code` to whichever process exited first (crash / sysop RESTART=42 / shutdown).
    launch_realms
    echo "[supervisor] realm topology exited with code $code"
  else
    # Belt-and-suspenders: clear any lingering host on our port before this launch (covers an orphan our
    # own previous iteration failed to reap), so we never crash-loop on "Address already in use".
    ensure_ports_free

    echo "[supervisor] starting CWGaming host on port $port"
    # Launch the built binary DIRECTLY rather than via `dotnet run`. The `dotnet run` wrapper spawns the
    # real CWGamingServ binary as a grandchild, so the supervisor's $child_pid (and the SIGTERM it
    # forwards) tracked the wrapper, not the server. On restart the wrapper could exit while the server
    # lingered/orphaned — holding ports 2323/5097 — so every relaunch crashed with "Address already in
    # use" (exit 134) in a 5s loop. Running the binary directly makes it the supervised child: `wait`
    # and signal-forwarding reach it, and the ports are released before the next launch. The build step
    # above produces this apphost.
    "$bbs_dir/bin/Release/net8.0/CWGamingServ" "$port" &
    child_pid=$!

    # `wait` returns early (status >128) if our signal trap fires mid-wait — possibly before the host has
    # finished its graceful save. Re-wait until the child is genuinely gone so its telnet/API ports are
    # released before we relaunch or exit; otherwise the next launch hits "Address already in use".
    code=0
    while kill -0 "$child_pid" 2>/dev/null; do
      wait "$child_pid"
      code=$?
    done
    child_pid=""
    echo "[supervisor] host exited with code $code"
  fi

  # Any signal to the supervisor (Ctrl+C / SIGTERM) means stop — never relaunch.
  if [[ "$shutting_down" == "1" ]]; then
    echo "[supervisor] shutdown signal received; stopping supervisor"
    break
  fi

  case "$code" in
    "$RESTART_CODE") echo "[supervisor] restart requested; relaunching latest build" ;;
    0) echo "[supervisor] clean shutdown; stopping supervisor"; break ;;
    *) echo "[supervisor] unexpected exit ($code); relaunching in 5s" >&2; sleep 5 ;;
  esac
done
