# ติดตั้ง / ซ่อม Agent บนเครื่องลูก (KamenRaiderPlay)

อัปเดตล่าสุด: 18 พ.ย. 2026 · ใช้กับเครื่องลูก Windows 10/11 x64

## 0) สิ่งที่ต้องมี
- ต่อ LAN เดียวกับเครื่องแม่ (ต้องเข้าถึง `https://192.168.1.100:7443` ได้)
- ต่ออินเทอร์เน็ต (ใช้ติดตั้ง Sunshine และดาวน์โหลดแพ็กเกจ)
- สิทธิ์ Administrator (จะมี UAC 1 ครั้ง)

## 1) ถ้าเคยติดตั้งล้มเหลว หรือเผลอลบไฟล์ Agent → ล้างก่อน

เปิด PowerShell (ปกติก็ได้ ตัว script จะขอสิทธิ์ admin เอง) แล้วรัน:

```powershell
irm "https://kamanriderplay.com/agent/Cleanup-AgentInstall.ps1" | iex
```

script จะทำให้:
- ลบ service `KamenRaider Agent` / `KamenRaider Stream Guard` ที่ค้าง
- ลบ `C:\Program Files\KamenRaider` และ `C:\ProgramData\KamenRaider`
- ลบ firewall rule ของ KamenRaider
- ตรวจ ACL ของ path ที่ installer ใช้ และพิมพ์จุดที่ทำให้ติดตั้งไม่ได้

ถ้าขึ้น `RESULT: leftovers removed and ACLs pass the installer trust check.` = พร้อมติดตั้ง

### ถ้า script รายงาน ACL problem
ให้รันคำสั่ง `fix:` ที่ script พิมพ์ เช่น:

```powershell
icacls "C:\Program Files" /remove:g "S-1-5-..."
```

แล้วรัน cleanup ซ้ำอีกครั้ง; ถ้าไม่แน่ใจให้แคปข้อความที่พิมพ์ส่งให้แอดมิน

### ลบโฟลเดอร์เองแบบ manual (ถ้าจำเป็น)

```powershell
takeown /f "C:\Program Files\KamenRaider" /r /d y
icacls "C:\Program Files\KamenRaider" /grant Administrators:F /t /c
Remove-Item "C:\Program Files\KamenRaider" -Recurse -Force

takeown /f "C:\ProgramData\KamenRaider" /r /d y
icacls "C:\ProgramData\KamenRaider" /grant Administrators:F /t /c
Remove-Item "C:\ProgramData\KamenRaider" -Recurse -Force

Test-Path "C:\Program Files\KamenRaider", "C:\ProgramData\KamenRaider"   # ต้องได้ False ทั้งคู่
```

## 2) ขอ one-liner จากเว็บ

1. เข้า `https://kamanriderplay.com/login` (บัญชี admin)
2. ไปที่ `/admin/machines` → กด **ติดตั้ง Agent** → **ขอคำสั่งติดตั้ง**
3. คัดลอก **“คำสั่งติดตั้งคลิกเดียว (แนะนำ)”** หน้าตาแบบนี้:

```powershell
irm "https://kamanriderplay.com/api/agent/install.ps1?k=<TOKEN>" | iex
```

> TOKEN ใช้ได้ครั้งเดียวและมีอายุ ~24 ชม. ถ้าใช้ไปแล้ว/หมดอายุ ให้กดขอใหม่
> อย่าใช้ one-liner เก่าซ้ำ เพราะ package hash เปลี่ยนเมื่ออัปเดต installer

## 3) รันคำสั่งบนเครื่องลูก

- เปิด PowerShell **ปกติ** (ไม่ต้อง Run as Administrator)
- วางคำสั่งแล้วกด Enter
- กด **Yes** ที่หน้าต่าง UAC
- รอจนขึ้น `Installation finished...`

ตัวติดตั้งจะทำให้อัตโนมัติ:
| ขั้น | รายละเอียด |
|---|---|
| Sunshine | ถ้ายังไม่มี จะติดตั้งให้ผ่าน winget (`LizardByte.Sunshine`) |
| Certificate | ดาวน์โหลด + ตรวจ thumbprint แล้ว import เข้า LocalMachine Root |
| Integrity | ตรวจ SHA-256 ของ `Bootstrap-Agent.ps1` และ `AgentPackage.zip` |
| Services | ติดตั้ง `KamenRaider Stream Guard`, `KamenRaider Agent`, TightVNC |
| Enroll | ลงทะเบียนกับ Controller แล้วเปิดหน้าแอดมินกลับมา |

## 4) อนุมัติเครื่องที่หน้าแอดมิน

- กลับไปที่ `/admin/machines` → เครื่องใหม่จะขึ้นสถานะ **pending**
- กรอก ชื่อ, ราคา (หน่วยสตางค์/ชั่วโมง), ports, VNC target IP
- กดอนุมัติด้วย fingerprint จากไฟล์บนเครื่องลูก:

```powershell
Get-Content "$env:ProgramData\KamenRaider\Agent\enrollment-receipt.json"
```

## 5) ตรวจหลังติดตั้ง

```powershell
Get-Service 'KamenRaider Agent','KamenRaider Stream Guard','tvnserver','SunshineService' |
  Format-Table Name,Status,StartType
```

ต้องเห็น:
- `KamenRaider Agent` — Running
- `KamenRaider Stream Guard` — Running
- `tvnserver` — Running
- `SunshineService` — มีอยู่ (จะ Running หรือ Manual ก็ได้)

## Troubleshooting

| อาการ | สาเหตุ / วิธีแก้ |
|---|---|
| `Cannot find an overload for "SetEquals"` | แพ็กเกจเก่า → ขอ one-liner ใหม่ (รุ่นที่แก้แล้ว hash `3CAC6829…67059`) |
| `Agent installation is incomplete or inconsistent` | มีเศษติดตั้งเก่าค้าง → รัน Cleanup ในข้อ 1 |
| `Bootstrap path has an untrusted writable ancestor` | ACL มี ACE แปลก (มักเกิดจาก takeown/ลบไฟล์เอง) → รัน Cleanup ข้อ 1 แล้วทำตามบรรทัด `fix:` |
| `Sunshine is not installed` หรือ winget ผิดพลาด | ติดตั้ง Sunshine manual จาก https://github.com/LizardByte/Sunshine/releases แล้วรัน one-liner ใหม่ |
| เครื่องลูกต่อ Controller ไม่ได้ | ต้องอยู่ LAN เดียวกันและเข้าถึง `192.168.1.100:7443`; ตรวจ firewall |
| UAC ไม่ขึ้น / หน้าต่างใหม่ไม่เปิด | เปิด PowerShell As Administrator แล้วรัน one-liner ซ้ำ |

## หมายเหตุ
- ไฟล์นี้: `docs/child-agent-install.md`
- สคริปต์ cleanup (ต้นฉบับ): `scripts/agent/child/Cleanup-AgentInstall.ps1`
- ตัวที่ใช้รันจริงผ่านเว็บ: `https://kamanriderplay.com/agent/Cleanup-AgentInstall.ps1`
- **Agent releases ในหน้าเว็บไม่เกี่ยวกับการติดตั้ง** — ใช้สำหรับ self-update ภายหลังเท่านั้น
- ห้าม restart Controller ระหว่างที่เครื่องลูกกำลัง enroll/sync (จะเข้า quarantine และ sync ได้ 409)
