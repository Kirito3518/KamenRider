# Production test — all commands in order

Copy‑paste companion to [production-test-deployment.md](production-test-deployment.md). Every block states **which machine** it runs on and whether it needs an **elevated** PowerShell. Values with `<…>` are placeholders you must replace; the setup block below defines the common ones.

Database migration / fresh-database generation / backup & restore commands: [database-operations.md](database-operations.md). Local development stack commands: [development-commands.md](development-commands.md).

> Run the ordered blocks **in one PowerShell window per machine** so the `$variables` defined above stay available. `.env.local` / launchers are written as **ASCII** on purpose (no UTF‑8 BOM at the start of `BETTER_AUTH_SECRET`). If `winget` is unavailable on the Mother, install Git/.NET 10 SDK/Node 24 manually instead of the winget block.

```powershell
# ---------- EDIT ONLY THIS BLOCK (values used by later commands) ----------
$MotherIp   = '192.168.1.10'        # Mother LAN IP
$MotherDns  = 'kamen-prod'          # optional name; keep DNS for the cert SAN
$RepoUrl    = 'https://github.com/Kirito3518/KamenRider.git'
$Src        = 'C:\KamenRaider\src'
$CtrlOut    = 'C:\KamenRaider\Controller'
$Certs      = 'C:\KamenRaider\certs'
$CaddyDir   = 'C:\KamenRaider\caddy'
$DataRoot   = 'C:\ProgramData\KamenRaider'
$AdminEmail = '<you@example.com>'   # the account you will register in the web UI
$RunnerUser = "$env:USERDOMAIN\$env:USERNAME"   # account that will run the Controller
# ---------------------------------------------------------------------------
```

> `RUN AS` = the shell context: **USER** (normal) or **ELEVATED** (Run as Administrator). Only blocks marked ELEVATED need administrator rights.

---

## A. Dev PC (this machine) — one‑time: publish the code

```powershell
# USER · on the development PC
cd C:\Users\User\Documents\kamenraiderplay
git status --porcelain            # must be clean or only your own WIP
git push origin master
git ls-remote origin master       # must point at 6c729c0 or newer
```

If the push is rejected because the remote moved, pull/rebase normally, then push again.

---

## B. Mother PC — one‑time prerequisites

```powershell
# ELEVATED · Mother — install tools (winget; skip already-installed ones)
winget install --id Git.Git -e
winget install --id Microsoft.DotNet.SDK.10 -e
winget install --id OpenJS.NodeJS.LTS -e
npm install -g pnpm@11.8.0
# Caddy: download caddy_windows_amd64.exe and place it at $CaddyDir\caddy.exe
#   https://caddyserver.com/download  -> windows amd64
New-Item -ItemType Directory -Force $CaddyDir | Out-Null
# copy caddy.exe into $CaddyDir manually, then:
$env:Path += ";$CaddyDir"
git --version; dotnet --version; node --version; pnpm --version; & "$CaddyDir\caddy.exe" version
```

Make `$MotherIp` static (router DHCP reservation) and confirm clocks are synced:

```powershell
# ELEVATED · Mother
w32tm /resync
Get-NetIPAddress -AddressFamily IPv4 | Where-Object InterfaceAlias -notmatch 'Loopback' |
  Select-Object InterfaceAlias,IPAddress
```

---

## C. Mother PC — clone and build

```powershell
# USER · Mother
New-Item -ItemType Directory -Force C:\KamenRaider | Out-Null
git clone $RepoUrl $Src
Set-Location $Src
git log -1 --oneline              # expected: 6c729c0 docs: add production test deployment runbook
```

```powershell
# USER · Mother — build Controller + web + Agent package
Set-Location $Src
dotnet publish .\services\Controller\Controller.csproj -c Release -o $CtrlOut
pnpm install --frozen-lockfile
pnpm build
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\Build-AgentPackage.ps1
$agentSha = (Get-Content "$Src\public\agent\AgentPackage.sha256").Trim()
$agentSha
```

