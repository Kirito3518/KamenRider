# ตรวจ Agent ไมดึง fleet Sunshine credential (applied revision = 0)

อัปเดต: 18 พ.ย. 2026

## ทำความเข้าใจ revision (คนละตัวกัน)

| ค่า | ความหมาย | ค่าล่าสุด |
|---|---|---|
| `Revision N` บนการ์ดเครื่อง | **lease revision** (เพิ่มเมื่อต่อเวลา) | lease ใหม่ = 1 |
| Fleet Sunshine credential revision | รหัส Sunshine ที่แอดมินตั้งใน `/admin/settings/sunshine` | **3** (`kamenrider`) |
| `appliedSunshineCredentialRevision` | Agent ดึง fleet credential ไปเก็บ/ใช้แล้วหรือยัง | **0** (ยังไม่ดึง) |

ตัวเลข 2/3 ที่เห็นจึงอาจเป็น lease revision กับ credential revision คนละตัว — ไม่ใช่ค่าเดียวกัน

## สถานะ lease ปัจจุบัน

- Lease: `090c0de9b534472d81034462a63f0ff9`
- state: `active`
- หมดอายุ: 2026-11-18 ~20:03 UTC (19 พ.ย. 2026 ~03:03 น. เวลาไทย)
- หมายเหตุ: lease เก่า (`b660e08d…`) ถูก `EndLease` ไปแล้ว (อาจกดยกเลิก/ปิดเครื่อง)

## เก็บหลักฐานจากเครื่องลูก (Administrator)

1. Event Log ของ Agent (20 รายการล่าสุด):

```powershell
Get-WinEvent -ProviderName "Agent" -MaxEvents 20 -ErrorAction SilentlyContinue |
  Select-Object TimeCreated, Id, LevelDisplayName, Message | Format-List
```

2. โครงสร้าง private store:

```powershell
Get-ChildItem 'C:\ProgramData\KamenRaider\Agent' -Recurse -Force -ErrorAction SilentlyContinue |
  Select-Object FullName, Length, LastWriteTime
```

3. ACL ของโฟลเดอร์ sunshine:

```powershell
Get-Acl 'C:\ProgramData\KamenRaider\Agent\sunshine' -ErrorAction SilentlyContinue | Format-List
```

## การอ่านผล

| สิ่งที่เห็นใน log | ความหมาย / ทางแก้ |
|---|---|
| `Applied fleet Sunshine management credential revision 3.` | สำเร็จ — ไปทดสอบ PIN ได้ |
| `Sunshine credential update failed (UnauthorizedAccessException)` | ACL ของ `Agent\sunshine` ไม่ผ่าน — ต้องซ่อม ACL (full control: Agent service SID, SYSTEM, Administrators; owner Administrators) |
| `applied revision is unavailable (UnauthorizedAccessException)` | ไฟล์ `applied-credential.json` / โฟลเดอร์ ACL ไม่ผ่าน — ลบโฟลเดอร์ sunshine แล้ว restart Agent เพื่อสร้างใหม่ |
| `Sunshine credential fetch was rejected (…)` | Agent ยังไม่ approved หรือ Controller quarantine — ตรวจสถานะเครื่อง |
| `Sunshine credential endpoint has no configured fleet credential` | หน้า admin ยังไม่ตั้ง credential (ตอนนี้มี revision 3 แล้ว) |

## ขั้นตอนแก้แบบเร็ว (ถ้าเป็น ACL)

```powershell
# ลบ store เดิม ให้ Agent สร้างใหม่ด้วย ACL ของตัวเอง
Remove-Item 'C:\ProgramData\KamenRaider\Agent\sunshine' -Recurse -Force -ErrorAction SilentlyContinue
Restart-Service 'KamenRaider Agent' -Force
Start-Sleep -Seconds 15
Get-WinEvent -ProviderName "Agent" -MaxEvents 20 |
  Select-Object TimeCreated, Id, LevelDisplayName, Message | Format-List
```

> **อย่าเปลี่ยน fleet credential ซ้ำ** จนกว่า Agent จะตามทัน (applied revision = fleet revision) ไม่งั้น revision จะเพิ่มขึ้นเรื่อย ๆ

## หลัง applied revision = 3

1. SunshineService ต้อง Running (Guard เปิดให้เพราะ lease active)
2. ตั้ง Sunshine credential ให้ตรงกับ fleet (`kamenrider` / รหัสที่ตั้ง) — ดู `docs/sunshine-password-reset.md`
3. เปิด Moonlight → `192.168.1.25:47989` → ให้ขึ้น PIN
4. `/my-machines` → “โหลดช่องจับคู่จาก Agent” → เลือกช่อง → กรอก PIN → ส่ง
