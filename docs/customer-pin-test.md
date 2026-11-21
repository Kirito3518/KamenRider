# ทดสอบ flow ลูกค้า + PIN เชื่อมต่อ (โหมด LAB)

> **LAB ONLY:** โหมดนี้ทำให้ Agent รายงาน `ready` โดยข้ามหลักฐาน boot/restore/desktop-health จริง
> ห้ามเปิดใช้กับเครื่องที่ปล่อยเช่าจริง
>
> เรื่องเงิน/เวลาทดสอบ: ใช้ **trial lease** (แอดมินสร้างให้) — **ไม่หักยอดเงิน** แต่ lease ยังมีเวลาหมดอายุ/ต่อเวลาได้ตามปกติ

---

## A) ติดตั้ง Agent แพ็กเกจใหม่ (มี lab mode) บนเครื่องลูก

แพ็กเกจใหม่ SHA-256: `78E8D1B050ACC2821871EF7FCA47F6748471EFEFF6012D60A41EDF2B6888F54C`
(ต้องติดตั้งใหม่เพราะ binary เปลี่ยน — ใช้ one-liner ใหม่ด้านล่าง)

1) ล้างของเดิม (Run PowerShell ปกติก็ได้ script ขอ UAC เอง):

```powershell
irm "https://kamanriderplay.com/agent/Cleanup-AgentInstall.ps1" | iex
```

2) ติดตั้งด้วย one-liner ใหม่ (ticket ปัจจุบัน ใช้ได้ครั้งเดียว ~24 ชม.):

```powershell
irm "https://kamanriderplay.com/api/agent/install.ps1?k=YUczxgWS27epOY4jTpDYzzdTwifPh_v8MinIURtUMZY" | iex
```

3) กลับไป `/admin/machines` → เครื่องใหม่จะขึ้น **pending** → **อนุมัติ**
   (เครื่องเก่า PC-01 อาจค้างอยู่/ออฟไลน์ ใช้เครื่องใหม่ที่เพิ่งอนุมัติ)

---

## A2) อนุมัติเครื่อง (pending) + หมายเหตุ TightVNC

หลัง one-liner สำเร็จ เครื่องจะขึ้น **pending** ใน `/admin/machines`:

- MachineId: `1312ae4135954d3aa6a25bf818780b31`
- Fingerprint: `94EA7919182EF4C1F05E0E96CF468D5A2073E5425CD4C1EEEB9F3F1E77F12AC1`
- Hostname: `DESKTOP-S0ABVFV` (IP 192.168.1.25)

กด **อนุมัติ** ที่รายการเครื่อง → ตั้งค่าชื่อ/ราคาใน dialog → กด **ยืนยัน** → กด **อนุมัติ** ใน alert dialog
(ไม่ต้องพิมพ์ fingerprint; ระบบใช้ fingerprint ที่ Agent รายงานโดยอัตโนมัติ)

### ถ้าขึ้น `An unmanaged or incompatible TightVNC installation exists`

แปลว่าเครื่องมี TightVNC ติดตั้งแบบไม่ได้มาจาก Agent อยู่ก่อนแล้ว (ไม่ fatal — PIN/Moonlight ยังทดสอบได้)
ถ้าต้องการให้ VNC ใช้งานได้:

```powershell
# ดูรายการ TightVNC ที่ติดตั้งอยู่
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' |
  Where-Object DisplayName -match 'TightVNC' |
  Select-Object DisplayName, DisplayVersion, UninstallString, PSChildName

# ถอดแบบ silent (แทน {ProductCode} ด้วย PSChildName ที่เห็น)
# msiexec /x {ProductCode} /qn /norestart

# หรือถ้าติดตั้งผ่าน winget
# winget uninstall TightVNC.TightVNC
```

หลังถอดแล้ว ให้ **ขอ one-liner ใหม่** จาก `/admin/machines` แล้วรันซ้ำ เพื่อให้ installer ติดตั้ง TightVNC เวอร์ชัน managed ให้สมบูรณ์

---

## B) เปิด lab-ready mode ให้ Agent

รันบนเครื่องลูก (Administrator):

```powershell
New-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\KamenRaider Agent' `
  -Name Environment -PropertyType MultiString `
  -Value 'KAMENRAIDER_AGENT_LAB_READY=1' -Force

Restart-Service 'KamenRaider Agent' -Force
Start-Sleep -Seconds 10

Get-Service 'KamenRaider Agent','KamenRaider Stream Guard','SunshineService' |
  Format-Table Name,Status,StartType
```

หลังทำแล้ว แจ้งแอดมินเพื่อตรวจ `readiness = ready` / `canRent = true`
(ถ้าไม่ขึ้น ให้ดู log: `Get-WinEvent -ProviderName "Agent" -MaxEvents 20 | fl TimeCreated,Id,LevelDisplayName,Message`)

---

## B2) เคลียร์ maintenance fence (จำเป็นก่อน lease แรก)

หลัง lab-ready แล้ว ถ้าสร้าง lease แล้วขึ้น `failed` → Agent ปฏิเสธ `PrepareMachine` เพราะ maintenance fence
อ่านเป็น "held" (ไฟล์ `C:\ProgramData\KamenRaider.AgentMaintenance\maintenance-fence.json` หาย/อ่านไม่ได้)

รันซ่อมบนเครื่องลูก (script ขอ UAC เอง จะสร้างโฟลเดอร์ + ไฟล์ + ACL ที่ถูกต้อง + clear fence ให้):

