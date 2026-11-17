# เครื่องลูก: ติดตั้งแล้วแต่ยังไม่ขึ้นใน /admin/machines (enrollment pending)

สถานการณ์: service `KamenRaider Agent` / `KamenRaider Stream Guard` ขึ้น Running แล้ว
`Test-NetConnection 192.168.1.100 -Port 7443` = **True**
แต่ Event Log `Agent` ขึ้น:

```
Enrollment unavailable; retrying in 5 seconds (HttpRequestException)
```

สาเหตุที่พบบ่อย: **ticket ที่ใช้ตอนติดตั้งไม่ตรงกับ Controller DB ปัจจุบัน** (เช่น DB ถูก reset/เปลี่ยน)
หรือ firewall/network profile เพิ่งถูกแก้ — ต้องใช้ **one-liner ใหม่** ที่มี ticket จาก DB ปัจจุบัน

## 1) ขอ one-liner ใหม่จากหน้า admin
- `https://kamanriderplay.com/login` (admin) → `/admin/machines` → **ติดตั้ง Agent** → **ขอคำสั่งติดตั้ง**
- คัดลอก **“คำสั่งติดตั้งคลิกเดียว”** (ห้ามใช้ URL เก่า)

## 2) one-liner ที่ออกให้ล่าสุด (ใช้ได้ครั้งเดียว ~24 ชม.)

```powershell
irm "https://kamanriderplay.com/api/agent/install.ps1?k=j2KIb0Kp4pvCV3ag3sd6IKgI4wev2iW7hQVQMGL0DpQ" | iex
```

- token นี้ผูกกับ Controller DB ปัจจุบัน (ออกเมื่อ 18 พ.ย. 2026 ~01:27 น.)
- ถ้าใช้ไปแล้วหรือหมดอายุ ให้กดขอใหม่จากหน้า admin (อย่าใช้ซ้ำ)

## 3) รันบนเครื่องลูก

เปิด PowerShell (ไม่ต้อง admin ตัวติดตั้งจะขอ UAC เอง):

```powershell
irm "https://kamanriderplay.com/api/agent/install.ps1?k=j2KIb0Kp4pvCV3ag3sd6IKgI4wev2iW7hQVQMGL0DpQ" | iex
```

- เครื่องที่ติดตั้งอยู่แล้วจะเข้าโหมด **repair/enrollment** ด้วย ticket ใหม่
- ถ้าขึ้น `Agent installation is incomplete or inconsistent` ให้ล้างก่อนแล้วรันซ้ำ:
  ```powershell
  irm "https://kamanriderplay.com/agent/Cleanup-AgentInstall.ps1" | iex
  ```

## 4) หลัง enroll

1. กลับไป `/admin/machines` → เครื่องจะขึ้น **pending** → **อนุมัติ**
2. **รัน one-liner เดิมซ้ำอีกครั้ง** เพื่อให้ตัวติดตั้งทำ TightVNC ที่รอบแรกข้ามไป (ตอนนั้นยัง enroll ไม่ได้)
3. ตรวจ service:
   ```powershell
   Get-Service 'KamenRaider Agent','KamenRaider Stream Guard','tvnserver' | Format-Table Name,Status,StartType
   ```

## ตรวจเพิ่มเติมถ้ายังไม่ขึ้น

```powershell
# TCP ต้อง True
Test-NetConnection 192.168.1.100 -Port 7443

# ดู log ล่าสุดของ Agent
Get-WinEvent -ProviderName "Agent" -MaxEvents 20 -ErrorAction SilentlyContinue |
  Select-Object TimeCreated, Id, LevelDisplayName, Message | Format-List

# ตรวจว่ามี cert ของ Controller อยู่ใน Root store
Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq 'B0244BCD3FD02A4F0260E433C34A26BE3B5BA482'
```

## หมายเหตุ
- **อย่า restart Controller** ระหว่างทดสอบ — DB จะเข้า quarantine และ enrollment/sync จะได้ 409
- ไฟล์นี้สำหรับอ่าน/พิมพ์เองบนเครื่องลูก (คัดลอกข้ามเครื่องไม่ได้)
- สถานะเครื่องแม่ล่าสุด: Controller/Web Running, DB fresh `quarantine=0`, firewall 7443 = Any, LAN = Private