Build the optional Sunshine TLS helper (needed on Child PCs):

```powershell
# USER · Mother
dotnet publish "$Src\scripts\agent\dev\SunshineCredentialReset\SunshineCredentialReset.csproj" `
  -c Release -r win-x64 --self-contained true -o C:\KamenRaider\SunshineTls
# self-contained: the Child PCs do not need a .NET runtime
```

---

## D. Mother PC — TLS certificates

```powershell
# ELEVATED · Mother — Controller certificate (PFX + root CER for the Child PCs)
New-Item -ItemType Directory -Force $Certs | Out-Null
$PfxPassword = -join ((48..57)+(65..90)+(97..122) | Get-Random -Count 32 | ForEach-Object {[char]$_})
$cert = New-SelfSignedCertificate `
  -Type Custom -Subject "CN=$MotherDns" `
  -CertStoreLocation 'Cert:\LocalMachine\My' `
  -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
  -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) `
  -TextExtension @(
    "2.5.29.17={text}DNS=$MotherDns&IPAddress=$MotherIp",
    "2.5.29.37={text}1.3.6.1.5.5.7.3.1"
  )
$ControllerThumbprint = $cert.Thumbprint
Export-PfxCertificate -Cert $cert -FilePath "$Certs\controller.pfx" `
  -Password (ConvertTo-SecureString $PfxPassword -AsPlainText -Force) | Out-Null
Export-Certificate -Cert $cert -FilePath "$Certs\controller-root.cer" | Out-Null
icacls $Certs /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "${RunnerUser}:(OI)(CI)F"
"THUMBPRINT = $ControllerThumbprint"
# Save $PfxPassword and $ControllerThumbprint for section F/G. Do not commit them.
```

```powershell
# USER · Mother — Caddyfile + first run + export Caddy root CA for Child PCs
New-Item -ItemType Directory -Force $CaddyDir | Out-Null
@"
https://$MotherIp {
    tls internal
    reverse_proxy 127.0.0.1:3000
}
"@ | Set-Content "$CaddyDir\Caddyfile" -Encoding ASCII
& "$CaddyDir\caddy.exe" validate --config "$CaddyDir\Caddyfile"
Start-Process "$CaddyDir\caddy.exe" -ArgumentList 'run','--config',"$CaddyDir\Caddyfile"
Start-Sleep -Seconds 5
$caddyRoot = Get-ChildItem "$env:APPDATA\Caddy" -Recurse -Filter root.crt -ErrorAction SilentlyContinue |
  Select-Object -First 1
if (-not $caddyRoot) { throw 'Caddy root CA not found; start Caddy and retry.' }
Copy-Item $caddyRoot.FullName "$Certs\caddy-root.crt" -Force
Import-Certificate -FilePath "$Certs\caddy-root.crt" -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
"CADDY_ROOT = $($caddyRoot.FullName)"
```

### VNC gateway ผ่าน origin เดียวกัน (จำเป็นเมื่อเบราว์เซอร์ต่อ Controller:7443 ตรงไม่ได้)

VNC viewer เปิด WebSocket `wss://.../admin/vnc` ไปที่ Controller โดยตรง ถ้าแอดมินเข้าจากภายนอก LAN (เช่นผ่าน Cloudflare ที่ proxy เฉพาะ 443) ต้องให้ reverse proxy ส่งต่อ path นี้ไป Controller `https://127.0.0.1:7443` แล้วตั้ง `VNC_PUBLIC_WS_URL=wss://<public-host>/admin/vnc`

**วิธีที่แนะนำ (มีในโค้ด ไมต้องติดตั้ง Caddy เพิ่ม):** รันเว็บ production ด้วย custom server `node server.mjs` แทน `next start` — มันเสิร์ฟ Next เหมือนเดิมและ proxy WebSocket `/admin/vnc` ไป `VNC_PROXY_TARGET` (ค่าเริ่มต้น `https://127.0.0.1:7443`, ไม่ตรวจ certificate ของ Controller เพราะ TLS ฝั่ง public ถูก terminate ที่ Cloudflare/Caddy) โดย:

