# Production-like test deployment (Mother / Child)

Last updated: 2026-11-17. Tested source: `26f1c15` (`feat(machines): report hardware profile and share fleet Sunshine credential`) on `master`.

This runbook walks a **real-machine test**: one Mother PC runs the Controller + web, one or more Child PCs run the Agent. It starts from `git clone` on the Mother and ends with a pending machine approved in the admin UI commands. For a single copy-paste command sheet in execution order, see [production-commands.md](production-commands.md); database migrate/generate/backup commands are in [database-operations.md](database-operations.md). Commands assume Windows PowerShell (run as Administrator where noted) and placeholder LAN addresses:

- Mother `192.168.1.10` (also give it a DNS name, e.g. `kamen-prod`, optional)
- Child `192.168.1.21`, `192.168.1.22`, …

> Security: this is a **test** deployment. Do not reuse production secrets. Keep `.env.local`, certificate passwords and the Sunshine credential in a protected location, never in Git.

---

## 0) What this test can and cannot prove (read first)

| Area | Expected result |
| --- | --- |
| Agent install, enrollment, admin approval, hostname/specs, Sunshine credential delivery, VNC setup, web/Controller wiring | **Can be verified** |
| Actual rental / Moonlight pairing / streaming / public connect address | **Blocked** — default boot/restoration/desktop-health evidence providers are unavailable, so machines stay `notReady` and `canRent=false`. The public-IP connect address is only returned while a lease is active, so it cannot be observed yet. |
| Controller restart mid-test | After restarting an **existing** database the Controller starts in global restore quarantine (sync returns 409, admin actions rejected). A **fresh database path** (no `controller.sqlite`, no sidecar) starts outside quarantine. Plan tests around this: either keep the Controller running for the whole window, or start each experiment with a fresh DB path. |
| Agent upgrade/update | The update path is gated (`AGENT_UPDATE_ENABLED=false`, `AgentReleaseRemediationEnabled=false`). This test uses a **fresh install** only. Re-running the installer on an existing install goes into `repair-enrollment` mode and does **not** replace binaries. |
| Sunshine credential | Fleet-wide value is stored by the Controller and delivered to Agents only (Agent DPAPI store). It does **not** change Sunshine's own configuration. Sunshine's management username/password on each Child must be set to the same value separately (section 11.2). |
| Controller as a Windows service | The packaged installer deliberately refuses `Role Controller`. For this test run the Controller from a launcher/Task Scheduler (sections 8–9). |
| Internet streaming | The connect address uses the shared WAN public IP plus each machine's Moonlight port (section 11.2 / 13). Port mapping must be designed per machine. |

Also keep Mother and Child clocks within ±60 seconds (signed requests have a short window). Enable Windows Time sync.

---

## 1) Architecture / ports

| Component | Host | Listener | Notes |
| --- | --- | --- | --- |
| Controller internal HTTP | Mother | `127.0.0.1:5081` | loopback only; used by web with `X-Service-Credential` |
| Controller Agent HTTPS | Mother | `0.0.0.0:7443` | Agent sync/enrollment/credential; needs a TLS cert whose SAN includes `192.168.1.10` |
| Web (Next.js production) | Mother | `127.0.0.1:3000` | behind HTTPS reverse proxy |
| Caddy reverse proxy | Mother | `443` (LAN) | provides HTTPS for the web origin |
| Agent / Stream Guard | Child | – | outbound to Mother `443` + `7443` |
| TightVNC | Child | `5900` | inbound only from the Controller LAN IP (firewall installed by Agent) |
| Sunshine GameStream | Child | `47984/47989/47990` + UDP range | only needed for real Moonlight tests |

Mother firewall must allow inbound TCP `443` and `7443` on the LAN profile. Do not expose `3000`/`5081`.

---

## 2) Get the code onto the Mother PC

The repository is `https://github.com/Kirito3518/KamenRider.git`. `master` already contains the tested commit (verify after clone).

If the repo is private, use a PAT or SSH key instead of an interactive password:

```powershell
# Option A: HTTPS with a fine-grained read-only PAT (do not save the PAT in scripts)
git clone https://<PAT>@github.com/Kirito3518/KamenRider.git C:\KamenRaider\src

# Option B: SSH
git clone git@github.com:Kirito3518/KamenRider.git C:\KamenRaider\src

Set-Location C:\KamenRaider\src
git log -1 --oneline
# expected first commit line: 26f1c15 feat(machines): report hardware profile and share fleet Sunshine credential
```

