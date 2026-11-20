# Database operations — migrate, generate, backup

Commands for the Controller SQLite database (`controller.sqlite`) and the web auth database (`auth.sqlite`), for both the **dev workstation** and the **production-style Mother** setup. See [development-commands.md](development-commands.md) and [production-commands.md](production-commands.md) for starting/stopping the stack.

---

## 1) How migrations work

- The Controller uses plain SQLite with embedded SQL migrations: `services/Controller/Data/Migrations/001-…sql` … `025-lease-ended-at.sql` (current schema **25**; migration 025 adds the nullable `leases.ended_at` effective-end timestamp used for prorated rental refunds).
- There is **no separate migration command**: when the Controller opens a database whose `PRAGMA user_version` is older, it applies every later migration inside a transaction, then validates the canonical schema.
- Older binaries reject a newer schema (e.g. a v24 build cannot open a v25 database), so always migrate with the **new build**.
- Do not edit `PRAGMA user_version` or table definitions by hand. Back up first (section 7).

| Version | Meaning |
| --- | --- |
| 23 | PC Control / command outbox (previous release) |
| 24 | Machine profile (hostname/CPU/GPU/RAM) + fleet Sunshine credential store |
| **25** | Rental lease end instant (`leases.ended_at`) used for prorated refunds |

---

## 2) Where the databases are

| Environment | Controller DB | Auth DB |
| --- | --- | --- |
| Dev workstation | `C:\Users\User\Documents\kamenraiderplay\data\controller.sqlite` | `…\data\auth.sqlite` |
| Mother (production test) | `C:\ProgramData\KamenRaider\Controller\controller.sqlite` | `C:\ProgramData\KamenRaider\Web\auth.sqlite` |

Sidecar files that belong to the Controller DB (keep them together):

- `<db>.restore-epoch` — protected restore/deployment epoch sidecar (must have a protected DACL)
- `<db>.active.lock`, `<db>.maintenance.lock` — activity/maintenance locks (created at runtime)
- `<db>-wal`, `<db>-shm` — SQLite WAL files when present

---

## 3) Check the current schema version

**Stop the Controller first** for an exact value (WAL may hold the newest page). `user_version` is a big-endian 32-bit value at byte offset 60 of the SQLite header, so plain PowerShell can read it:

```powershell
$db = 'C:\Users\User\Documents\kamenraiderplay\data\controller.sqlite'   # or the Mother path
$bytes = [IO.File]::ReadAllBytes($db)
if ($bytes.Length -lt 64) { throw 'Not a SQLite database file.' }
$version = [BitConverter]::ToUInt32([byte[]]@($bytes[63], $bytes[62], $bytes[61], $bytes[60]), 0)
"user_version = $version"   # expected: 25
```

On the dev workstation you can also use the bundled Python:

```powershell
& 'C:\Users\User\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe' -c "import sqlite3;c=sqlite3.connect(r'file:C:\Users\User\Documents\kamenraiderplay\data\controller.sqlite?mode=ro',uri=True);print('user_version',c.execute('pragma user_version').fetchone()[0]);print('quarantine',c.execute('select quarantine_active,quarantine_reason from controller_restore_state').fetchall())"
```

---

## 4) Migrate an existing database (dev workstation)

Migration happens automatically on Controller start; the steps are just *backup → start new build → verify*.

```powershell
# 1) USER — stop the dev Controller (and web) so the database is not open
Get-NetTCPConnection -State Listen -LocalPort 5081,7443,3000 -ErrorAction SilentlyContinue |
  Select-Object LocalPort,OwningProcess
# Stop-Process -Id <pid> -Force

# 2) USER — backup the current database with the NEW build (it can read older schemas)
Set-Location C:\Users\User\Documents\kamenraiderplay
$stamp = Get-Date -Format 'yyyyMMddTHHmmssZ'
& '.\services\Controller\bin\Debug\net10.0\Controller.exe' --backup-database `
  '.\data\controller.sqlite' `
  (".\.next-local\backup\pre-migrate-$stamp.db")

# 3) USER — start the new Controller; the log shows the restore-quarantine line and it migrates to 24
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Restart-DevStack.ps1 -SkipWeb

# 4) USER — verify (stop it again first if you need an exact read)
#    user_version = 25 expected; the existing database starts in restore quarantine (sync 409) by design.
```

> Existing databases always start in **restore quarantine** after the epoch reconciliation. If you need a rentable/syncable test run, use a **fresh database path** (section 5) instead of migrating the old one.

---

## 5) Generate a brand-new Controller database (dev, no quarantine)

A database created at a path with **no existing database and no restore sidecar** starts outside quarantine.

```powershell
# 1) USER — stop the Controller
Get-NetTCPConnection -State Listen -LocalPort 5081,7443 -ErrorAction SilentlyContinue |
  Select-Object LocalPort,OwningProcess
# Stop-Process -Id <pid> -Force

# 2) USER — pick a new path inside the existing protected data directory
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$newDb = "C:\Users\User\Documents\kamenraiderplay\data\controller-$stamp.sqlite"

# 3) USER — point .env.local at it (ASCII, no BOM; keeps a copy of the old file)
Copy-Item .env.local (".\.next-local\env-local-$stamp.bak")
$lines = Get-Content .env.local
$lines = $lines | ForEach-Object {
  if ($_ -match '^CONTROLLER_DATABASE_PATH=') { "CONTROLLER_DATABASE_PATH=$newDb" } else { $_ }
}
Set-Content .env.local -Value $lines -Encoding ASCII

# 4) USER — start the Controller (fresh DB + sidecar are created automatically)
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Restart-DevStack.ps1 -SkipWeb

# 5) USER — verify: user_version 25, quarantine 0
& 'C:\Users\User\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe' -c "import sqlite3,sys;c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True);print('user_version',c.execute('pragma user_version').fetchone()[0]);print('quarantine',c.execute('select quarantine_active,quarantine_reason from controller_restore_state').fetchall())" $newDb
```