```powershell
# .env.local
VNC_PUBLIC_WS_URL=wss://<public-host>/admin/vnc
VNC_PROXY_TARGET=https://127.0.0.1:7443
# ถ้า certificate ของ Controller ใช้ชื่อ/IP อื่น ให้ระบุ SNI:
# VNC_PROXY_SERVERNAME=192.168.1.100
```

```powershell
# Task/launcher ของเว็บ (working directory = repo)
$env:NODE_ENV='production'; $env:PORT='3000'; node server.mjs
```

Cloudflare Tunnel ยังชี้ `http://127.0.0.1:3000` เหมือนเดิม; ไม่ต้องเปิดพอร์ต 7443 สู่อินเทอร์เน็ต

ทางเลือกถ้าไม่ใช้ custom server: ใช้ Caddy proxy path เองตามตัวอย่างนี้

```caddyfile
https://$MotherIp {
    tls internal

    @vnc path /admin/vnc
    handle @vnc {
        reverse_proxy https://${MotherIp}:7443
    }

    reverse_proxy 127.0.0.1:3000
}
```

- Caddy ต้องเชื่อถือ certificate ของ Controller (import `controller-root.cer`เข้า `LocalMachine\Root` ตามขั้นตอนด้านบน) และต้อง forward WebSocket upgrade + header `Origin` ตามค่าเริ่มต้น
- Cloudflare proxy/Tunnel รองรับ WebSocket บนพอร์ต 443 เท่านั้น จึงใช้วิธี path นี้แทนการเปิดพอร์ต 7443 สู่อินเทอร์เน็ต; ห้ามตั้ง `VNC_PUBLIC_WS_URL` เป็น `wss://<LAN-IP>:7443` ถ้าเบราว์เซอร์อยู่นอก LAN
- ถ้าแอดมินอยู่ใน LAN และเบราว์เซอร์เชื่อถือ cert ของ Controller แล้ว ไม่ต้องตั้ง `VNC_PUBLIC_WS_URL` (ระบบจะ derive จาก `AGENT_CONTROLLER_URL` ตามเดิม)

---

## E. Mother PC — data directories and ACLs

```powershell
# ELEVATED · Mother
New-Item -ItemType Directory -Force "$DataRoot\Controller" | Out-Null
New-Item -ItemType Directory -Force "$DataRoot\Web"        | Out-Null
icacls "$DataRoot\Controller" /inheritance:r /grant:r `
  "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "${RunnerUser}:(OI)(CI)F"
icacls "$DataRoot\Web" /inheritance:r /grant:r `
  "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "${RunnerUser}:(OI)(CI)F"
# First run must start from an EMPTY Controller directory (no controller.sqlite, no sidecar).
Get-ChildItem "$DataRoot\Controller" -Force      # expected: empty
```

---

## F. Mother PC — secrets and configuration files

```powershell
# USER · Mother — generate secrets once and write .env.local + launchers
node -e "console.log(require('crypto').randomBytes(48).toString('base64url'))"   # $AuthSecret
node -e "console.log(require('crypto').randomBytes(48).toString('base64url'))"  # $ServiceCredential
node -e "console.log(require('crypto').randomBytes(32).toString('base64'))"     # $SunshineKey
```

Assign them (replace the placeholders) and write the web env file:

