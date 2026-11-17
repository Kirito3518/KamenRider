# คู่มือคำสั่ง — เครื่องแม่ (Mother) `192.168.1.100`

ไฟล์นี้ทำไว้ให้ **คัดลอกคำสั่งจากไฟล์นี้บนเครื่องนั้น** (ไม่ต้องคัดลอกจากแชท)
repo อยู่ที่ `C:\Users\PC\Desktop\KamenRider`

เปิดไฟล์นี้บนเครื่องนั้นด้วยคำสั่ง:

```powershell
notepad "$env:USERPROFILE\Desktop\KamenRider\docs\mother-setup-192.168.1.100.md"
```

> หมายเหตุ: ถ้าเพิ่ง `git pull` แล้วยังไม่เห็นไฟล์นี้ ให้รันคำสั่งในหัวข้อ 1 ก่อน

---

## 0) เปิด PowerShell ที่ repo

```powershell
cd "$env:USERPROFILE\Desktop\KamenRider"
```

ถ้าต้องรันคำสั่ง firewall ให้เปิด PowerShell แบบ **Run as Administrator** ด้วย

---

## 1) อัปเดตโค้ดให้ล่าสุด

```powershell
git pull
git log -1 --oneline
```

ต้องเห็น `4f5be6a` หรือใหม่กว่า ถ้า `git` ยังไม่รู้จัก ให้ปิดหน้าต่างนี้แล้วเปิดใหม่ (หรือ sign out/in หลังติดตั้ง Git)

---

## 2) แก้ปัญหา `dotnet` หาไม่เจอ (ทำทุกครั้งที่เปิดหน้าต่างใหม่)

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User') + ';' + $env:Path
dotnet --version
```

ถ้ายังไม่เจอ ให้ค้นหา `dotnet.exe`:

```powershell
Get-ChildItem 'C:\Program Files\dotnet','C:\Program Files (x86)\dotnet',"$env:LOCALAPPDATA\Microsoft\dotnet","$env:USERPROFILE\.dotnet" -Filter dotnet.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
```

- ถ้าเจอ path ให้เก็บไว้ใช้ในข้อ 6:

```powershell
$dotnetExe = 'C:\Program Files\dotnet\dotnet.exe'
& $dotnetExe --list-sdks
```

- ถ้าไม่เจอไฟล์เลย = SDK ยังไม่ติดตั้งจริง ให้ลงใหม่:

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
```

หลังติดตั้งเสร็จ ให้รัน PATH refresh ด้านบนซ้ำ (หรือ sign out/in) แล้วตรวจ `dotnet --list-sdks` ต้องเห็น `10.0.x`

---

## 3) ตรวจเครื่องมืออื่น

```powershell
node --version
pnpm --version
git --version
```

ถ้า `pnpm` หาย:

```powershell
npm install -g pnpm@11.8.0
```

ถ้า `node` หาย:

```powershell
winget install --id OpenJS.NodeJS.LTS -e
```

แล้วปิด/เปิดหน้าต่างใหม่

---

## 4) ติดตั้ง dependencies

```powershell
pnpm install --frozen-lockfile
```

(ถ้าเคยรันแล้วจะเร็ว)

---

## 5) ตั้ง IP เครื่องนี้เป็น `192.168.1.100` (ถ้ายังไม่ได้ตั้ง)

ตั้งผ่าน GUI: **Settings → Network & internet → Ethernet → IP assignment = Manual**
- IP: `192.168.1.100`
- Subnet: `255.255.255.0`
- Gateway: `192.168.1.1`

ตรวจว่าได้ IP แล้ว:

```powershell
Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '192.168.1.*' } | Select-Object InterfaceAlias,IPAddress
```

---

## 6) เตรียม clone (แก้ path env, ACL, archive DB ที่แนบมา)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Bootstrap-DevClone.ps1
```

ถ้าขึ้น `Missing tools: dotnet` ให้ระบุ path ตรง ๆ:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Bootstrap-DevClone.ps1 -DotnetPath 'C:\Program Files\dotnet\dotnet.exe'
```

ผลที่ต้องเห็น: `[ok] node/pnpm/dotnet`, dev HTTPS cert exists, updated .env.local, data DACL protected, archived Controller DB, AgentPackage SHA matches

---