If `git log -1` is not `26f1c15` (or newer), pull again before continuing.

---

## 3) Mother prerequisites

Install (x64):

- Windows 10/11 with an Administrator account
- **.NET 10 SDK** (10.0.3xx; the repo targets `net10.0`)
- **Node.js 24 LTS** (x64) and **pnpm 11.8.0** (matches `packageManager`; pnpm 12.x also worked in the dev environment)
- **Git for Windows**
- **Caddy** (HTTPS reverse proxy) — download `caddy_windows_amd64.exe` and copy to `C:\KamenRaider\caddy\caddy.exe`
- Internet access for `dotnet restore`, `pnpm install`, Caddy CA first run and `https://api.ipify.org`

```powershell
node --version              # v24.x
npm install -g pnpm@11.8.0
pnpm --version
dotnet --version            # 10.0.3xx
git --version
```

Static LAN IP on the Mother (`192.168.1.10`), and reserve it in the router. Child PCs should also be static/reserved (`192.168.1.21+`).

---

## 4) Build the three artifacts

```powershell
Set-Location C:\KamenRaider\src

# 4.1 Controller (framework-dependent; needs the installed .NET 10 runtime/SDK)
dotnet publish services\Controller\Controller.csproj -c Release -o C:\KamenRaider\Controller

# 4.2 Web
pnpm install --frozen-lockfile
pnpm build

# 4.3 Agent package (self-contained win-x64) written to public\agent\
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\agent\Build-AgentPackage.ps1
Get-Content public\agent\AgentPackage.sha256
```

- The Agent package file is intentionally gitignored, so it must be built on the Mother.
- Save the printed SHA-256; it becomes `AGENT_PACKAGE_SHA256`.
- `public\agent\Bootstrap-Agent.ps1` comes from Git; the install command hash-pins it at request time.

---

## 5) TLS certificates and trust

The setup needs two HTTPS endpoints: the **Controller Agent listener** (7443) and the **web origin** (443). Both certificates must be trusted by every Child PC.

### 5.1 Controller certificate (self-signed is fine for the test)

Run in an **elevated** PowerShell on the Mother. Replace `$ip` with the Mother LAN IP.

```powershell
New-Item -ItemType Directory -Force C:\KamenRaider\certs | Out-Null
$ip  = '192.168.1.10'
$dns = 'kamen-prod'
$pfxPassword = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 32 | ForEach-Object {[char]$_})

$cert = New-SelfSignedCertificate `
  -Type Custom -Subject "CN=$dns" `
  -CertStoreLocation 'Cert:\LocalMachine\My' `
  -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
  -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) `
  -TextExtension @(
    "2.5.29.17={text}DNS=$dns&IPAddress=$ip",
    "2.5.29.37={text}1.3.6.1.5.5.7.3.1"
  )

Export-PfxCertificate -Cert $cert -FilePath C:\KamenRaider\certs\controller.pfx `
  -Password (ConvertTo-SecureString $pfxPassword -AsPlainText -Force) | Out-Null
Export-Certificate -Cert $cert -FilePath C:\KamenRaider\certs\controller-root.cer | Out-Null

"THUMBPRINT=$($cert.Thumbprint)"
"PFX_PASSWORD=$pfxPassword"
```

Save the thumbprint into `AGENT_TRUST_ROOT_THUMBPRINT` and the PFX password into the Controller launcher. Restrict the cert folder:

```powershell
icacls C:\KamenRaider\certs /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "$env:USERDOMAIN\$env:USERNAME:(OI)(CI)F"
```

### 5.2 Web HTTPS with Caddy (LAN + internal CA)

Create `C:\KamenRaider\caddy\Caddyfile`:

```caddyfile
https://192.168.1.10 {
    tls internal
    reverse_proxy 127.0.0.1:3000
}
```

Start Caddy once; it creates a local CA. Export its root for the Child PCs:

```powershell
C:\KamenRaider\caddy\caddy.exe validate --config C:\KamenRaider\caddy\Caddyfile
Start-Process C:\KamenRaider\caddy\caddy.exe -ArgumentList 'run','--config','C:\KamenRaider\caddy\Caddyfile'