```powershell
# USER · Mother
$AuthSecret        = '<first value>'
$ServiceCredential = '<second value>'
$SunshineKey       = '<third value>'
$PfxPassword       = '<from section D>'
$ControllerThumbprint = '<from section D>'
$agentSha          = (Get-Content "$Src\public\agent\AgentPackage.sha256").Trim()

@"
BETTER_AUTH_SECRET=$AuthSecret
BETTER_AUTH_URL=https://$MotherIp
AUTH_DATABASE_PATH=$DataRoot\Web\auth.sqlite
GOOGLE_CLIENT_ID=
GOOGLE_CLIENT_SECRET=
DISCORD_CLIENT_ID=
DISCORD_CLIENT_SECRET=
CONTROLLER_INTERNAL_URL=http://127.0.0.1:5081
CONTROLLER_SERVICE_CREDENTIAL=$ServiceCredential
CONTROLLER_AGENT_URL=https://${MotherIp}:7443
CONTROLLER_DATABASE_PATH=$DataRoot\Controller\controller.sqlite
CONTROLLER_PUBLIC_IP_ENDPOINT=https://api.ipify.org
CONTROLLER_SUNSHINE_CREDENTIAL_KEY=$SunshineKey
AGENT_INSTALL_ENABLED=true
AGENT_INSTALL_BASE_URL=https://$MotherIp
AGENT_PACKAGE_URL=https://$MotherIp/agent/AgentPackage.zip
AGENT_PACKAGE_SHA256=$agentSha
AGENT_CONTROLLER_URL=https://${MotherIp}:7443
AGENT_TRUST_ROOT_THUMBPRINT=$ControllerThumbprint
AGENT_VNC_ALLOWED_SOURCE_IP=
# Optional browser-facing VNC endpoint when the Admin browser cannot reach the Controller's own
# TLS port (Cloudflare / reverse proxy). Must be wss://.../admin/vnc (no query/hash).
VNC_PUBLIC_WS_URL=
CONTROLLER_WAKE_INTERFACE_ADDRESS=$MotherIp
CONTROLLER_WAKE_BROADCAST_ADDRESS=192.168.1.255
AGENT_UPDATE_ENABLED=false
"@ | Set-Content "$Src\.env.local" -Encoding ASCII
```

Write the Controller launcher:

```powershell
# USER · Mother
@"
@echo off
set "InternalUrl=http://127.0.0.1:5081"
set "AgentUrl=https://${MotherIp}:7443"
set "DatabasePath=$DataRoot\Controller\controller.sqlite"
set "ServiceCredential=$ServiceCredential"
set "PublicIpEndpoint=https://api.ipify.org"
set "SunshineCredentialKey=$SunshineKey"
set "Kestrel__Certificates__Default__Path=$Certs\controller.pfx"
set "Kestrel__Certificates__Default__Password=$PfxPassword"
set "DOTNET_ENVIRONMENT=Production"
set "ASPNETCORE_ENVIRONMENT=Production"
cd /d $CtrlOut
Controller.exe
"@ | Set-Content C:\KamenRaider\controller.cmd -Encoding ASCII

@"
@echo off
cd /d $Src
set "NODE_ENV=production"
pnpm start
"@ | Set-Content C:\KamenRaider\web.cmd -Encoding ASCII
```

---

## G. Mother PC — start, firewall, verify

```powershell
# USER · Mother — start Controller (fresh DB) and web
Start-Process cmd.exe -ArgumentList '/c C:\KamenRaider\controller.cmd'
Start-Sleep -Seconds 8
Start-Process cmd.exe -ArgumentList '/c C:\KamenRaider\web.cmd'
Start-Sleep -Seconds 8
```

```powershell
# USER · Mother — verify
$cred = $ServiceCredential
(Invoke-WebRequest http://127.0.0.1:5081/internal/health `
  -Headers @{ 'X-Service-Credential' = $cred } -UseBasicParsing).Content
