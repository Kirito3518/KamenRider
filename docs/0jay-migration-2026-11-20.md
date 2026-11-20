# 0jay-style Agent migration (2026-11-20)

สรุปการเปลี่ยนสถาปัตยกรรม Agent/Sunshine/Parsec ที่ทำในรอบนี้ — ยัง **ต้องทดสอบบนเครื่องจริง** ก่อนถือว่าพร้อมเปิดเช่า

## 1) สิ่งที่เปลี่ยน

- **ถอด Stream Guard ออกทั้งหมด**: ไม่มี service `KamenRaider Stream Guard`, ไม่มี firewall block rule `KamenRaider Stream Guard - Sunshine`, ไม่มี named pipe/maintenance mode 45 วินาที
- **Sunshine รันตลอด** (`SunshineService` = Automatic/Running) — Agent ไม่ stop/start service และไม่แตะ firewall
- **ประตูสตรีม = การจับคู่ Sunshine**: Server ส่ง `PairPin` เฉพาะ lease active; จบการเช่า/หมดเวลา/boot → Agent เรียก `POST /api/clients/unpair-all` (ตัดสตรีมทันที) แล้ว shutdown ตามปกติ
- **Readiness = Sunshine liveness**: ตรวจ 2 พอร์ตทุก 5 วินาที (`base/serverinfo` + `base+1/api/apps`, พลาด 3 ครั้งติดจึงออกจากชั้นวาง); diagnostic `streamFence`/`runtimeHealth` สื่อความหมายใหม่นี้, ส่วน baseline/restoration/desktopHealth เป็น advisory
- **Agent service = LocalSystem** ไม่มี dependency; ติดตั้ง/อัปเกรดด้วย `scripts/agent/Remove-StreamGuard.ps1` (idempotent, backup/safe failure)
- **Parsec**: ลูกค้าใช้บัญชี Parsec ของตัวเอง; เบราว์เซอร์เข้ารหัสด้วย boot RSA key (RSA-OAEP SHA-256 + AES-256-GCM), Agent ถอดใน memory และรัน desktop automation; cleanup ตอนจบ
- **Admin maintenance commands**: `RestartSunshine` / `UnpairSunshine` (machine-scoped) แทน maintenance mode เดิม; ส่งถึง Agent ได้แม้ `notReady`

## 2) Migration/ฐานข้อมูล

- `026-sunshine-maintenance-commands.sql`: command types `RestartSunshine`/`UnpairSunshine` + admin action audit types
- `027-parsec-attempts.sql`: `machines.parsec_enabled`, `machine_parsec_state`, `parsec_attempts`, command type `ParsecConnect`, purge/partial indexes
- Controller schema ปัจจุบัน = **27**
- Command/contract ใหม่: `ParsecConnect` payload `{attemptId, deadlineAtUtc, secret{wrappedKey,iv,ciphertext}}`, TTL 150 วิ, 1 attempt/lease, 3 ครั้ง/10 นาที/เครื่อง
- Endpoints: `GET/POST /internal/leases/{id}/parsec`, `GET .../parsec/{attemptId}`, `POST /agent/parsec-status` (signed), `PUT /internal/machines/{id}/parsec`; web proxy ใต้ `/api/my-leases/{rentalId}/parsec`

## 3) ลำดับติดตั้ง/อัปเกรด