## 7) ตั้งค่าเป็น Mother ที่ `192.168.1.100` และสตาร์ท

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Configure-MotherLan.ps1 -LanIp 192.168.1.100 -Start
```

ผลที่ต้องเห็น: สร้าง Controller certificate, `.env.local` updated (`http://192.168.1.100:3000`), เขียน launcher, `Controller health`, `Web (local) -> 200`

ถ้าแจ้งว่าพอร์ตถูกใช้ ให้ดู/ปิด process เก่า:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 5081,7443,3000 -ErrorAction SilentlyContinue | Select-Object LocalPort,OwningProcess
```

```powershell
Stop-Process -Id <pid> -Force
```

หรือรันสคริปต์ซ้ำด้วย `-Force`

---

## 8) Firewall (ถ้าสคริปต์แจ้งว่า not elevated)

เปิด **PowerShell as Administrator** แล้วรัน:

```powershell
New-NetFirewallRule -DisplayName 'KamenRaider Controller' -Direction Inbound -Protocol TCP -LocalPort 7443 -Action Allow -Profile Private
```

```powershell
New-NetFirewallRule -DisplayName 'KamenRaider Web' -Direction Inbound -Protocol TCP -LocalPort 3000 -Action Allow -Profile Private
```

---

## 9) ตรวจระบบ

```powershell
(Invoke-WebRequest http://127.0.0.1:3000/ -UseBasicParsing).StatusCode
```

```powershell
(Get-FileHash public\agent\AgentPackage.zip -Algorithm SHA256).Hash
```

เปิดเบราว์เซอร์บนเครื่องนั้น:

```text
http://192.168.1.100:3000/login
```

---

## 10) สร้าง/เลื่อนสิทธิ์ admin (ถ้ายังไม่มี)

1. สมัครบัญชีที่ `http://192.168.1.100:3000/login` ก่อน
2. กลับมาที่ PowerShell แล้วรัน (แก้ `you@example.com` เป็นอีเมลที่สมัคร):

```powershell
@'
const D = require('better-sqlite3');
const db = new D(process.argv[2]);
db.prepare("UPDATE user SET role='admin' WHERE email=?").run(process.argv[3]);
console.log(db.prepare('SELECT email, role FROM user').all());
'@ | Set-Content "$env:TEMP\promote-admin.cjs" -Encoding ASCII
```

```powershell
node "$env:TEMP\promote-admin.cjs" "$env:USERPROFILE\Desktop\KamenRider\data\auth.sqlite" "you@example.com"
```

3. sign out / sign in ใหม่ แล้วเปิด:

```text
http://192.168.1.100:3000/admin/machines
http://192.168.1.100:3000/admin/settings/sunshine
```

---

## 11) ขั้นต่อไป: ติดตั้ง Agent จากเครื่องลูก (ต้องมี HTTPS ก่อน)

ตอนนี้เว็บเป็น HTTP — ติดตั้ง Agent จากเครื่องลูกได้เฉพาะเมื่อมี HTTPS reverse proxy (Caddy) แล้ว:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Configure-MotherLan.ps1 -LanIp 192.168.1.100 -HttpsWeb -Start
```

จากนั้นที่ **เครื่องลูก** (Run as Administrator) import certificate ของ Mother:

```powershell
Import-Certificate -FilePath 'C:\path\to\controller-root.cer' -CertStoreLocation Cert:\LocalMachine\Root
```

ไฟล์ cert อยู่ที่เครื่องแม่:

```text
C:\Users\PC\Desktop\KamenRider\.next-local\mother\controller-root.cer
```

แล้วใช้คำสั่งติดตั้ง Agent ที่สร้างจากหน้า `/admin/machines` ไปรันบนเครื่องลูก (elevated)

> รายละเอียด Caddy/HTTPS ดู `docs/production-commands.md`

---

## 12) Troubleshooting

### `dotnet` ยังไม่เจอ
ทำหัวข้อ 2 ซ้ำ หรือระบุ `-DotnetPath` ในหัวข้อ 6

### `The Controller database directory must have a protected DACL`
รัน Bootstrap ซ้ำ (หัวข้อ 6) แล้วรัน Configure อีกครั้ง

### Controller ไม่ขึ้น / พอร์ตชน
ดู process ด้วยคำสั่งในหัวข้อ 7 แล้ว `Stop-Process` หรือรันซ้ำด้วย `-Force`

### Web ไม่ขึ้น (`Web did not start listening`)
1. ตรวจก่อนว่าอาจ start ช้า/ฟังพอร์ตอื่น:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 3000,3001 -ErrorAction SilentlyContinue | Select-Object LocalPort,OwningProcess
Get-Process node -ErrorAction SilentlyContinue | Select-Object Id,StartTime
```

2. อ่าน log ของ web (ตั้งแต่ commit ใหม่ สคริปต์เขียน log ไว้):

```powershell
Get-Content .\.next-local\mother\web-dev.log -Tail 50
```

3. สตาร์ท web เองแบบเห็น error ชัด ๆ:

```powershell
cd "$env:USERPROFILE\Desktop\KamenRider"
pnpm dev
```

หรือใช้ตัวที่สคริปต์สร้างให้ (มี log):

```powershell
.\.next-local\mother\web-dev.cmd
```

แล้วอ่าน log:

```powershell
Get-Content .\.next-local\mother\web-dev.log -Tail 50
```

4. สาเหตุที่พบบ่อย:
- `Port 3000 is in use` → ปิด process เดิม (หัวข้อ 7)
- `Cannot find module 'next'` / module หาย → `pnpm install --frozen-lockfile`
- `EPERM`/`EACCES` ที่ `.next` → ปิด node ทั้งหมดแล้วลบโฟลเดอร์ `.next` (`Remove-Item .next -Recurse -Force -ErrorAction SilentlyContinue`) แล้ว start ใหม่
- เปิดในหน้าต่าง elevated แล้ว path ของ pnpm หาย → เปิดหน้าต่างใหม่ (ไม่ elevated) หรือรัน `pnpm --version` ก่อน

5. หลัง web ขึ้นแล้ว รันซ้ำแบบมี log/ตรวจให้:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\dev\Configure-MotherLan.ps1 -LanIp 192.168.1.100 -Force -Start
```

(ถ้า Controller ยังรันอยู่ สคริปต์จะข้ามการ start Controller และเริ่มเฉพาะ web)


### Agent sync 409 / quarantine
อย่าใช้ DB ที่แนบมากับ repo — ให้ Bootstrap archive แล้วปล่อยให้ Controller สร้าง DB ใหม่ (หัวข้อ 6)

### เปิดเว็บจากเครื่องอื่นไม่ได้
ตรวจ firewall หัวข้อ 8 และตรวจว่าเครื่องนั้นได้ IP `192.168.1.100` จริง (หัวข้อ 5)

### Certificate หมดอายุ/อยากสร้างใหม่
ลบไฟล์นี้แล้วรัน Configure ซ้ำ:

```powershell
Remove-Item .\.next-local\mother\cert.json -Force
```

### ต้องการ stop stack ที่รันอยู่
```powershell
Get-NetTCPConnection -State Listen -LocalPort 5081,7443,3000 -ErrorAction SilentlyContinue | Select-Object LocalPort,OwningProcess
```

```powershell
Stop-Process -Id <pid> -Force
```

---

## 13) เริ่มใหม่ทั้งหมด (ถ้าอยากล้างข้อมูลทดสอบ)

```powershell
Get-NetTCPConnection -State Listen -LocalPort 5081,7443,3000 -ErrorAction SilentlyContinue | Select-Object LocalPort,OwningProcess
```

```powershell
Stop-Process -Id <pid> -Force
```

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force .\.next-local\reset-$stamp | Out-Null
Move-Item .\data\controller.sqlite,.\data\controller.sqlite.restore-epoch,.\data\controller.sqlite.active.lock,.\data\controller.sqlite.maintenance.lock -Destination ".\.next-local\reset-$stamp" -Force -ErrorAction SilentlyContinue
```

> ถ้าคำสั่งด้านบน path ไม่ถูก ให้ใช้เวอร์ชันนี้แทน:

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Move-Item .\data\controller.sqlite* -Destination (New-Item -ItemType Directory -Force ".\.next-local\reset-$stamp").FullName -Force -ErrorAction SilentlyContinue
```

แล้วกลับไปเริ่มที่หัวข้อ 6 อีกครั้ง

---

## 14) หมายเหตุความปลอดภัย

- `.env.local` และ `data/` ใน repo นี้เป็นข้อมูลทดสอบที่มี secret จริงจากเครื่อง dev — **rotate secret ก่อนใช้งานจริง**
- ไฟล์ cert/รหัส PFX อยู่ที่ `.next-local\mother\` (อยู่ในเครื่องเท่านั้น)
- repo นี้ private แต่ค่าที่เคย push อยู่ใน git history แล้ว ตามที่ตกลงกันไว้
