# Parsec — ให้ลูกค้าเข้าสู่ระบบด้วยบัญชีของตัวเอง (แบบ 0jay)

สถานะ: implemented ใน Agent/Controller/web (2026-11-20) — **ยังต้องทดสอบบนเครื่องจริงกับบัญชี Parsec ทดสอบก่อนเปิดใช้**

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

## Flow

1. ลูกค้าเปิดหน้าของฉัน → การ์ดเครื่องที่ active → "เชื่อมต่อด้วย Parsec"
2. เว็บขอ public key จาก Controller (เฉพาะเจ้าของ lease ที่ active)
3. เบราว์เซอร์ seal credentials → `POST /api/my-leases/{rentalId}/parsec`
4. Controller สร้าง `parsec_attempts` + queue `ParsecConnect` (TTL 150 วิ, 1 ครั้งต่อ lease, จำกัด 3 ครั้ง/10 นาที/เครื่อง)
5. Agent รับคำสั่ง ตอบรับทันที แล้วรายงานสถานะผ่าน `/agent/parsec-status` (signed): `starting`/`waiting_email`/`ready`/`failed` + error code
6. จบการเช่า/หมดเวลา/shutdown/boot: Agent cleanup; Controller mark attempts ที่ค้างเป็น `LEASE_ENDED`
7. Controller ล้าง payload ที่มี ciphertext เมื่อคำสั่งไม่อยู่ในสถานะ pending แล้ว

## Error codes

ดู `services/Contracts/Parsec.cs` และข้อความไทยใน `src/lib/parsec-contracts.ts` (`INVALID_CREDENTIALS`, `CAPTCHA_REQUIRED`, `APP_NOT_FOUND`, `HOST_NOT_READY`, `TIMEOUT`, `TWO_FACTOR_UNSUPPORTED`, `PARSEC_BUSY`, ฯลฯ)

## ความปลอดภัย

- ห้าม log/เก็บ plaintext credential; สคริปต์ลบตัวเองหลังอ่าน secret จาก named pipe; ไฟล์ชั่วคราวอยู่ใต้ `%ProgramData%\KamenRaider\parsec` พร้อม ACL SYSTEM/Administrators/user และถูกลบหลังใช้
- Agent ไม่ส่งภาพหน้าจอขึ้น Controller; ข้อความสถานะถูกตัด/กรองความยาว 200 ตัวอักษร
- Attempt ผูกกับ boot key: ถ้า Agent รีบูต ใบเก่าจะถูก mark `AGENT_REBOOTED` และถอดรหัสไม่ได้

## ทดสอบก่อน rollout

1. เครื่องที่มี Parsec + auto-login → capability ขึ้นใน heartbeat
2. ล็อกอินบัญชีทดสอบถูก → `READY` ภายใน 70 วิ
3. รหัสผิด → `INVALID_CREDENTIALS`; 2FA → `TWO_FACTOR_UNSUPPORTED`
4. จบการเช่า → parsecd ใน session ปิด, session files หาย, clipboard ว่าง
5. ตรวจ log/DB: ไม่มีรหัสผ่าน/ภาพหน้าจอ
6. เครื่องที่ไม่มี Parsec → capability ไม่ขึ้น และไม่มีปุ่มบนเว็บ
