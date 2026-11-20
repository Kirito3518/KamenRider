# ระบบเติมเงินและประวัติธุรกรรม (Top-up & history)

สถานะ: พร้อมใช้งานฝั่งเว็บ (mocked provider tests ผ่าน); live provider E2E ต้องตั้งค่า API Key จริงก่อน
อัปเดต: 2026-11-20

## ภาพรวม

- ลูกค้าเติมเงินได้ 3 ช่องทาง: **TrueMoney Angpao**, **ธนาคาร** (PromptPay QR หรือโอนบัญชีธนาคาร — Admin เปิดได้ครั้งละหนึ่งรูปแบบ) และ **คูปอง** (UI เท่านั้น)
- QR PromptPay สร้างผ่าน **EasySlip เท่านั้น**
- การตรวจสลิปใช้ผู้ให้บริการ **ตัวเดียวตาม Settings** (`easyslip` หรือ `slipok`) ไม่มี per-transaction selector, manual fallback หรือ automatic fallback
- Wallet ใช้ `user.balanceSatang` เดิม; การเพิ่มยอดเกิดหลังตรวจสอบผ่านเท่านั้น และเกิดครั้งเดียวด้วย Database transaction + CAS + unique constraints
- ประวัติ: `/history` (ลูกค้า, 2 tabs), `/admin/topups`, `/admin/rentals`, ตั้งค่าที่ `/admin/settings/topup`

## ช่องทาง

| ช่องทาง | ผู้ใช้ทำอะไร | ระบบทำอะไร |
|---|---|---|
| TrueMoney | วางลิงก์ซองอั่งเปา/รหัสซอง | เรียก TrueMoney gateway ด้วยเบอร์ผู้รับจาก Settings, credit ตามยอดที่ API ยืนยัน |
| PromptPay QR | กรอกจำนวน → สแกน QR → อัปโหลดสลิป | EasySlip สร้าง QR; ตรวจสลิปตาม provider ที่เลือก; ตรวจยอด/ผู้รับ/วันที่/สลิปซ้ำ |
| โอนบัญชีธนาคาร | เลือกบัญชี + จำนวน → โอน → อัปโหลดสลิป | snapshot บัญชี; ตรวจสลิปด้วย flow เดียวกับ QR |
| คูปอง | กรอกรหัส + ยืนยัน | แสดงข้อความจาก Admin เท่านั้น; ยังไม่มี API/ตารางคูปอง |

- ขั้นต่ำ/สูงสุดของ QR และโอนบัญชี: **20 – 100,000 บาท** (2,000 – 10,000,000 สตางค์) กำหนดใน `src/lib/topup/constants.ts`
- QR อายุ **15 นาที**; รายการโอนบัญชี **24 ชั่วโมง**; TrueMoney pending สูงสุด **30 นาที**
- TrueMoney credit ตามยอดจริงที่ API ส่งกลับ (ไม่ใช้ยอดจาก client); ช่องทางนี้ไม่ใช้ขั้นต่ำ/สูงสุดของฟอร์ม

## Admin Settings

`/admin/settings/topup` แบ่ง Tabs:

1. **TrueMoney**: เปิด/ปิด, เบอร์ผู้รับ 10 หลัก
2. **PromptPay QR**: เปิด/ปิด, ประเภท `msisdn`/`natId`, หมายเลข, ชื่อบัญชีสำหรับตรวจสลิป
3. **บัญชีธนาคาร**: เปิด/ปิดช่องทาง, เพิ่ม/แก้ไข/archive บัญชี, ชื่อธนาคาร/รหัส/เลขบัญชี/ชื่อบัญชี, โลโก้ preset หรืออัปโหลด (PNG/JPEG/WebP ≤ 32 KB)
   - Bank Select ใช้ preset: BBL `002`, KBANK `004`, KTB `006`, TMB `011`, SCB `014`, BAY `025`, GSB `030`, KK `069` พร้อมโลโก้จาก `public/topup/bank/`; ยังเพิ่มธนาคารอื่นด้วยรหัส/ชื่อเองได้
4. **ตรวจสอบสลิป**: เลือกผู้ให้บริการ (ค่าเดียว), EasySlip API Key, SlipOk API Key + Branch ID, ปุ่มทดสอบการเชื่อมต่อ
5. **คูปอง**: เปิด/ปิด + ข้อความแจ้ง

ข้อบังคับเวลาเปิดช่อง:
- PromptPay ต้องมี EasySlip API Key (ใช้สร้าง QR)
- การตรวจสลิปต้องมี key ของ provider ที่เลือก (SlipOk ต้องมี Branch ID)
- โอนบัญชีต้องมีบัญชีที่ active อย่างน้อย 1 บัญชี
- PromptPay และโอนบัญชีเปิดพร้อมกันไม่ได้; เปิดอันหนึ่งจะปิดอีกอัน
- ห้ามลบ/เปลี่ยน key หรือสลับ provider ขณะมีรายการรอตรวจสลิป
- API Key ถูกเข้ารหัส AES-256-GCM ก่อนเก็บ; API คืนเฉพาะ `maskedKey` + `configured` ไม่เคยคืนค่าจริง

