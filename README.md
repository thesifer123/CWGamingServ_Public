# CWGamingServ

A telnet BBS host for door games, written against .NET 8 with PostgreSQL.

It owns everything around the game: the telnet transport (including GMCP for clients that
ask for it), account registration and login, the board menu, sysop commands such as `;ban`,
`;maxconn` and `;access`, and a small loopback HTTP API for account lookups. Door games are
loaded at runtime from their own assemblies. It is the host for
[mmudreborn](https://github.com/thesifer123/mmudreborn_open), and can run several game realms
behind a single login (see [MULTI-REALM.md](MULTI-REALM.md)).

## Prerequisites

- .NET 8 SDK
- PostgreSQL. The mmudreborn repository's `docker-compose.yml` starts a development instance.
- A door to host. For mmudreborn, check it out **as a sibling directory**. The solution file
  includes its projects, and `run-game-supervised.sh` builds it. The directory names matter,
  so clone both like this:

  ```bash
  git clone https://github.com/thesifer123/CWGamingServ_Public.git CWGamingServ
  git clone https://github.com/thesifer123/mmudreborn_open.git mmudreborn
  ```

  ```
  parent/
    CWGamingServ/
    mmudreborn/
  ```

## Build

```bash
dotnet build CWGamingServ.sln
```

## Run

The simplest route is the supervisor script. It builds the mmudreborn door and the host in
Release, runs the host, and relaunches it when a sysop restarts the board from inside the
game (exit code 42). Ctrl+C or SIGTERM saves state and stops it.

```bash
./run-game-supervised.sh 2323
```

Before every build it runs `git pull --ff-only` in both repositories. Remove the two
`pull_repo` calls if you don't want that.

To run the host by hand, point it at the door assembly:

```bash
dotnet build -c Release ../mmudreborn/src/mmudreborn.Server/mmudreborn.Server.csproj
CWGAMING_DOOR_ASSEMBLIES=../mmudreborn/src/mmudreborn.Server/bin/Release/net8.0/mmudreborn.Server.dll \
  dotnet run -c Release -- 2323
```

Players connect with any telnet client to the port you chose (default 2323).

## Configuration

All settings are environment variables. The defaults suit local development only.

| Variable | Purpose | Default |
|---|---|---|
| `MMUDREBORN_PORT` | Telnet port (the first command-line argument wins) | `2323` |
| `CWGAMING_BBS_POSTGRES_CONNECTION` | BBS accounts and settings database | the game connection, with database `cwgaming_bbs` |
| `MMUDREBORN_POSTGRES_CONNECTION` | The door's game database | `Host=localhost;Port=5432;Database=mmudreborn;Username=mmudreborn;Password=mmudreborn` |
| `MMUDREBORN_POSTGRES_ADMIN_CONNECTION` | A role with CREATE DATABASE, used only if the configured user can't create the databases | none |
| `CWGAMING_BBS_API_URL` | Where the account API listens | `http://127.0.0.1:5097/` |
| `CWGAMING_BBS_API_KEY` | Shared key for the account API | `local-dev-bbs-api-key` |
| `CWGAMING_DOOR_ASSEMBLIES` | Door assemblies to load, separated by the platform path separator | none |

For anything reachable from outside your machine, use your own database credentials, set
`CWGAMING_BBS_API_KEY` to a long random value, and keep the API on a loopback address.

## The first sysop

A fresh install has no sysops. Register an account through telnet, then mark it as a board
sysop directly in the BBS database:

```sql
UPDATE bbs.Users SET IsSysop = 1 WHERE UserName = 'YourName';
```

After that, use `;access` from inside the board to grant or revoke sysop for others.
Game-side sysop rights are separate and belong to the door; see its own documentation.

## Real client IP addresses

IP bans and the per-IP connection cap only work when the host sees each client's real
address. Behind NAT, every client appears to come from the gateway instead. WSL2's default
networking is one example; switch WSL2 to mirrored networking to fix it.

## Licence

[MIT](LICENSE). You're free to use, change and redistribute this software, including
commercially, as long as the copyright notice and licence text stay with every copy or
substantial portion of it.
