# เปิด Sunshine GUI ชั่วคราวบนเครื่องลูก (โหมด maintenance / LAB)

Stream Guard จะปิด SunshineService (Manual/Stopped) และ firewall block ไว้จนกว่าจะมี stream authority
ถ้าต้องใช้ **Sunshine GUI** เพื่อตั้งค่า/ดูสถานะ ให้เปิดโหมด maintenance ชั่วคราว

> **LAB ONLY:** สคริปต์จะหยุด Agent และปิด firewall block ของ Guard ชั่วคราว
> ใช้เสร็จต้อง `-Restore` ทุกครั้ง

## ใช้ GUI บนเครื่องลูกเอง (localhost)

รันบนเครื่องลูก (Administrator; script ขอ UAC เอง):

```powershell
irm "https://kamanriderplay.com/agent/Enable-SunshineGui.ps1" | iex
```

สคริปต์จะ:
- หยุด `KamenRaider Agent`
- disable firewall rule ของ Stream Guard
- เปิด `SunshineService`
- เปิดเบราว์เซอร์ `https://localhost:47990` ให้

## ใช้ GUI จากเครื่องของคุณเอง (LAN เดียวกัน)

รันบนเครื่องลูก (Administrator) ระบุ IP เครื่องคุณ:

```powershell
& ([scriptblock]::Create((irm "https://kamanriderplay.com/agent/Enable-SunshineGui.ps1"))) -AllowClientIp 192.168.1.34
```

หรือเปิดให้ทั้ง subnet:

```powershell
& ([scriptblock]::Create((irm "https://kamanriderplay.com/agent/Enable-SunshineGui.ps1"))) -AllowLan
```

แล้วเปิดจากเครื่องคุณ: `https://192.168.1.25:47990`
(ดู URL ที่สคริปต์พิมพ์ให้อีกครั้ง)

## ปิดโหมด maintenance (คืน Guard/Agent)

```powershell
& ([scriptblock]::Create((irm "https://kamanriderplay.com/agent/Enable-SunshineGui.ps1"))) -Restore
```

สคริปต์จะลบ firewall rule ชั่วคราว, เปิด firewall block ของ Guard กลับ และ start Agent

## หมายเหตุ

- ระหว่าง maintenance: Agent หยุด → Controller จะเห็นเครื่องออฟไลน์ (ปกติ)
- Sunshine GUI ใช้ username/password ที่ตั้งไว้; ถ้าจำไม่ได้ใช้ `docs/sunshine-password-reset.md`
- อย่าเปิด 47990 ออกอินเทอร์เน็ต; ใช้เฉพาะ LAN/localhost และปิด maintenance เมื่อเสร็จ
- สคริปต์อยู่ที่ `scripts/agent/child/Enable-SunshineGui.ps1` และให้บริการที่ `/agent/Enable-SunshineGui.ps1`