## Provider APIs

| Provider | ใช้ | เอกสาร |
|---|---|---|
| TrueMoney Angpao | รับเงินจากซอง | https://tmn-angpao.pages.dev/ |
| EasySlip | สร้าง QR PromptPay, ตรวจสลิป, test `/v1/me` | https://document.easyslip.com/th/v1/qr/generate, https://document.easyslip.com/th/v1/verify/bank/image |
| SlipOk | ตรวจสลิป, test quota | https://slipok.com/api-documentation/check-slip/ |

- EasySlip ตรวจสลิปครั้งแรกด้วย `checkDuplicate=true`; กรณี timeout แล้วผู้ใช้กด retry จะใช้ `checkDuplicate=false` เพื่อกู้ข้อมูลสลิป (ยังเป็น provider เดิม; duplicate ยังถูกกันด้วย hash/transRef ฝั่งเรา)
- SlipOk ส่ง `log=false` เพื่อไม่ผูกการตรวจกับบัญชี LIFF และใช้ amount/บัญชี/สลิปซ้ำจากฝั่งเราเป็นหลัก
- timeout ผู้ให้บริการ 20 วินาที; ไม่มี auto retry
- TrueMoney timeout/5xx ไม่ retry อัตโนมัติ เพราะอาจ claim เงินไปแล้ว — ระบบตั้งสถานะ `processing` เพื่อให้ Admin ตรวจสอบ

## Database

สร้างแบบ lazy migration ใน auth DB (`data/auth.sqlite`) ผ่าน `ensureTopupSchema`:

| ตาราง | หน้าที่ |
|---|---|
| `topup_schema_migrations` | version ของ schema เติมเงิน |
| `topup_settings` | ค่าตั้งที่ไม่ลับ (singleton id=1) |
| `topup_provider_settings` | API key ที่เข้ารหัส, branchId, ผลทดสอบล่าสุด |
| `topup_bank_accounts` | บัญชีรับโอน + logo (archive แทนการลบ) |
| `topup_transactions` | รายการเติมเงิน + snapshot ผู้รับ/QR/slip/verification |
| `wallet_transactions` | ledger ของการเพิ่มยอดจาก topup |
| `slip_verification_attempts` | ทุกครั้งที่เรียก provider (รวม TrueMoney claim) + response excerpt |
| `topup_events` | timeline การเปลี่ยนสถานะ |
| `topup_settings_audit` | audit การแก้ settings/provider/bank account |

Unique/กันซ้ำหลัก:
- `topup_transactions.reference` unique
- `UNIQUE(user_id, idempotency_key)` สำหรับ request ซ้ำ
- partial unique `slip_trans_ref`, `slip_image_hash`, `truemoney_voucher_id`
- `UNIQUE(reference_type, reference_id, type)` ใน `wallet_transactions`
- บัญชีธนาคาร unique `(bank_code, account_number)` เมื่อยังไม่ archive

ไม่แตะตาราง better-auth หรือ `rental_payments`; ไม่ทำ FK user→transaction เพื่อไม่ให้การลบ/ย้ายบัญชีทำลายประวัติเงิน (เก็บ snapshot + index แทน)

## สถานะ

| สถานะ | ความหมาย | เพิ่มเงิน |
|---|---|---|
| `pending` | รอชำระ/รออัปโหลดสลิป | ไม่ |
| `processing` | กำลังตรวจสลิป/claim, หรือผลยังไม่ทราบ (timeout) | ไม่ |
| `completed` | ตรวจผ่านและ credit แล้ว | ครั้งเดียว |
| `failed` | invalid/duplicate/amount mismatch/recipient mismatch/error | ไม่ |
| `expired` | QR/รายการหมดอายุก่อนเริ่มตรวจ | ไม่ |

- `verification_status`: `pending`, `verified`, `rejected`, `duplicate`, `error`, `unknown`
- processing ที่ค้างเกิน 2 นาทีหรือ `unknown` ผู้ใช้ retry ได้; จำกัด 5 ครั้ง/รายการ
- Admin re-verify ได้เฉพาะรายการสลิปที่ค้าง (ไม่มีการเติมเงินด้วยมือ)

## การตรวจสอบความถูกต้องของสลิป

ก่อน credit ต้องผ่านทั้งหมด:
1. **ยอดเงิน** ตรงกับ `requested_satang` เป๊ะ
2. **บัญชีผู้รับ**:
   - PromptPay: proxy ตรง (full/masked) หรือชื่อผู้รับตรง
   - โอนบัญชี: bank code ตรง + เลขบัญชีตรง (full/masked last4) หรือชื่อผู้รับตรง