# After Caddy has run once, find and copy its root CA
$root = Get-ChildItem "$env:APPDATA\Caddy" -Recurse -Filter root.crt | Select-Object -First 1
Copy-Item $root.FullName C:\KamenRaider\certs\caddy-root.crt -Force
```

> Alternative with a public domain: point DNS to the Mother and use `kamen.example.com { reverse_proxy 127.0.0.1:3000 }` (Caddy obtains a public certificate automatically if ports 80/443 are reachable). Children then only need the Controller certificate.

### 5.3 Trust on each Child (elevated)

Copy `controller-root.cer` and `caddy-root.crt` to the Child (USB/network share), then:

```powershell
Import-Certificate -FilePath C:\temp\controller-root.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath C:\temp\caddy-root.crt    -CertStoreLocation Cert:\LocalMachine\Root
Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Thumbprint -eq '<THUMBPRINT>' }
```

The Agent installer checks that `AGENT_TRUST_ROOT_THUMBPRINT` exists in `LocalMachine\Root`; it never imports certificates itself.

---

## 6) Folders and ACLs on the Mother

Run in elevated PowerShell:

```powershell
New-Item -ItemType Directory -Force C:\ProgramData\KamenRaider\Controller | Out-Null
New-Item -ItemType Directory -Force C:\ProgramData\KamenRaider\Web        | Out-Null

# The Controller refuses insecure database directories (must have a protected DACL)
icacls C:\ProgramData\KamenRaider\Controller /inheritance:r `
  /grant:r "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "$env:USERDOMAIN\$env:USERNAME:(OI)(CI)F"
icacls C:\ProgramData\KamenRaider\Web /inheritance:r `
  /grant:r "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "$env:USERDOMAIN\$env:USERNAME:(OI)(CI)F"
```

**First-run rule:** leave the directory empty (no `controller.sqlite`, no `controller.sqlite.restore-epoch`). The Controller creates them and starts outside quarantine. Do **not** copy an old DB+sidecar here for a test; if you must migrate one, the sidecar file also needs its own protected DACL, or startup fails with `The restore epoch sidecar owner or DACL is not protected`.

---

## 7) Secrets

Generate once on the Mother:

```powershell
node -e "console.log(require('crypto').randomBytes(48).toString('base64url'))"  # BETTER_AUTH_SECRET
node -e "console.log(require('crypto').randomBytes(48).toString('base64url'))"  # CONTROLLER_SERVICE_CREDENTIAL
node -e "console.log(require('crypto').randomBytes(32).toString('base64'))"     # CONTROLLER_SUNSHINE_CREDENTIAL_KEY
```

---

## 8) Configuration files

### 8.1 `C:\KamenRaider\src\.env.local` (web + install-command settings)

Create this file (gitignored) with real values:

```ini
BETTER_AUTH_SECRET=<48-byte base64url>
BETTER_AUTH_URL=https://192.168.1.10
AUTH_DATABASE_PATH=C:\ProgramData\KamenRaider\Web\auth.sqlite

GOOGLE_CLIENT_ID=
GOOGLE_CLIENT_SECRET=
DISCORD_CLIENT_ID=
DISCORD_CLIENT_SECRET=

CONTROLLER_INTERNAL_URL=http://127.0.0.1:5081
CONTROLLER_SERVICE_CREDENTIAL=<48-byte base64url>
CONTROLLER_AGENT_URL=https://192.168.1.10:7443
CONTROLLER_DATABASE_PATH=C:\ProgramData\KamenRaider\Controller\controller.sqlite

# Public IP for the renter connect address (polled every 5 minutes by the Controller)
CONTROLLER_PUBLIC_IP_ENDPOINT=https://api.ipify.org

CONTROLLER_SUNSHINE_CREDENTIAL_KEY=<base64 of 32 bytes>

AGENT_INSTALL_ENABLED=true
AGENT_INSTALL_BASE_URL=https://192.168.1.10
AGENT_PACKAGE_URL=https://192.168.1.10/agent/AgentPackage.zip
AGENT_PACKAGE_SHA256=<from public\agent\AgentPackage.sha256>
AGENT_CONTROLLER_URL=https://192.168.1.10:7443
AGENT_TRUST_ROOT_THUMBPRINT=<Controller certificate thumbprint>

# Optional: only needed when AGENT_CONTROLLER_URL uses a hostname instead of a private IPv4 literal.
# With https://192.168.1.10 the VNC source address is derived automatically.
AGENT_VNC_ALLOWED_SOURCE_IP=