After switching paths:

- The fresh database has **no machines**: previously installed Agents belong to the old database and will not sync; generate a new install command from `/admin/machines` and run it on that PC (it will use the repair-enrollment path and replace the ticket).
- To go back, set `CONTROLLER_DATABASE_PATH` back to `data\controller.sqlite` (or restore the `.env.local` backup) and restart the Controller.
- Keep the old `.restore-epoch` sidecar with its database; never mix sidecars between databases.

---

## 6) Generate / reset the web auth database

Better Auth creates `auth.sqlite` lazily on the first auth request; there is no separate generator.

```powershell
# 1) USER — with the web running, trigger creation (or just open http://localhost:3000/login)
Invoke-WebRequest http://localhost:3000/api/auth/get-session -UseBasicParsing | Out-Null
Get-ChildItem .\data\auth.sqlite* | Select-Object Name,Length

# 2) Register the first account in the browser, then promote it to admin (see development-commands.md)
```

Reset all web users/sessions (dev only):

```powershell
# USER — stop the web server first, then archive the auth database
$stamp = Get-Date -Format 'yyyyMMddTHHmmssZ'
New-Item -ItemType Directory -Force .\.next-local\auth-archive | Out-Null
Get-ChildItem .\data\auth.sqlite* | Move-Item -Destination ".\.next-local\auth-archive\$stamp-" -Force
# next auth request recreates an empty auth.sqlite
```

---

## 7) Production Mother: migrate or generate

```powershell
# 1) USER · Mother — stop the Controller process (Task Scheduler job or the console window)
Get-NetTCPConnection -State Listen -LocalPort 5081,7443 -ErrorAction SilentlyContinue |
  Select-Object LocalPort,OwningProcess
# Stop-Process -Id <pid> -Force

# 2) USER · Mother — backup with the new build
& 'C:\KamenRaider\Controller\Controller.exe' --backup-database `
  'C:\ProgramData\KamenRaider\Controller\controller.sqlite' `
  ('D:\KamenRaiderBackups\pre-migrate-' + (Get-Date -Format 'yyyyMMddTHHmmssZ') + '.db')

# 3a) MIGRATE: start the Controller again with the same DB path -> it migrates to 24 and enters quarantine
Start-Process cmd.exe -ArgumentList '/c C:\KamenRaider\controller.cmd'

# 3b) OR GENERATE A FRESH DB (no quarantine): prepare an empty protected directory, then point controller.cmd at it
New-Item -ItemType Directory -Force 'C:\ProgramData\KamenRaider\Controller2' | Out-Null
icacls 'C:\ProgramData\KamenRaider\Controller2' /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "$env:USERDOMAIN\$env:USERNAME:(OI)(CI)F"
# edit controller.cmd: set "DatabasePath=C:\ProgramData\KamenRaider\Controller2\controller.sqlite"
# then start it; the directory must not contain any controller.sqlite / restore-epoch file
```

Restore an existing backup (Controller **must be stopped**; the command refuses while a Controller holds the database lock):

```powershell
& 'C:\KamenRaider\Controller\Controller.exe' --restore-database `
  'D:\KamenRaiderBackups\pre-migrate-YYYYMMDDTHHMMSSZ.db' `
  'C:\ProgramData\KamenRaider\Controller\controller.sqlite' `
  --controller-stopped
```

> Moving a database to another machine/directory: copy the `.restore-epoch` sidecar **with** it and give the sidecar its own protected DACL, otherwise startup fails with `The restore epoch sidecar owner or DACL is not protected`.

---

## 8) Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `SQLITE_BUSY` / `database is locked` | The old Controller (or the web tests) still has the database open. Stop all Controller processes on 5081/7443 first. |
| Startup fails with `Untrusted principal … on Controller path component …` | A parent directory has an untrusted write ACE (sandbox leftovers). Use a clean protected directory, or repair the ACL (see dev troubleshooting). |
| `The restore epoch sidecar owner or DACL is not protected` | The copied sidecar inherited an unprotected ACL. Start from an empty directory (fresh DB) or re-create/protect the sidecar. |
| Version check prints an old number after migration | WAL not checkpointed, or you are reading the wrong path. Stop the Controller, verify the path in `.env.local` / `controller.cmd`, then re-read. |
| Old build cannot start on migrated DB | Expected: v24 binaries reject schema 25. Always run the new build. |
| Sync returns 409 right after a restart | Restore quarantine on an existing database (by design). Use a fresh DB path for rentable test runs. |
| Backup command refused | It validates the database/schema and refuses if an artifact sidecar is missing/corrupt; check `D:\KamenRaiderBackups` exists and is writable. |
| Restore refused | `--controller-stopped` missing, a Controller still holds the lock, or the backup `.agent-releases` sidecar is missing/corrupt. |