1. Deploy Controller + web (รับ protocol ใหม่; Agent เก่าจะยังไม่เป็น ready เพราะไม่ส่ง `StreamReady`)
2. อัปเดต Agent package (`Build-AgentPackage.ps1`) แล้วติดตั้งบนเครื่องลูกด้วย admin install command
3. เครื่องที่มี Guard เดิม: installer/updater เรียก `Remove-StreamGuard.ps1` — หยุด/ลบ Guard, ลบ rule, ถอด dependency, ตั้ง Agent เป็น LocalSystem, คืน Sunshine Automatic/Running, restart Agent ตามสถานะเดิม (backup อยู่ที่ `%ProgramData%\KamenRaider\StreamGuardMigration\`)
4. ตั้ง image prerequisites สำหรับ Parsec (ถ้าเปิดใช้) และ hardening ตาม `docs/sunshine-local-control.md`

## 4) Parsec (สรุป)

- image: `C:\Program Files\Parsec\parsecd.exe`, Hosting เปิด, มี Windows user ล็อกอินหน้าเครื่อง, Windows OCR, หน้าต่างเต็มจอ
- admin เปิด toggle “เปิดให้ลูกค้าเข้าสู่ระบบ Parsec” ต่อเครื่อง; capability จะขึ้นเมื่อ Agent ตรวจพบ parsecd
- ลูกค้ากรอกอีเมล/รหัสในหน้าเครื่องของฉัน → ดูสถานะ starting/waiting_email/ready/failed
- 2FA ไม่รองรับ (`TWO_FACTOR_UNSUPPORTED`); อีเมลยืนยันอุปกรณ์จะได้ `WAITING_EMAIL`
- จบการเช่า/boot: ปิด parsecd ใน session ลูกค้า, ลบ `app_session_id`/`user.bin` ทั้งสองโฟลเดอร์, clear clipboard
- ดูรายละเอียดใน `docs/parsec.md`

## 5) หลักฐานการตรวจ (2026-11-20)

| รายการ | ผล |
|---|---|
| `dotnet build services/KamenRaider.slnx` | ผ่าน 0 warning 0 error |
| Agent tests | 395 passed / 1 skipped / 0 failed |
| Controller tests | 984 passed / 5 skipped / 3 failed — 3 เป็น baseline ที่มีอยู่ก่อน (AgentRecovery Unicode literal, AgentReleaseStore 2 เคสที่ต้องมี pwsh.exe/ฟังก์ชัน fixture ที่ไม่มีใน HEAD) |
| PowerShell installer/plan tests (Windows PowerShell 5.1) | 11/11 ผ่าน (รวม `Test-RemoveStreamGuardPlan.ps1`) |
| Web `pnpm exec tsc --noEmit` / `pnpm lint` / `pnpm build` | ผ่าน (lint 0 error, warning เดิม) |
| Web machines (vitest) | 19/19; rentals group 40/40 (หลังแก้ mock one-liner ที่ล้าสมัย) |
| `tests/machines.test.mjs` (`run-machines.mjs`) | ยังมี 1 baseline failure: not-owner extend คาด 403 แต่ได้ 404 จาก local payment lookup — ไม่เกี่ยวกับ Stream Guard/Parsec |
| `public/agent/AgentPackage.zip` | rebuild จาก HEAD+working tree, SHA-256 `9354DF0499D64DD57A9CE1D7B1BE54DF79329E1C88A134473E2C3D5E29850E91` (dirty build) |

> หมายเหตุ: `Test-AgentPackageBuild.ps1` ต้องรันด้วย PowerShell 7 (เครื่องนี้มีแต่ Windows PowerShell 5.1)

## 6) ยังต้องทดสอบบนเครื่องจริง (ห้ามถือว่าผ่านจาก unit test)

1. Fresh install + upgrade จากเครื่องที่มี Guard เดิม (identity/enrollment/ACL เดิม, Sunshine กลับมา Running)
2. เช่า → pair → stream → จบ → `named_certs` ว่าง ≤10 วิ; expiry → shutdown; reboot → ว่าง
3. หยุด/เปิด SunshineService ระหว่างเครื่องว่าง → notReady/ready ภายใน ~20 วิ
4. Parsec: login จริงด้วยบัญชีทดสอบ (READY ≤70 วิ), รหัสผิด, 2FA, cleanup ตอนจบ, ตรวจว่าไม่มี credential ใน log/ไฟล์
5. Image hardening: `origin_web_ui_allowed=pc` + per-user outbound block พอร์ต Sunshine web UI
6. Admin `RestartSunshine`/`UnpairSunshine` บนเครื่อง notReady และผลลัพธ์ใน PC Control
7. Reboot Restore Rx / cold boot / WOL / external network — ตามข้อจำกัดเดิมของโปรเจกต์

## 7) ข้อจำกัดที่ยอมรับ

- Agent รันเป็น LocalSystem (จำเป็นสำหรับ Parsec desktop automation) — เพิ่มสิทธิ์จาก virtual service account เดิม
- readiness ไม่รอ baseline/restoration/desktop evidence ที่ยัง unqualified; admin ยังเห็น diagnostic เป็นข้อมูล
- Parsec UI automation เปราะตามหน้าจอ/OCR; ต้องมี layout override และ live acceptance
- ยังไม่มี Parsec 2FA และไม่ได้ทดสอบบนเครื่องจริงในรอบนี้
