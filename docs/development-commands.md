# Development commands (this workstation)

Copy-paste commands for the **development** setup: the dev Controller (`Run-Controller-Local.ps1`), the Next.js dev server, rebuilding the Agent package, and checking the live state.

For the real-machine Mother/Child test use [production-commands.md](production-commands.md) and [production-test-deployment.md](production-test-deployment.md) instead. The production Mother does **not** use `Run-Controller-Local.ps1` โ€” that launcher is intentionally restricted to the localhost dev endpoints.

Database migration, fresh-database generation and backups: [database-operations.md](database-operations.md).

> Run blocks in one PowerShell window so variables stay defined. `ELEVATED` = Run as Administrator.

---

## 1. One-shot: bootstrap a fresh clone

After `git clone` + `pnpm install` on a new machine:

```powershell
# USER ยท repo root โ€” fixes .env.local paths, dev HTTPS cert, data ACL,
# archives the bundled Controller DB for a clean v25 start, verifies the package hash
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Bootstrap-DevClone.ps1 -Start
```

Options:

| Flag | Effect |
| --- | --- |
| `-Start` | start the dev Controller and `pnpm dev`, then verify both |
| `-KeepBundledDatabase` | keep the committed `data\controller.sqlite` (Controller starts in restore quarantine) |
| `-ResetAuthDatabase` | archive `data\auth.sqlite*` + `.auth-secret` for a fresh admin account |
| `-TrustCertificate` | also run `dotnet dev-certs https --trust` |
| `-SkipInstall` | do not run `pnpm install` if `node_modules` is missing |
| `-Force` | allow the fresh-database step while 5081/7443/3000 are already listening |
| `-WhatIf` | show what the script would change, without changing anything |

The script never deletes data: archived files go to `.next-local\bootstrap-archive\<timestamp>\`.
By default it starts from a **fresh Controller database** (no restore quarantine) but keeps the
bundled auth database (with the original admin accounts). Read the summary it prints at the end.

## 2. One-shot: restart the whole dev stack

```powershell
# USER ยท repo root
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Restart-DevStack.ps1
```

- Stops whatever listens on `3000` (web), `5081` (Controller loopback HTTP), `7443` (Controller Agent HTTPS).
- Starts the dev Controller (new window) and `pnpm dev` (new window), waits for both, then calls `/internal/health`.
- Switches: `-SkipWeb`, `-SkipController`.
- Script: [scripts/agent/dev/Restart-DevStack.ps1](../scripts/agent/dev/Restart-DevStack.ps1)

> Restarting the Controller on the **existing dev database** starts in restore quarantine (sync returns 409). That is the current design, not a startup failure.

---

## 3. Manual stop / start

```powershell
# USER โ€” see who owns the dev ports
Get-NetTCPConnection -State Listen -LocalPort 5081,7443,3000 -ErrorAction SilentlyContinue |
  Select-Object LocalPort,OwningProcess

# Stop them (repeat per pid you own)
Stop-Process -Id <pid> -Force

# Start the dev Controller (reads .env.local; Development environment)
Set-Location C:\Users\User\Documents\kamenraiderplay
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Run-Controller-Local.ps1

# Start the web dev server (separate window)
Set-Location C:\Users\User\Documents\kamenraiderplay
pnpm dev
```

`Run-Controller-Local.ps1` requires `.env.local` with:

- `CONTROLLER_INTERNAL_URL=http://127.0.0.1:5081`
- `CONTROLLER_AGENT_URL=https://localhost:7443`
- `CONTROLLER_SERVICE_CREDENTIAL`
- `CONTROLLER_DATABASE_PATH` = absolute drive path
- optional `CONTROLLER_WAKE_INTERFACE_ADDRESS` + `CONTROLLER_WAKE_BROADCAST_ADDRESS` (both or neither)
- optional `CONTROLLER_PUBLIC_IP_ENDPOINT`, `CONTROLLER_SUNSHINE_CREDENTIAL_KEY`

It maps those values to the Controller environment, sets `DOTNET_ENVIRONMENT=Development`, and runs `dotnet run --project services\Controller\Controller.csproj`, so **its window shows the Controller log**.

---

## 4. Verify the dev stack

```powershell
# USER โ€” Controller health (service credential from .env.local)
$cred = ((Select-String -LiteralPath .env.local -Pattern '^CONTROLLER_SERVICE_CREDENTIAL=(.*)$').Matches[0].Groups[1].Value).Trim()
(Invoke-WebRequest http://127.0.0.1:5081/internal/health -Headers @{ 'X-Service-Credential' = $cred } -UseBasicParsing).Content

# Machines as the Controller sees them (hostname/specs applied after a new Agent build syncs)
Invoke-RestMethod http://127.0.0.1:5081/internal/machines -Headers @{ 'X-Service-Credential' = $cred } |
  Select-Object machineId,approvalStatus,displayName,hostname,cpu,gpu,ramBytes,readiness,canRent,appliedSunshineCredentialRevision |
  Format-List

# Sunshine credential state (never returns the password)
(Invoke-WebRequest http://127.0.0.1:5081/internal/sunshine-credential -Headers @{ 'X-Service-Credential' = $cred } -UseBasicParsing).Content

# Web
(Invoke-WebRequest http://localhost:3000/ -UseBasicParsing).StatusCode
(Invoke-WebRequest http://localhost:3000/api/machines -UseBasicParsing).Content

# Realtime machine SSE (frames must arrive incrementally; Ctrl+C to stop)
curl.exe -N -H "Accept: text/event-stream" http://localhost:3000/api/machines/stream
```

