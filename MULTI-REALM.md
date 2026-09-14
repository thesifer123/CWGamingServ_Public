# Multi-realm (separate-process realms behind one BBS front)

Players connect to **one public port** (2323), log in **once**, and pick a realm from a menu. Each realm
is its **own OS process** with its **own game DB**, all sharing the one `cwgaming_bbs` login. Isolation is
structural — no shared memory between realms, so no cross-realm state bleed is possible.

```
              telnet 2323 (public)
                     │
              ┌──────▼───────┐   BBS login + realm menu, then proxies the
              │  FRONT (2323) │   connection to the chosen realm's backend.
              │  role=front   │   Runs NO world.
              └───┬──────┬────┘
     handshake +  │      │  (loopback only, not publicly reachable)
     byte-proxy   │      │
          ┌───────▼─┐  ┌─▼───────┐
          │ backend │  │ backend │   role=backend, EnterRealmDirectly for the
          │ "main"  │  │ "two"   │   front-authenticated account. Own game DB.
          │  :2601  │  │  :2602  │
          │mmudreborn│ │mmud..._two│
          └─────────┘  └─────────┘
                shared: cwgaming_bbs (one login)
```

## Roles (`MMUDREBORN_ROLE`)
- **`monolithic`** (default, unchanged): one process does login + a single in-process realm. What you run today.
- **`front`**: owns the public port; login + realm menu; proxies to backends. Loads no world.
- **`backend`**: binds loopback only; accepts a front handshake (shared secret) vouching for an
  already-authenticated account and runs the realm directly. No re-login.

## Run it
```bash
MMUDREBORN_MULTIREALM=1 ./run-game-supervised.sh 2323   # front on 2323, backends on auto-assigned loopback ports
```
Stop it with Ctrl+C (or SIGTERM); every process saves its world first. `run-game-supervised.sh` builds
Release, launches every backend, generates the front's `realms.json` from the **same** realm list (no
drift), then launches the front. Logs + pids in `realms-run/`. A proxy secret is
generated once into `realms-run/proxy.secret` (override with `MMUDREBORN_REALM_PROXY_SECRET`).

## Cutover (players keep using 2323)
The current monolithic server owns 2323. To switch:
1. Stop the current monolithic server (whatever supervises 2323 today).
2. `MMUDREBORN_MULTIREALM=1 ./run-game-supervised.sh 2323`
3. Connect to 2323 → you now get the realm menu. Players change nothing.

To roll back: stop the supervisor and restart it without `MMUDREBORN_MULTIREALM=1`. Realm DBs are untouched.

## Add a realm
1. Provision its DB (clone content, wipe characters) — e.g. `mmudreborn_three`.
2. Add one line to the `REALMS=(...)` list in `run-game-supervised.sh` (or set `MMUDREBORN_REALMS`):
   `"three|Hardcore|mmudreborn_three"` (id | display name | game DB; loopback ports are auto-assigned).
3. Restart. Infra ids are stable; the display name is just the menu label — rename anytime.

## Per-realm vs shared settings
- `SYSOP CONFIGURE ...` (MINWAIT, exp rate, spawn rate, `PvpLevelRange`, …) live in each realm's own game
  DB → **per-realm** automatically.
- BBS-level `;maxconn` / `;ban` live in the shared `cwgaming_bbs` → shared front-door settings.

## Notes / still to verify in a live deploy
- The front's `;who` unions each backend's pushed `public.online_players` with users sitting at the BBS menu.
- `=x` / hangup closes only the proxied leg (that realm), leaving other same-account sessions alive; the
  per-realm "same character logged in again" kick is already the in-realm behavior, now naturally scoped
  per realm because each realm is its own process.
- `run-game-supervised.sh` relaunches the whole set when any process exits; for resilience across
  reboots, run it under a service manager such as systemd.
