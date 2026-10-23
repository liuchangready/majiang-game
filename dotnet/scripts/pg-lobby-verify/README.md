# P5.3 LobbyService PostgreSQL verification scripts

Disposable, loopback-only PostgreSQL verification for the accepted P5.3 lobby
store (`PostgresLobbyStore`, Npgsql). These scripts are test infrastructure:
they do not change the lobby application, its migration, or the V4 production
baseline.

## Safety model

- Official EDB PostgreSQL 16.10 Windows binaries, downloaded over HTTPS with a
  pinned SHA-256 (`EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C`).
- The cluster root must be outside the git worktree and is owned through
  `pg-owner.json` (absolute path + owner + port). Cleanup refuses any other path.
- `listen_addresses = '127.0.0.1'` only, a dedicated non-default port (5432 and
  5433 are refused), and a dedicated data directory created by the script.
- The database name must start with `mahjong_lobby_test`.
- The database carries a `lobby_verify.meta` ownership row. The tests refuse to
  write or truncate a database without that marker and (when
  `MAHJONG_LOBBY_PG_ROOT` is set by the runner) with a different root path.
- No password is used or printed: host auth is `trust` only for this throwaway
  loopback cluster. If a password is ever put in the connection string, the
  scripts do not log the connection string.
- Nothing is pushed, merged, or written inside the main worktree.

## Quick start (from the repository root)

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  dotnet\scripts\pg-lobby-verify\verify-all.ps1 `
  -Root D:\majiang-pg-tmp\p53-pg `
  -Port 55432 `
  -DbName mahjong_lobby_test `
  -ZipPath D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip
```

Omit `-ZipPath` to download the pinned official archive. `verify-all.ps1` runs:
start -> migration twice -> build -> full suite -> restart-write -> immediate
crash restart -> restart-read -> durable snapshot -> archive evidence -> delete
the owned cluster. It exits non-zero on any failure, any unexpected skip, or a
cleanup failure.

## Individual steps

```powershell
start-postgres.ps1  -Root <root> -Port 55432 -DbName mahjong_lobby_test [-ZipPath <zip>]
migrate.ps1         -Root <root> -Port 55432 -DbName mahjong_lobby_test -Times 2
run-tests.ps1       -Mode full|write|read|class -Root <root> -Port 55432 -DbName mahjong_lobby_test
restart-postgres.ps1 -Mode immediate|fast|smart -Root <root> -Port 55432 -DbName mahjong_lobby_test
cleanup-postgres.ps1 -Root <root> -Port 55432 -DbName mahjong_lobby_test [-WhatIf]
```

`run-tests.ps1` sets the connection, evidence, handoff and restart-phase values
only in the child-process environment. It fails on any skip unless `-AllowSkips`
is explicitly passed (the full-suite step uses this for the two restart phases,
which are then executed separately in steps 5 and 7).

## Environment variables (set by the runner)

| Variable | Purpose |
|---|---|
| `MAHJONG_LOBBY_TEST_POSTGRES` | Loopback test connection string |
| `MAHJONG_LOBBY_PG_EVIDENCE` | Append-only JSONL evidence path |
| `MAHJONG_LOBBY_PG_HANDOFF` | Cross-process restart handoff file |
| `MAHJONG_LOBBY_PG_RESTART_PHASE` | `write` or `read` restart phase |
| `MAHJONG_LOBBY_PG_ROOT` | Expected disposable cluster root, checked against `lobby_verify.meta` |

## Evidence

Each run archives the raw transcript, build/test logs, summaries, PostgreSQL log,
recovery lines, restart handoff and `postgres-evidence.jsonl` next to the cluster
root (`<parent>\p53-pg-evidence-<timestamp>`), then deletes the cluster itself.
