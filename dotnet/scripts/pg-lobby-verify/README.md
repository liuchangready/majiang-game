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
- Any listening instance is identified with a read-only
  `current_setting('data_directory')` query before a database is created,
  migrated, marked or stopped. The normalized absolute data directory must equal
  the context's data directory; a port collision with another instance is
  refused even when a local `data/PG_VERSION` exists. No marker is written to
  the foreign instance.
- The database name must start with `mahjong_lobby_test`.
- The database carries exactly one `lobby_verify.meta` ownership row. The tests
  refuse to write or truncate a database without that marker, with a different
  owner/root, or with more than one row. Existing markers are validated, never
  overwritten; a database is created only on a freshly started, verified data
  directory.
- `cleanup-postgres.ps1 -WhatIf` performs the read-only checks and returns
  before either `pg_ctl stop` or deletion.
- No password is used or printed: host auth is `trust` only for this throwaway
  loopback cluster. If a password is ever put in the connection string, the
  scripts do not log the connection string.
- Nothing is pushed, merged, or written inside the main worktree.

## Quick start (from the repository root)

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  dotnet\scripts\pg-lobby-verify\verify-all.ps1 `
  -Root D:\majiang-pg-tmp\p53-pg-r3 `
  -Port 55442 `
  -DbName mahjong_lobby_test `
  -ZipPath D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip
```

Omit `-ZipPath` to download the pinned official archive. `verify-all.ps1` runs:
start -> migration twice -> build test project + probe -> full suite (zero
skips) -> restart-write probe -> immediate crash restart -> restart-read probe ->
durable snapshot -> archive evidence -> delete the owned cluster. It exits
non-zero on any failure, any test skip, or a cleanup failure.

The ordinary Lobby test assembly contains **no phase-only tests**. The
cross-process restart path is a separate console helper
(`Mahjong.LobbyService.PostgresProbe`, not in `Mahjong.sln`, not a test project)
invoked explicitly by `verify-all.ps1`. Its build and process phases are not
counted as xUnit test cases.

## Individual steps

```powershell
start-postgres.ps1  -Root <root> -Port 55432 -DbName mahjong_lobby_test [-ZipPath <zip>]
migrate.ps1         -Root <root> -Port 55432 -DbName mahjong_lobby_test -Times 2
run-tests.ps1       -Mode full|class -Root <root> -Port 55432 -DbName mahjong_lobby_test
run-probe.ps1       -Phase write|read -Root <root> -Port 55432 -DbName mahjong_lobby_test
restart-postgres.ps1 -Mode immediate|fast|smart -Root <root> -Port 55432 -DbName mahjong_lobby_test
cleanup-postgres.ps1 -Root <root> -Port 55432 -DbName mahjong_lobby_test [-WhatIf]
```

`run-tests.ps1` sets the connection, evidence, root and data-directory values
only in the child-process environment. It always fails on any skip; there is no
skip bypass. `run-probe.ps1` runs one write/read phase of the standalone console
probe and fails unless the child exits 0 and prints its PASS sentinel. The
runner itself is what writes the handoff and performs a real immediate restart
between the two probe processes.

## Negative verification

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  dotnet\scripts\pg-lobby-verify\negative-checks.ps1 `
  -Root D:\majiang-pg-tmp\p53-pg-negative-r3 `
  -PortA 55444 `
  -DbA mahjong_lobby_test_neg_a -DbB mahjong_lobby_test_neg_b `
  -ZipPath D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip
```

The script creates both clusters itself. B is initialized and stopped first on
the shared port; A then takes that port. `start`, `migrate`, `run-tests`,
`restart` and `cleanup` are all invoked with B's root but A's port, so B's local
port marker is valid and the refusal must come from the server data-directory
check. It re-checks A's postmaster start time, PID, database list and marker, and
B's marker/PG_VERSION hashes after every refusal; it then checks that
`cleanup -WhatIf` leaves an active A's postmaster, PID, port and files intact
before the normal cleanup. Exit code 0 means all 13 negative checks passed.

## Unified gate

With the disposable PG and all three environment variables configured, the
existing `tools/verify-v5.ps1 -Configuration Release -RunnerMode dotnetTest`
must report `Mahjong.LobbyService.Tests` as **89 total / 0 failed / 0 skipped**
and overall PASS. The console probe is auto-discovered as a build-only helper
and contributes no test cases. Do not modify `tools/verify-v5.ps1` or the
workflow to accommodate this harness.

## Environment variables (set by the runner)

| Variable | Purpose |
|---|---|
| `MAHJONG_LOBBY_TEST_POSTGRES` | Loopback test connection string |
| `MAHJONG_LOBBY_PG_EVIDENCE` | Append-only JSONL evidence path |
| `MAHJONG_LOBBY_PG_HANDOFF` | Cross-process restart handoff file (console probe only) |
| `MAHJONG_LOBBY_PG_ROOT` | Expected disposable cluster root, checked against `lobby_verify.meta` |
| `MAHJONG_LOBBY_PG_DATA` | Expected server `data_directory` (must equal `<root>\data`) |

## Evidence

Each run archives the raw transcript, build/test logs, summaries, PostgreSQL log,
recovery lines, restart handoff and `postgres-evidence.jsonl` next to the cluster
root (`<parent>\p53-pg-evidence-<timestamp>`), then deletes the cluster itself.