# Optional Wake-on-LAN (Mother NIC + /24 broadcast)
CONTROLLER_WAKE_INTERFACE_ADDRESS=192.168.1.10
CONTROLLER_WAKE_BROADCAST_ADDRESS=192.168.1.255

# Keep the update path closed for this test
AGENT_UPDATE_ENABLED=false
```

Notes:

- `AGENT_INSTALL_BASE_URL` / `AGENT_PACKAGE_URL` / `AGENT_CONTROLLER_URL` must be HTTPS when the Child is remote; the web route rejects plain HTTP LAN URLs.
- `BETTER_AUTH_URL` must equal the browser origin exactly (scheme + host + port), otherwise login/admin requests fail.
- The admin install command is only shown when the package hash, controller URL and trust thumbprint are all configured.

### 8.2 `C:\KamenRaider\controller.cmd` (Controller process environment)

The Controller reads configuration from environment variables (it is not started through `Run-Controller-Local.ps1` because that launcher is restricted to the localhost dev endpoints):

```bat
@echo off
set "InternalUrl=http://127.0.0.1:5081"
set "AgentUrl=https://192.168.1.10:7443"
set "DatabasePath=C:\ProgramData\KamenRaider\Controller\controller.sqlite"
set "ServiceCredential=<same as CONTROLLER_SERVICE_CREDENTIAL>"
set "PublicIpEndpoint=https://api.ipify.org"
set "SunshineCredentialKey=<same as CONTROLLER_SUNSHINE_CREDENTIAL_KEY>"
set "Kestrel__Certificates__Default__Path=C:\KamenRaider\certs\controller.pfx"
set "Kestrel__Certificates__Default__Password=<PFX password>"
set "DOTNET_ENVIRONMENT=Production"
set "ASPNETCORE_ENVIRONMENT=Production"
cd /d C:\KamenRaider\Controller
Controller.exe
```

### 8.3 `C:\KamenRaider\web.cmd`

```bat
@echo off
cd /d C:\KamenRaider\src
set "NODE_ENV=production"
pnpm start
```

---

## 9) Start and verify (order matters)

### 9.1 Controller (fresh database)

```powershell
Start-Process cmd.exe -ArgumentList '/c C:\KamenRaider\controller.cmd' -Verb RunAs
```

Expected log lines: `Now listening on: http://127.0.0.1:5081`, `Now listening on: https://192.168.1.10:7443` (or `[::]:7443`), `Hosting environment: Production`, and **no** restore-quarantine line on a fresh DB.

Verify:

```powershell
$cred = '<CONTROLLER_SERVICE_CREDENTIAL>'
(Invoke-WebRequest http://127.0.0.1:5081/internal/health -Headers @{ 'X-Service-Credential' = $cred } -UseBasicParsing).Content
# {"protocolVersion":1,"status":"ok"}
```

### 9.2 Web

```powershell
Start-Process cmd.exe -ArgumentList '/c C:\KamenRaider\web.cmd'
```

Verify `http://127.0.0.1:3000` returns the site, and `https://192.168.1.10` returns it through Caddy.

### 9.3 Firewall

```powershell
New-NetFirewallRule -DisplayName 'KamenRaider Controller 7443' -Direction Inbound -Protocol TCP -LocalPort 7443 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName 'KamenRaider Web 443'         -Direction Inbound -Protocol TCP -LocalPort 443  -Action Allow -Profile Private
```

### 9.4 Optional: run at startup

For a test box you can keep the two windows open. If you want them to survive reboot, point Task Scheduler "At startup" tasks at `controller.cmd`, `web.cmd` and `caddy.exe run --config C:\KamenRaider\caddy\Caddyfile`. Remember that restarting the Controller with the **existing** DB re-enters quarantine (section 0).

---

## 10) First admin and fleet Sunshine credential

1. Open `https://192.168.1.10/login` and register the account you will use (Better Auth creates `auth.sqlite` on first request).
2. Promote it to admin **once** (the app never self-promotes):

```powershell
Set-Location C:\KamenRaider\src
node -e "const Database=require('better-sqlite3');const db=new Database(process.argv[1]);const r=db.prepare(\"UPDATE user SET role='admin' WHERE email=?\").run(process.argv[2]);console.log('changed',r.changes);console.log(db.prepare('SELECT email,role FROM user').all());" "C:\ProgramData\KamenRaider\Web\auth.sqlite" "you@example.com"
```

