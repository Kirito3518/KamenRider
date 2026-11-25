# Parsec — ให้ลูกค้าเข้าสู่ระบบด้วยบัญชีของตัวเอง (แบบ 0jay)

สถานะ: implemented ใน Agent/Controller/web (2026-11-20) · แก้บั๊ก fatal ที่ทำให้เริ่มใช้งานไม่ได้แล้ว (2026-11-25) — **ต้อง redeploy Controller + Agent package และยังต้องทดสอบบนเครื่องจริงกับบัญชี Parsec ทดสอบก่อนเปิดใช้**

## การแก้ไขล่าสุด (2026-11-25)

ก่อนหน้านี้ flow นี้ไม่มีทางสำเร็จเลยเพราะ 2 บั๊ก:

1. **เว็บปฏิเสธ sealed secret จริงทุกครั้ง** — route `/api/my-leases/{id}/parsec` ใช้ตัวตรวจ `text()` เพดาน 128 ตัวอักษรกับทุกฟิลด์ แต่ RSA-2048 OAEP wrapped key มี 342 ตัวอักษร → ตอบ 400 `Invalid request` ก่อนถึง Controller. แก้เป็นตัวตรวจ base64url เฉพาะ (`wrappedKey ≤ 512`, `iv ≤ 64`, `ciphertext ≤ 8192`) ตรงกับ Controller
2. **Agent ข้ามการล็อกอินหลังรายงาน `starting`** — reporter ต้องได้ JSON `{"accepted":true}` แต่ Controller ตอบ `204 No Content` → Agent ตีว่า attempt ไม่ถูกถืออยู่แล้วและไม่รัน automation; attempt ค้างจนหมดเวลา 150 วิ. แก้ให้ถือว่า 2xx ทุกกรณีสำเร็จ (204/body ว่าง/JSON ไม่มี `accepted`), 4xx = ปฏิเสธถาวร, 408/429/5xx = retry

เพิ่มเติม: attempt ที่หมดอายุถูก mark `failed/TIMEOUT` ทันทีเมื่อมีการอ่านสถานะ (ไม่ต้องรอ Agent sync) และหน้า `/my-machines` แสดง TIMEOUT พร้อมปุ่มลองใหม่แทน spinner ค้าง; ปุ่ม Parsec เปิด dialog ได้แม้เครื่องยังไม่พร้อมเพื่ออ่านเหตุผล; ข้อความ 409 เปลี่ยนเป็นข้อความกลาง ๆ (busy/rate-limit/ยังไม่เปิดใช้) ไม่เดาว่าเป็นอีเมลยืนยัน

**Rollout หลังแก้:** rebuild + publish Controller, build `public/agent/AgentPackage.zip` ใหม่และอัปเดต Agent ทุกเครื่อง, deploy เว็บ แล้วจึงทดสอบ; ถ้า Agent ยังเป็น build เก่า attempt จะยังค้างแบบเดิม

## Troubleshooting (อาการที่เคยเจอ)

| อาการ | สาเหตุ/ทางแก้ |
| --- | --- |
| กดยืนยันแล้วขึ้น `400: คำขอไม่สำเร็จ` | เว็บ build เก่ายังใช้ validator 128 ตัวอักษร — deploy เว็บใหม่ |
| attempt ค้าง `starting` แล้ว `TIMEOUT` | Agent build เก่าไม่รับ 204 — อัปเดต Agent package ทุกเครื่อง |
| ปุ่ม Parsec disabled “ยังไม่พร้อม” | เครื่องยังไม่ approved/active lease, admin ยังไม่เปิด Parsec ของเครื่อง, หรือ Agent ยังไม่รายงาน `parsecd.exe` (ต้องติดตั้งระดับเครื่องที่ `C:\Program Files\Parsec`) |
| ข้อความ busy/rate-limited | มี attempt ค้างหรือกดเกิน 3 ครั้ง/10 นาที/เครื่อง — รอแล้วลองใหม่ |
| ค้างที่ `waiting_email` | ยืนยันอีเมล/IP ตามที่ Parsec ส่ง แล้วกด “ยืนยันอีเมลแล้ว” เพื่อทำรอบสอง |


## หลักการ

- Parsec เป็นช่องทางทางเลือกแทน Moonlight: ลูกค้ากรอก **อีเมล/รหัสผ่าน Parsec ของตัวเอง** ในหน้าเครื่องของฉัน
- เบราว์เซอร์เข้ารหัสด้วย public key ของ Agent (RSA-2048 ต่อ boot) + AES-256-GCM; Controller เห็นแค่ ciphertext
- Agent ถอดรหัสในหน่วยความจำ แล้วสั่ง desktop automation บนเครื่อง (ผ่าน scheduled task ใน session ของ user ที่ล็อกอิน) เพื่อล็อกอิน Parsec
- จบการเช่า: `parsec_cleanup` ปิด parsecd ใน session ลูกค้า, ลบ `app_session_id`/`user.bin` ทั้ง `%APPDATA%\Parsec` และ `%ProgramData%\Parsec`, ล้าง clipboard
- ยังไม่รองรับ Parsec 2FA (จะได้ `TWO_FACTOR_UNSUPPORTED`); อีเมลยืนยันอุปกรณ์จะได้ `WAITING_EMAIL`

