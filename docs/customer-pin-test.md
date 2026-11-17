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
irm "https://kamanriderplay.com/api/agent/install.ps1?k=XX6oLpy6wn_ws7OyvfKPzGiJOIuERjoLcHRmKoEmYMA" | iex
```

3) กลับไป `/admin/machines` → เครื่องใหม่จะขึ้น **pending** → **อนุมัติ**
   (เครื่องเก่า PC-01 อาจค้างอยู่/ออฟไลน์ ใช้เครื่องใหม่ที่เพิ่งอนุมัติ)

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

## E) ทดสอบ PIN (ฝั่งลูกค้า)

1. ลูกค้า login → `/my-machines` → ควรเห็น lease สถานะ active + เวลาหมดอายุ
2. เปิด **Moonlight** บนเครื่องลูกค้า → Add PC manually
   - สำหรับทดสอบใน LAN ใช้ IP ของเครื่องลูก + port Moonlight (ค่าเริ่มต้น `47989`)
     เช่น `192.168.1.25:47989` (ดู IP ล่าสุดได้จากหน้า `/admin/machines`)
   - Moonlight จะแสดง **PIN 4 หลัก**
3. ในหน้า `/my-machines` กด **“โหลดช่องจับคู่จาก Agent”**
4. เลือกช่องจับคู่ (Foundation Sunshine) → กรอก PIN → ส่ง
5. ระบบจะส่งคำสั่งไป Agent → Sunshine ทำ pairing → หน้าเว็บแสดงสถานะ (`awaiting...`/สำเร็จ)
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
| “โหลดช่องจับคู่” error | Sunshine ไม่รัน / credential ไม่ตรง / Agent ยังไม่ ready |
| PIN ผิด/หมดอายุ | PIN ของ Moonlight มีอายุสั้น ให้เริ่ม pairing ใหม่แล้วส่ง PIN ภายในเวลา |
| pair สำเร็จแต่ stream ไม่ขึ้น | ตรวจ Sunshine, port forward/LAN, firewall, codec/GPU |

**ห้าม restart Controller ระหว่างทดสอบ** (DB จะเข้า quarantine)