```powershell
# USER โ€” current SQLite schema version (Python from this tool environment)
& 'C:\Users\User\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe' -c "import sqlite3;c=sqlite3.connect(r'file:C:\Users\User\Documents\kamenraiderplay\data\controller.sqlite?mode=ro',uri=True);print('user_version',c.execute('pragma user_version').fetchone()[0]);print('quarantine',c.execute('select quarantine_active,quarantine_reason from controller_restore_state').fetchall())"
```

Expected on this host after the latest work: `user_version 25`, quarantine active on the existing database, machine `c24afโ€ฆ` pending/unreachable with empty hostname until the installed Agent is upgraded.

---

## 5. Rebuild the Agent package after code changes

```powershell
# USER ยท repo root
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\Build-AgentPackage.ps1
$sha = (Get-Content .\public\agent\AgentPackage.sha256).Trim()
$sha
```

Then update `AGENT_PACKAGE_SHA256` in `.env.local` (it is gitignored) and **restart the web dev server** (Next caches `.env.local` at startup):

```powershell
# USER โ€” replace the hash line without printing the rest of .env.local
$lines = Get-Content .env.local
$new = $lines | ForEach-Object { if ($_ -match '^AGENT_PACKAGE_SHA256=') { "AGENT_PACKAGE_SHA256=$sha" } else { $_ } }
Set-Content .env.local -Value $new -Encoding ASCII
```

Verify the served bytes:

```powershell
$tmp = Join-Path $env:TEMP 'agentpkg-check.zip'
Invoke-WebRequest http://localhost:3000/agent/AgentPackage.zip -OutFile $tmp -UseBasicParsing
(Get-FileHash $tmp -Algorithm SHA256).Hash   # must equal $sha
Remove-Item $tmp -Force
```

---

## 6. Backups and restarts

```powershell
# USER โ€” online backup of the dev database while the Controller is running
& '.\services\Controller\bin\Debug\net10.0\Controller.exe' --backup-database `
  'C:\Users\User\Documents\kamenraiderplay\data\controller.sqlite' `
  ('C:\Users\User\Documents\kamenraiderplay\.next-local\backup\controller-' + (Get-Date -Format 'yyyyMMddTHHmmssZ') + '.db')
```

```powershell
# USER โ€” clean test without quarantine: use a fresh absolute database path in .env.local
# (stop the Controller first). Do not delete the old file if you may want to inspect it.
# CONTROLLER_DATABASE_PATH=C:\Users\User\Documents\kamenraiderplay\data\controller-test-YYYYMMDD.sqlite
# Then start with scripts\agent\dev\Run-Controller-Local.ps1 again.
```

---

## 7. Troubleshooting (dev)

| Symptom | Fix |
| --- | --- |
| `Untrusted principal 'S-โ€ฆ' has write or replacement rights on Controller path component โ€ฆ` | A parent directory has a stray write ACE (sandbox/installer leftovers). Repair the repo root with a protected DACL: `icacls <repo> /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "$env:USERDOMAIN\$env:USERNAME:(OI)(CI)F"` (run as owner). This exact fix was applied to this workspace on 2026-11-17. |
| `The restore epoch sidecar owner or DACL is not protected` | You copied an old DB+sidecar to a new folder. Start from an empty database directory (fresh run) or protect the sidecar file ACL. |
| Controller window closes immediately / `Controller exited with code โ€ฆ` | Read the window: port 5081/7443 already in use, database lock held by another Controller, or the ACL checks above. |
| Web shows old package hash / install command hash mismatch | Restart `pnpm dev` โ€” env is cached at startup. |
| `/api/machines` returns 503 | Controller not running or service credential mismatch between `.env.local` and the Controller environment. |
| Agent sync 409 / machine unreachable | Existing dev DB is in restore quarantine after a Controller restart (expected). |
| HTTPS `https://localhost:7443` fails from PowerShell | Dev Kestrel certificate trust is not set up for the current user; the Agent sync path is the real check. The browser/admin uses `http://localhost:3000`. |
| Builds fail with duplicate `AssemblyInfo` attributes | Stray `obj-*` / `agentapply-isolated` artifacts; `services/Directory.Build.props` excludes them โ€” if a new folder appears, exclude it or remove the artifact folder. |

---

## 8. Logs

- Controller: the `Run-Controller-Local.ps1` window (or redirect its output to a file when starting it).
- Web: the `pnpm dev` window (`GET โ€ฆ 200` lines).
- Agent (installed build on this host): `Get-WinEvent -LogName Application -MaxEvents 100 | Where-Object ProviderName -eq 'Agent' | Select-Object -First 20 TimeCreated,Id,LevelDisplayName,Message | Format-List`
- Background job logs from this session: `.next-local\*.log`.