## สิ่งที่ image ต้องมี

1. Parsec ติดตั้งแบบทั้งเครื่องที่ `C:\Program Files\Parsec\parsecd.exe` และเปิด Host mode
2. มีผู้ใช้ Windows ล็อกอินค้างที่ desktop (auto-login ของร้าน)
3. Windows OCR ภาษาอังกฤษ (หรือภาษาของโปรไฟล์) และหน้าต่าง Parsec เปิดได้เต็มจอ
4. ตั้ง `PARSEC_LOGIN_LAYOUT` (JSON) เฉพาะเครื่องที่พิกัดคลิกไม่ตรง; ค่าเริ่มต้นอ้างอิงจอ 1920x1080

## Flow (2 รอบ)

1. ลูกค้าเปิดหน้าของฉัน → การ์ดเครื่องที่ active → ปุ่ม "Parsec"
2. เว็บขอ public key จาก Controller (เฉพาะเจ้าของ lease ที่ active)
3. **รอบแรก:** เบราว์เซอร์ seal credentials → `POST /api/my-leases/{rentalId}/parsec` ด้วย `{idempotencyKey, secret}`
4. Controller สร้าง `parsec_attempts` + queue `ParsecConnect` (TTL 150 วิ, ทำงานได้ครั้งละ 1 attempt ต่อ lease, จำกัด 3 ครั้ง/10 นาที/เครื่อง)
5. Agent รับคำสั่ง ตอบรับทันที แล้วรายงานสถานะผ่าน `/agent/parsec-status` (signed): `starting`/`waiting_email`/`ready`/`failed` + error code
6. ถ้า Parsec ขอยืนยันอีเมล/IP (`waiting_email`) เว็บแสดง AlertDialog ค้างไว้จนลูกค้ากด "ยืนยันอีเมลแล้ว"
7. **รอบสอง:** เว็บ seal credentials เดิมอีกครั้ง แล้ว `POST .../parsec` ด้วย `{idempotencyKey ใหม่, secret, retryAttemptId: <attempt แรก>}`
   - Controller ตรวจว่า attempt แรกเป็นของ lease/user/boot key เดียวกันและยัง `waiting_email`
   - mark attempt แรก `failed` + error code `EMAIL_VERIFIED`, invalidate pending `ParsecConnect` ของ lease เดิม
   - queue attempt ใหม่ใน transaction เดียวกัน (ผ่าน unique partial index `lease_parsec_pending`)
   - Agent ล็อกอินอีกรอบโดย Parsec ไม่ติดยืนยันอีเมลแล้ว (ปกติจะได้ `ready`)
8. จบการเช่า/หมดเวลา/shutdown/boot: Agent cleanup; Controller mark attempts ที่ค้างเป็น `LEASE_ENDED`
9. Controller ล้าง payload ที่มี ciphertext เมื่อคำสั่งไม่อยู่ในสถานะ pending แล้ว

## Error codes

ดู `services/Contracts/Parsec.cs` และข้อความไทยใน `src/lib/parsec-contracts.ts` (`INVALID_CREDENTIALS`, `CAPTCHA_REQUIRED`, `APP_NOT_FOUND`, `HOST_NOT_READY`, `TIMEOUT`, `TWO_FACTOR_UNSUPPORTED`, `PARSEC_BUSY`, `EMAIL_VERIFIED`, ฯลฯ)

## ความปลอดภัย

- ห้าม log/เก็บ plaintext credential; สคริปต์ลบตัวเองหลังอ่าน secret จาก named pipe; ไฟล์ชั่วคราวอยู่ใต้ `%ProgramData%\KamenRaider\parsec` พร้อม ACL SYSTEM/Administrators/user และถูกลบหลังใช้
- Agent ไม่ส่งภาพหน้าจอขึ้น Controller; ข้อความสถานะถูกตัด/กรองความยาว 200 ตัวอักษร
- Attempt ผูกกับ boot key: ถ้า Agent รีบูต ใบเก่าจะถูก mark `AGENT_REBOOTED` และถอดรหัสไม่ได้

## ทดสอบก่อน rollout

1. เครื่องที่มี Parsec + auto-login → capability ขึ้นใน heartbeat
2. ล็อกอินบัญชีทดสอบถูก → `READY` ภายใน 70 วิ
3. ล็อกอินจาก IP ใหม่ → รอบแรกได้ `waiting_email`; กด "ยืนยันอีเมลแล้ว" → รอบสองต้องไม่ติดอีเมลและได้ `READY`
4. รหัสผิด → `INVALID_CREDENTIALS`; 2FA → `TWO_FACTOR_UNSUPPORTED`; retry ผิด attempt → 409
5. จบการเช่า → parsecd ใน session ปิด, session files หาย, clipboard ว่าง
6. ตรวจ log/DB: ไม่มีรหัสผ่าน/ภาพหน้าจอ
7. เครื่องที่ไม่มี Parsec → capability ไม่ขึ้น และไม่มีปุ่มบนเว็บ