3. Sign out/in. Open **`/admin/settings/sunshine`** and save the fleet Sunshine username + password (same value you will put into each Child's Sunshine, section 11.2). The page shows revision/updatedAt and never displays the password again. Minimum length is 8 characters.
4. Optionally check `/admin/machines` and `/admin/agent-releases`.

---

## 11) Child PC setup (per machine)

### 11.1 OS / network

- Windows 10/11, static LAN IP reserved (`192.168.1.21`…), LAN profile.
- Enable Wake-on-LAN in BIOS + NIC if you want the admin wake button to work later.
- Install **Sunshine** first (the Agent package prepares its service policy and expects the installation to exist). Keep it at its default stopped/manual state afterwards.
- The Agent package is self-contained: no .NET runtime is required on the Child.

### 11.2 Sunshine management credential and TLS (per Child, elevated)

The fleet credential feature only manages the **Agent-side** credential. Sunshine's own web-manager credential must be set to the **same** username/password, and the Agent requires a TLS certificate that validates for `https://localhost:47990`.

```powershell
$fleetUser = 'admin'            # same as the web setting
$fleetPass = '<fleet password>' # same as the web setting

# 1) Set Sunshine's own management credential (Sunshine must be stopped)
& 'C:\Program Files\Sunshine\sunshine.exe' --creds $fleetUser $fleetPass

# 2) Provision a Windows-trusted localhost certificate for Sunshine (helper from this repo)
#    Build once on the Mother:
#    dotnet publish scripts\agent\dev\SunshineCredentialReset -c Release -o C:\KamenRaider\SunshineTls
#    Copy C:\KamenRaider\SunshineTls to the Child, then run elevated:
C:\KamenRaider\SunshineTls\SunshineCredentialReset.exe --provision-tls `
  --sunshine-root 'C:\Program Files\Sunshine' `
  --result-file 'C:\ProgramData\KamenRaider\SunshineSetup\tls-result.json'
```

`--provision-tls` writes `cacert.pem` + `cakey.pem` into `C:\Program Files\Sunshine\config\credentials\` and imports the certificate into the local machine root store. Do **not** run `scripts\agent\dev\Reset-SunshineCredentials.ps1` for the fleet test: it generates a random password per machine (not the shared fleet value).

### 11.3 Trust the Mother certificates

```powershell
Import-Certificate -FilePath C:\temp\controller-root.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath C:\temp\caddy-root.crt    -CertStoreLocation Cert:\LocalMachine\Root
```

### 11.4 Run the Agent install command

1. On the Mother, sign in as admin and open `/admin/machines`.
2. Press the button that generates a fresh **Agent install command** (24-hour, single-use ticket).
3. Copy the whole command into **elevated PowerShell on the Child** and run it.
   - It downloads `Bootstrap-Agent.ps1` + the package over HTTPS (Caddy origin), verifies the script hash, package SHA-256 and Controller trust thumbprint, installs `KamenRaider Stream Guard` and `KamenRaider Agent`, starts them, and waits up to 90 s for enrollment.
4. The machine appears as **pending** in `/admin/machines`.

If the command fails after installing service/binaries, do **not** immediately rerun the same installer with a different package; read the error, and use a **freshly generated command on the same PC** for enrollment-ticket repair only.

### 11.5 Approve the machine

1. On the Child (elevated), read the fingerprint and verify it matches what you approved:

```powershell
Get-Content "$env:ProgramData\KamenRaider\Agent\enrollment-receipt.json"
# PublicKeyFingerprint is the 64-char value to type into the approval box
```

2. On `/admin/machines`, fill in name (leave it empty once to let the Agent hostname become the display name), price in **satang** (e.g. `2000` = 20 THB/hour), Moonlight/Sunshine ports, and the confirmed VNC LAN IP, then **Approve** by typing the fingerprint.
3. The Agent receives the configuration revision on its next sync. The machine now shows hostname/CPU/GPU/RAM and the applied Sunshine credential revision.

> Ports set in the admin UI are metadata + Agent configuration; they do not reconfigure Sunshine or the router by themselves. Keep the values equal to the actual Sunshine/Moonlight setup.

### 11.6 Optional: VNC

- TightVNC is installed by the Agent package and only accepts the Controller LAN IP (`AGENT_VNC_ALLOWED_SOURCE_IP`, or derived from `AGENT_CONTROLLER_URL`).
- Save the machine's LAN IP as the admin-confirmed VNC target, run the probe, then use the read-only viewer in `/admin/pc-control`.
- The VNC password is stored on the Child at `%ProgramData%\KamenRaider\Agent\RemoteDesktop\vnc-password.txt` (elevated). Type it in the viewer prompt; never paste it into chat/tickets.

---

## 12) Verification checklist

| Check | Expected |
| --- | --- |
| `Controller /internal/health` with service credential | `{"protocolVersion":1,"status":"ok"}` |
| Controller log, fresh DB | listening lines, no restore-quarantine line |
| `https://192.168.1.10` | web loads (trusted cert in the browser) |
| Register + promote admin + sign in | `/admin/*` accessible |
| `/admin/settings/sunshine` save | revision increments; password not shown again |
| Child: `Get-Service 'KamenRaider Agent','KamenRaider Stream Guard'` | Running/Automatic |
| Child: `Get-Service tvnserver` | Running/Automatic (installed by Agent package) |
| Child: `Get-Service SunshineService` | Stopped/Manual (Stream Guard owns its lifecycle) |
| `/admin/machines` | machine pending → approved, fingerprint verified |
| After first sync | hostname filled, CPU/GPU/RAM populated, `appliedSunshineCredentialRevision` = fleet revision |
| Machine readiness | stays `notReady` (expected, see section 0) |
| `/admin/pc-control` | shows machine card; wake/VNC actions require valid network/firewall and readiness constraints |

---

## 13) Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `Untrusted principal 'S-…' has write or replacement rights on Controller path component …` | An ancestor directory has an untrusted write ACE (installer/sandbox leftovers). Use a clean database directory with a protected DACL (section 6), or remove the stray ACE from the offending path. |
| `The Controller database directory must have a protected DACL …` | Run the `icacls /inheritance:r …` command in section 6 on the **database directory** itself. |
| `The restore epoch sidecar owner or DACL is not protected` | You copied an old DB file/sidecar. Start from an empty directory (fresh install) or protect the sidecar file ACL as well. |
| Agent sync returns 409 / machine `unreachable` | Controller restarted with an existing DB → global restore quarantine (expected). Or the machine is not approved. |
| Web says install command unavailable | `.env.local` missing one of: `AGENT_INSTALL_ENABLED=true`, HTTPS base/package URL, 64-hex package hash, 40-hex trust thumbprint, private VNC source/matching Controller IP. |
| Installer: certificate not found | Import `controller-root.cer` into `LocalMachine\Root`; the thumbprint in `.env.local` must match exactly. |
| Installer download fails | Child cannot reach `https://192.168.1.10`; import Caddy root CA, open firewall 443, verify DNS/IP. |
| Agent rejects HTTPS to Controller | Controller cert SAN must include the host/IP used in `AGENT_CONTROLLER_URL`; re-issue the cert or use an IP SAN. |
| VNC viewer fails | Machine approved + heartbeat ≤20 s + saved VNC target + TightVNC running + Controller IP allowed by the Child firewall rule. |
| Signed request 401 | Mother/Child clock skew > ±60 s (enable Windows Time), or identity was re-installed with a new key. |
| Public connect address missing | Only returned for an **active** lease; rentals are blocked until readiness evidence exists (section 0). |

---

## 14) Day-2 (pull updates, restart, backups)

```powershell
Set-Location C:\KamenRaider\src
git pull --ff-only
pnpm install --frozen-lockfile
pnpm build
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\agent\Build-AgentPackage.ps1
# update AGENT_PACKAGE_SHA256 in .env.local from public\agent\AgentPackage.sha256
dotnet publish services\Controller\Controller.csproj -c Release -o C:\KamenRaider\Controller
# restart Controller + web (remember the quarantine consequence on existing DB)
```

Backups while the Controller is running:

```powershell
& C:\KamenRaider\Controller\Controller.exe --backup-database `
  C:\ProgramData\KamenRaider\Controller\controller.sqlite `
  D:\KamenRaiderBackups\controller-$(Get-Date -Format yyyyMMddTHHmmssZ).db
```

Keep the `.agent-releases` sidecar directory produced next to the backup with the DB file. No payment/billing integration exists in this test scope; do not expose this build to real customers.