(Invoke-WebRequest http://127.0.0.1:3000 -UseBasicParsing).StatusCode
(Invoke-WebRequest "https://$MotherIp" -UseBasicParsing).StatusCode
```

```powershell
# ELEVATED · Mother — firewall
New-NetFirewallRule -DisplayName 'KamenRaider Controller 7443' -Direction Inbound -Protocol TCP -LocalPort 7443 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName 'KamenRaider Web 443'         -Direction Inbound -Protocol TCP -LocalPort 443  -Action Allow -Profile Private
```

Optional share for the Child PCs:

```powershell
# ELEVATED · Mother
New-Item -ItemType Directory -Force C:\KamenRaider\setup-share | Out-Null
Copy-Item "$Certs\controller-root.cer" C:\KamenRaider\setup-share -Force
Copy-Item "$Certs\caddy-root.crt"      C:\KamenRaider\setup-share -Force
Copy-Item C:\KamenRaider\SunshineTls   C:\KamenRaider\setup-share -Recurse -Force
New-SmbShare -Name KamenRaiderSetup -Path C:\KamenRaider\setup-share -ReadAccess 'Everyone'
# If Children cannot map it anonymously, copy the files with USB instead.
```

---

## H. Mother PC — first admin + fleet Sunshine credential

```powershell
# 1) Open https://$MotherIp/login and register the admin account (email $AdminEmail).
# 2) USER · Mother — promote it to admin once (temporary script, no quoting traps)
Set-Location $Src
@'
const D = require('better-sqlite3');
const db = new D(process.argv[2]);
const r = db.prepare("UPDATE user SET role='admin' WHERE email=?").run(process.argv[3]);
console.log('changed', r.changes);
console.log(db.prepare('SELECT email,role FROM user').all());
'@ | Set-Content "$env:TEMP\promote-admin.cjs" -Encoding ASCII
node "$env:TEMP\promote-admin.cjs" "$DataRoot\Web\auth.sqlite" $AdminEmail
Remove-Item "$env:TEMP\promote-admin.cjs" -Force
```

3. Sign out/in, open `https://$MotherIp/admin/settings/sunshine`, and save the fleet Sunshine username + password (you will enter the **same values** on each Child in section I). The page never shows the password again.

---

## I. Child PC — Sunshine, trust, Agent install

```powershell
# ELEVATED · Child — copy the two root certificates from the Mother share (or USB)
#   \\<MotherIp>\KamenRaiderSetup\controller-root.cer
#   \\<MotherIp>\KamenRaiderSetup\caddy-root.crt
Import-Certificate -FilePath 'C:\temp\controller-root.cer' -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath 'C:\temp\caddy-root.crt'      -CertStoreLocation Cert:\LocalMachine\Root
```

```powershell
# ELEVATED · Child — Sunshine management credential (must match the web fleet value)
$fleetUser = Read-Host 'Sunshine fleet username'
$secure    = Read-Host 'Sunshine fleet password' -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $fleetPass = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    Stop-Service SunshineService -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    if ((Get-Service SunshineService).Status -ne 'Stopped') { throw 'Stop Sunshine before --creds (Stream Guard normally keeps it stopped).' }
    & 'C:\Program Files\Sunshine\sunshine.exe' --creds $fleetUser $fleetPass
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    $fleetPass = $null
}
```

```powershell
# ELEVATED · Child — provision a Windows-trusted localhost certificate for Sunshine
#   Copy the SunshineTls folder from the Mother share to C:\KamenRaider\SunshineTls.
New-Item -ItemType Directory -Force 'C:\ProgramData\KamenRaider\SunshineSetup' | Out-Null
C:\KamenRaider\SunshineTls\SunshineCredentialReset.exe --provision-tls `
  --sunshine-root 'C:\Program Files\Sunshine' `
  --result-file 'C:\ProgramData\KamenRaider\SunshineSetup\tls-result.json'
Get-Content 'C:\ProgramData\KamenRaider\SunshineSetup\tls-result.json'
```

```powershell
# ELEVATED · Child — install the Agent
# 1) On the Mother: /admin/machines -> generate a fresh Agent install command.
# 2) Paste that whole generated command here (it downloads and verifies the package):
<PASTE THE GENERATED INSTALL COMMAND HERE>

# 3) After it completes, check services and read the fingerprint:
Get-Service 'KamenRaider Agent','KamenRaider Stream Guard','tvnserver' | Format-Table Name,Status,StartType
Get-Content "$env:ProgramData\KamenRaider\Agent\enrollment-receipt.json"
# Type the PublicKeyFingerprint (64 hex chars) into the approval box on the Mother.
```

---

## J. Mother PC — approve and verify the machine

```powershell
# On https://$MotherIp/admin/machines:
#  - the Child appears as pending -> click อนุมัติ -> settings dialog: fill name (empty lets hostname auto-fill),
#    price in satang, Moonlight/Sunshine ports, VNC LAN IP (optional) -> click ยืนยัน -> click อนุมัติ.
#    No fingerprint typing: the web sends the Agent-reported fingerprint automatically.
```

```powershell
# USER · Mother — read what the Controller sees (after the Agent syncs)
$cred = $ServiceCredential
Invoke-RestMethod http://127.0.0.1:5081/internal/machines `
  -Headers @{ 'X-Service-Credential' = $cred } |
  Select-Object machineId,approvalStatus,displayName,hostname,cpu,gpu,ramBytes,readiness,canRent,appliedSunshineCredentialRevision |
  Format-List
```

---

## K. Day‑2 operations (Mother)

```powershell
# USER · Mother — update code and rebuild (quarantine warning: restarting with an existing DB blocks sync)
Set-Location $Src
git pull --ff-only
pnpm install --frozen-lockfile
pnpm build
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\Build-AgentPackage.ps1
# update AGENT_PACKAGE_SHA256 in .env.local from public\agent\AgentPackage.sha256
dotnet publish .\services\Controller\Controller.csproj -c Release -o $CtrlOut
```

```powershell
# USER · Mother — online backup while the Controller is running
& "$CtrlOut\Controller.exe" --backup-database `
  "$DataRoot\Controller\controller.sqlite" `
  "D:\KamenRaiderBackups\controller-$(Get-Date -Format yyyyMMddTHHmmssZ).db"
```

```powershell
# USER · Mother — find and stop the running Controller / web (before a controlled restart)
Get-NetTCPConnection -State Listen -LocalPort 5081,7443,3000 -ErrorAction SilentlyContinue |
  Select-Object LocalPort,OwningProcess
# Stop-Process -Id <pid> -Force   # repeat per owning process
```

```powershell
# USER · Mother — start again
Start-Process cmd.exe -ArgumentList '/c C:\KamenRaider\controller.cmd'
Start-Process cmd.exe -ArgumentList '/c C:\KamenRaider\web.cmd'
Start-Process "$CaddyDir\caddy.exe" -ArgumentList 'run','--config',"$CaddyDir\Caddyfile"
```

---

## L. Quick troubleshooting commands

```powershell
# Mother: Controller/Web listening?
Get-NetTCPConnection -State Listen -LocalPort 5081,7443,3000 -ErrorAction SilentlyContinue |
  Select-Object LocalPort,OwningProcess

# Mother: Controller health (service credential required)
(Invoke-WebRequest http://127.0.0.1:5081/internal/health -Headers @{ 'X-Service-Credential' = $ServiceCredential } -UseBasicParsing).Content

# Child: certificate trust checks
Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Thumbprint -eq '<ControllerThumbprint>' }
(Get-Service SunshineService).Status
(Get-Service 'KamenRaider Agent','KamenRaider Stream Guard').Status

# Child: Agent/Guard recent errors
Get-WinEvent -LogName Application -MaxEvents 200 |
  Where-Object ProviderName -eq 'Agent' |
  Select-Object -First 20 TimeCreated,Id,LevelDisplayName,Message | Format-List

# Mother: database ACL check (should list only SYSTEM/Administrators/your account; must be protected)
(Get-Acl "$DataRoot\Controller").Access | Select-Object IdentityReference,FileSystemRights,IsInherited
(Get-Acl "$DataRoot\Controller").AreAccessRulesProtected
```

> Expected at this stage: machine `readiness=notReady`, `canRent=False`. Rental/streaming/public-IP connect address cannot be validated until the boot/restoration/desktop-health evidence providers exist (see the runbook section 0).