3. **วันที่สลิป** อยู่ในช่วง transaction −10 นาที ถึง now +5 นาที (ถ้า provider ให้เวลา)
4. **ไม่ซ้ำ**: hash รูป + provider transRef + voucher unique
5. มี `transRef` จาก provider (ถ้าไม่มีจะไม่ credit)

ชื่อเทียบแบบ normalize คำนำหน้า/ช่องว่าง/`*`; เลขบัญชีเทียบเลขที่มองเห็น (masked) ตามกฎใน `src/lib/topup/verify.ts`

## API

Customer (session required):
- `GET /api/topups/config`
- `GET /api/topups?q=&channel=&status=&period=&from=&to=&sort=&page=`
- `POST /api/topups/truemoney` `{requestId, link}`
- `POST /api/topups/promptpay` `{requestId, amountSatang}`
- `POST /api/topups/bank` `{requestId, amountSatang, bankAccountId}`
- `GET /api/topups/[id]`
- `POST /api/topups/[id]/slip` multipart `file`
- `POST /api/topups/[id]/retry`
- `GET /api/topups/[id]/slip` (owner/admin)

Admin (`role=admin`):
- `GET /api/admin/topups` (stats/chart/table/filters)
- `GET /api/admin/topups/[id]` (attempts + response excerpt)
- `POST /api/admin/topups/[id]/verify`
- `GET/PUT /api/admin/topup-settings`
- `PUT /api/admin/topup-providers/[provider]`
- `POST /api/admin/topup-providers/[provider]/test`
- `GET/POST /api/admin/topup-bank-accounts`
- `PATCH/DELETE /api/admin/topup-bank-accounts/[id]`

ทุก route ใช้ `src/lib/api-guard.ts` (session, admin role, same-origin สำหรับ non-GET) และตอบ `Cache-Control: no-store`

## ไฟล์/ที่เก็บข้อมูล

- API key เข้ารหัสใน auth DB; key derivation จาก `TOPUP_SETTINGS_KEY` → `BETTER_AUTH_SECRET` → `data/.auth-secret` → `data/.topup-secret`
- รูปสลิปเก็บที่ `TOPUP_SLIP_DIR` (default `dirname(AUTH_DATABASE_PATH)/topup-slips`, สิทธิ์ 0700) และเสิร์ฟผ่าน route ที่ตรวจ owner/admin เท่านั้น
- Backup: สำรอง `auth.sqlite`, secret sidecar (`.auth-secret` หรือ `.topup-secret`) และโฟลเดอร์ slip ไปพร้อมกัน

## Environment

```dotenv
# optional: override provider base URLs (server-only)
EASYSLIP_API_URL=https://api.easyslip.com
SLIPOK_API_URL=https://api.slipok.com
TRUEMONEY_ANGPAO_API_URL=https://tmn-angpao.pages.dev/api

# optional: base64 32-byte key for encrypting stored API keys at rest
# TOPUP_SETTINGS_KEY=
# optional: absolute slip directory
# TOPUP_SLIP_DIR=
```

## การทดสอบ

```powershell
pnpm test:topups
pnpm test:rentals
pnpm exec tsc --noEmit
pnpm lint
# Use a separate dist dir while the dev server owns .next-local.
$env:NEXT_DIST_DIR='.next-verify'; pnpm build
```

> `pnpm build` runs `next build --webpack`. Webpack obeys `outputFileTracingExcludes`
> for `data/**`; the current Turbopack production trace tries to hash the live
> Controller SQLite lock file and aborts the build while the dev Controller runs.
> Webpack uses `@tailwindcss/postcss` via `postcss.config.mjs`; Turbopack dev keeps
> the existing `@tailwindcss/turbopack` loader from `next.config.ts`.

- Tests ใช้ mocked provider responses; live E2E ต้องตั้ง key จริงใน Admin แล้วทดสอบด้วยยอดเล็ก ๆ
- `tests/topup-routes.test.ts` ครอบคลุม auth/origin/owner, idempotency, QR, slip success/amount mismatch/duplicate/timeout+retry, admin routes, no key leak

## ข้อจำกัด

- TrueMoney timeout หลัง claim: ไม่ auto-credit; ต้องให้ Admin ตรวจกับผู้ให้บริการ (ไม่มี status API)
- สลิปที่ failed แล้วไม่สามารถนำไปใช้กับรายการใหม่ได้ (strict duplicate guard) — Admin ต้องตรวจกับธนาคาร
- คูปองยังไม่มี backend ตามข้อกำหนด
- Admin rental history ใช้ lease window ล่าสุด 500 รายการจาก Controller เดิม
- ไม่มี manual credit/approve ใน Admin; ทุกการเพิ่มเงินต้องผ่าน provider verification