```powershell
irm "https://kamanriderplay.com/agent/Fix-AgentMaintenanceFence.ps1" | iex
```

หลังรันแล้ว ควรเห็น `ACL verification passed` และ service ถูก restart
> หลังเคลียร์แล้ว บอกแอดมินเพื่อสร้าง trial lease ใหม่ (allocation ของ lease ที่ failed ถูกปลดแล้ว)

## C) ตั้ง Sunshine credential ให้ตรงกัน (จำเป็นต่อการ pair PIN)

1. แอดมินเข้า `https://kamanriderplay.com/admin/settings/sunshine`
   ตั้ง username/password สำหรับ fleet (เช่น `admin` / รหัสทดสอบที่จำได้)
2. ที่เครื่องลูก ตั้ง credential ของ Sunshine ให้เป็นค่าเดียวกัน:
   - เปิด Sunshine web UI ที่เครื่องลูก (เมื่อ Sunshine รันอยู่) `https://localhost:47990`
     แล้วตั้ง username/password ให้ตรงกับข้อ 1 **หรือ** ใช้เครื่องมือ `SunshineCredentialReset.exe --apply`
3. รอ Agent sync credential revision (ไม่กี่วินาที)

> ถ้า credential ไม่ตรงกัน pairing จะถูกปฏิเสธ (Agent ใช้ credential นี้เรียก Sunshine API)

---

## D) บัญชีลูกค้าทดสอบ + trial lease

บัญชีทดสอบที่สร้างไว้ (role user, balance 0 — trial ไม่หักเงิน):

- อีเมล: `customer.lab@kamenraider.local`
- รหัสผ่าน: `LabCustomer123!`

หลังเครื่องใหม่ `ready` แล้ว แอดมินจะสร้าง **trial lease** ให้บัญชีนี้ (duration เช่น 86400 วินาที = 24 ชม.)
แล้วลูกค้า login ที่ `https://kamanriderplay.com/login` → ไปที่ `/my-machines`

---

## D2) เรื่อง Sunshine ถูก Stopped/Manual + firewall block — เป็นปกติ

Stream Guard ตั้งใจปิด Sunshine (Manual/Stopped) และ block inbound ไว้จนกว่าจะมี **stream authority (active lease)**
ห้ามพยายามเปิด SunshineService เอง เพราะ Guard จะปิด/บล็อกกลับ

เมื่อ lease active สำเร็จ Guard จะเปิด Sunshine ให้เอง; จากนั้นเปิด Sunshine web UI ได้จาก **เครื่องลูกเอง** ที่
`https://localhost:47990` (loopback ไม่ถูก firewall inbound block) เพื่อตั้ง password ให้ตรงกับ fleet credential

## E) ทดสอบ PIN (ฝั่งลูกค้า)

1. ลูกค้า login → `/my-machines` → ควรเห็นเฉพาะเครื่องที่กำลังเช่า (state active) + เวลาหมดอายุ
2. ในการ์ดเครื่อง กดปุ่ม **Moonlight** → dialog แสดงที่อยู่ `ip:port` ด้านบนพร้อมปุ่มคัดลอก
3. คัดลอกที่อยู่ไปเพิ่มใน **Moonlight** บนเครื่องลูกค้า (Add PC manually)
   - Moonlight จะแสดง **PIN 4 หลัก**
4. กลับมาที่ dialog ของเว็บ กรอก PIN 4 หลัก → กด **ยืนยัน PIN**
   - ระบบตรวจหาช่องจับคู่จาก Agent ให้อัตโนมัติ (ไม่ต้องกดโหลดช่องจับคู่เอง)
5. ระบบจะส่งคำสั่งไป Agent → Sunshine ทำ pairing → dialog แสดงสถานะ (`queued`/สำเร็จ/ไม่สำเร็จ)
6. เมื่อ pair สำเร็จ Moonlight จะเห็นเครื่องและเริ่ม stream ได้ (ทดสอบว่าเห็นภาพ/เสียง/อินพุต)

---

## ตรวจสอบ/แก้ปัญหา

```powershell
# ฝั่งเครื่องลูก
Get-Service 'KamenRaider Agent','KamenRaider Stream Guard','SunshineService'
Get-WinEvent -ProviderName "Agent" -MaxEvents 20 -ErrorAction SilentlyContinue |
  Select-Object TimeCreated, Id, LevelDisplayName, Message | Format-List

# ตรวจว่า Agent อ่าน env lab-ready แล้ว (ใน service registry)
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\KamenRaider Agent' -Name Environment
```

| อาการ | สาเหตุ/แก้ |
|---|---|
| `/my-machines` ไม่มี lease | trial lease ยังไม่ถูกสร้าง/สร้างไม่สำเร็จ (ต้อง `ready`) |
| dialog Moonlight แจ้ง “โหลดช่องจับคู่ไม่สำเร็จ” | Sunshine ไม่รัน / credential ไม่ตรง / Agent ยังไม่ ready |
| PIN ผิด/หมดอายุ | PIN ของ Moonlight มีอายุสั้น ให้เริ่ม pairing ใหม่แล้วส่ง PIN ภายในเวลา |
| pair สำเร็จแต่ stream ไม่ขึ้น | ตรวจ Sunshine, port forward/LAN, firewall, codec/GPU |

**ห้าม restart Controller ระหว่างทดสอบ** (DB จะเข้า quarantine)
