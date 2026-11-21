# ระบบเช่าจริงและประวัติการเช่า (Rental & history)

สถานะ: ใช้งานได้จริงแบบ prepaid จากยอดเงินสมาชิก; ระบบเติมเงินออนไลน์เพิ่มใน [topup.md](topup.md)
อัปเดต: 2026-11-20

## ภาพรวม

- ลูกค้าเลือกเครื่องที่หน้า `/rent` กำหนดจำนวนชั่วโมง แล้วกด **ยืนยันการเช่า** ระบบจะหักค่าเช่าจาก `user.balanceSatang` ก่อนสร้าง lease ผ่าน Controller
- Controller ยังเป็นเจ้าของสถานะ/เวลา/การจับคู่ของ lease ทั้งหมด; ฝั่งเว็บเป็นเจ้าของยอดเงินและบันทึกการชำระเงิน (ดู [full-rental-roadmap.md](full-rental-roadmap.md))
- เมื่อสิ้นสุดการเช่า (หมดเวลา, แอดมินปิดเครื่อง/ยกเลิก/ลดเวลา, reboot หรือ Agent ล้มเหลว) ระบบคืนเงินตามเวลาที่ไม่ได้ใช้ให้อัตโนมัติแบบ prorate
- มีประวัติการเช่าทั้งฝั่งลูกค้า `/history` และแอดมิน `/admin/rentals`
- การเติมเงินทำได้ที่ `/topup` (TrueMoney, PromptPay QR/โอนธนาคาร, คูปอง UI) และยังมีแอดมินปรับยอดที่ `/admin/users` ได้; ดู [topup.md](topup.md)

## กติกาเงินและเวลา

| กติกา | ค่า |
| --- | --- |
| ราคาต่อชั่วโมง | จำนวนเต็มบาทเท่านั้น (เช่น ฿10, ฿20, ฿40) เก็บใน Controller เป็นสตางค์ `hourly_price = บาท × 100`; ราคาที่ไม่ใช่จำนวนเต็มบาทจะถูกปฏิเสธและไม่แสดงเป็นเครื่องพร้อมเช่า |
| จำนวนชั่วโมง | จำนวนเต็ม 1–24 ชั่วโมงต่อการสร้างรายการ และ 1–24 ชั่วโมงต่อการต่อเวลา (ไม่รับนาที/วินาทีจากผู้ใช้) |
| ยอดชำระเริ่มต้น | `hourlyPriceSatang × ชั่วโมง` (เป็นบาทเต็ม) หักทันทีตอนสร้างรายการ |
| ยอดต่อเวลา | `hourlyPriceSatang × ชั่วโมงที่ต่อ` (ใช้ราคา snapshot ของรายการนั้น) หักทันทีตอนต่อเวลา |
| เงินคืน | prorate ตามเวลาจริงเป็นวินาที และคืนเป็นสตางค์ (อาจมีเศษสตางค์ในยอดเงิน) |

ตัวอย่างการคืนเงิน: เช่า 2 ชม. ราคา ฿40/ชม. (฿80) ใช้ไป 1 ชม. 20 นาที → เหลือ 40 นาที → คืน `floor(8000 × 2400 / 7200) = 2666` สตางค์ (฿26.66)

### กติกาคืนเงินแบบละเอียด

1. **เริ่มนับเวลาแล้ว** (`startsAtUtc` มีค่า) และมี `endedAtUtc`: `usedMs = clamp(endedAt − startsAt, 0, paidSeconds×1000)` แล้ว `refund = floor(paidSatang × (paidSeconds×1000 − usedMs) / (paidSeconds×1000))`
2. **ไม่เคยเริ่ม** (เช่น เครื่องเตรียมไม่สำเร็จ หรือถูกปิดก่อนเริ่ม): คืนเต็มจำนวนที่ชำระ
3. **ยังไม่สิ้นสุด** (`active`/`preparing`): ยังไม่คืน
4. เพดาน: คืนไม่เกินยอดที่ชำระและไม่เกินยอดที่ยังไม่เคยคืน; เรียกซ้ำไม่คืนซ้ำ (CAS ด้วย `settled_at IS NULL`)
5. หมดเวลาตามกำหนด → คืน 0; แอดมินเพิ่มเวลา (ฟรี) → คืน 0; แอดมินลดเวลา/ยกเลิก/ปิดเครื่อง/reboot/Agent ล้มเหลวหลังเริ่ม → คืนตามเวลาที่ไม่ได้ใช้

`paidSeconds = durationSeconds + Σ extension.additionalSeconds (สถานะ charged)` และ `paidSatang = amountSatang + extensionSatang` ตามที่บันทึกใน payment row (extension ที่ถูกปล่อย/คืนแล้วจะถูกหักออกจากยอดนี้)

## ที่เก็บข้อมูล

### Controller (schema 25)

Migration `025-lease-ended-at.sql` เพิ่ม `leases.ended_at INTEGER` (nullable) ใช้บันทึกเวลาสิ้นสุดจริงครั้งแรกเมื่อ lease ออกจากช่วง active:

| จุดเปลี่ยนสถานะ | `ended_at` |
| --- | --- |
| หมดเวลาตามกำหนด (active → ending) | `expires_at` |
| เตรียมเครื่องไม่ทัน preparation deadline | เวลาที่ตรวจพบ |
| Agent รายงานผลล้มเหลว | เวลาที่ตรวจพบ |
| แอดมินปิดเครื่อง / ยกเลิกการเช่า | เวลาที่คำขอถูกบันทึก |
| ตรวจพบ boot ใหม่ (reboot) | เวลาที่ตรวจพบ |
| Reconcile หลังบูต (preparing → failed / ending → completed) | เวลาที่ตรวจพบ |

`LeaseDto` เพิ่ม `EndedAtUtc` และ Controller มี endpoint สำหรับแอดมิน `GET /internal/leases?take=N` (1–500, ใหม่→เก่า)

### Auth DB (Next.js)

ตารางที่สร้างแบบ lazy ใน `src/lib/rental-payments.ts`:

- `rental_payments` — หนึ่งแถวต่อหนึ่งรายการเช่า: id (= Controller idempotency key), userId, machine snapshot, ราคา/ชั่วโมง (สตางค์, หาร 100 ลงตัว), duration, amount, extension seconds/amount, refunded, status (`pending`/`confirmed`/`rejected`), rejectReason, refundedAt/refundNote, settledAt, timestamps
- `rental_payment_requests` — idempotency ของการสร้างรายการ `(userId, requestKey) → paymentId + fingerprint`
- `rental_extension_payments` — การต่อเวลาแต่ละครั้ง `(paymentId, requestKey)` พร้อม `status` (`charged`/`released`)

ไม่มีการแก้ schema ของ better-auth และไม่เก็บข้อมูลบัตร/บัญชีธนาคาร

## Flow

### สร้างรายการ (`POST /api/rentals`)

1. ตรวจ session/origin, อ่าน `/internal/machines`, ตรวจว่าเครื่อง approved + `canRent` + ราคาเต็มบาท
2. เปิด immediate transaction ใน auth DB: ตรวจ idempotency → ตรวจยอดพอ → หักยอด → insert payment (`pending`) + request map
3. เรียก Controller `POST /internal/leases` ด้วย `idempotencyKey = payment.id`
4. ผลลัพธ์: สำเร็จ → `confirmed`; 4xx → คืนเงินเต็ม + `rejected`; 503/timeout → retry 2 ครั้งแล้วคง `pending` (ตอบ 503)
5. ยอดไม่พอ → HTTP 402 `insufficient-funds` โดยไม่สร้าง lease

### ต่อเวลา (`POST /api/my-leases/{id}/extend`)

1. บังคับ `additionalSeconds` เป็นพหุคูณของ 3600 และ 1–24 ชม.
2. ตรวจ payment ของเจ้าของ → หักเงิน + บันทึก extension (`charged`)
3. เรียก Controller extend; 4xx → ปล่อย extension คืนเงินทันที; 503 → คงเงินที่หักไว้ (retry ด้วย key เดิมได้ ไม่หักซ้ำ) และส่วนที่ไม่ได้ใช้จะถูกคืนตอนสิ้นสุด

### คืนเงินอัตโนมัติ

`GET /api/my-leases`, `GET /api/rentals` และ `GET /api/admin/rentals` เรียก `settleRentalRefunds` ก่อนแสดงผล: หา payment ที่ `confirmed` และ `settled_at IS NULL` ที่ lease สิ้นสุดแล้ว → คำนวณ/คืนเงิน + mark settled (CAS กันคืนซ้ำ) แล้วจึงประกอบประวัติ

`reconcilePendingRentals` จะลองสร้าง lease ซ้ำด้วย idempotency key เดิมเมื่อ payment ยัง `pending` (Controller คืน lease เดิมได้ถ้าสร้างสำเร็จไปแล้ว)

### ประวัติการเช่า

- `GET /api/rentals` (เจ้าของ): `{ balanceSatang, controllerAvailable, summary, entries }` — ถ้า Controller ล่มจะ fallback แสดงข้อมูลการชำระเงินโดย `state: "unknown"` และ `controllerAvailable: false`
- `GET /api/admin/rentals` (แอดมิน): join lease ล่าสุด 500 รายการ + payment + ชื่อ/อีเมลสมาชิก; `summary` มีรายได้สุทธิ/คืนเงินทั้งหมดจาก payment table
- `GET /api/my-leases`: ข้อมูล lease สำหรับหน้า `/my-machines` พร้อม `payment` ต่อรายการ และ entry `pending` สำหรับการเช่าที่ยังรอยืนยัน
- `/my-machines` กรองแสดงเฉพาะ `state=active`; รายการ `pending`/`preparing` แสดงเป็นแถบ "กำลังเตรียมเครื่อง"; รายการที่สิ้นสุดแล้วดูได้ที่ `/history`
- Parsec เป็น flow 2 รอบ: รอบแรกขออีเมลยืนยัน IP, รอบสองส่งด้วย `retryAttemptId` หลังลูกค้ายืนยันอีเมล (รายละเอียดใน [parsec.md](parsec.md))

## หน้าเว็บ

| หน้า | ลูกค้าเห็น |
| --- | --- |
| `/rent` | ราคาเต็มบาท/ชม., จำนวนชั่วโมงเต็ม, ยอดรวม, ยอดเงินคงเหลือ, ปุ่มยืนยันการเช่า (ต้อง login) |
| `/my-machines` | แสดงเฉพาะเครื่องที่กำลังเช่า (state `active`); มี Tabs ตามชื่อเครื่องเมื่อเช่าหลายเครื่อง; สถานะ/เวลา/ราคา, ต่อเวลาผ่าน dialog, เชื่อมต่อ Moonlight (PIN) หรือ Parsec (2 รอบ) ผ่าน dialog, ยอดเงินคงเหลือ |
| `/history` | 2 Tabs: ประวัติการเติมเงิน และประวัติการเช่า (เวลาเริ่ม–สิ้นสุดจริง, ยอดชำระ/คืน/สุทธิ, สถานะ, ค้นหา/กรอง/แบ่งหน้า) |
| `/admin/rentals` | Dashboard ประวัติการเช่า: stat cards, กราฟรายได้/จำนวน, ผู้เช่า/เครื่อง/เวลา/ยอดชำระ/คืน/สุทธิ, ค้นหา/กรอง/เรียง/แบ่งหน้า, รายละเอียด |

## ข้อจำกัดและ edge cases

- ระบบเติมเงินออนไลน์อยู่ใน [topup.md](topup.md); ยังไม่มีใบเสร็จ/ภาษี
- ไม่มีปุ่มให้ลูกค้ายกเลิกเอง/ขอคืนเงินเอง; แอดมินใช้ PC Control หรือปรับยอดสมาชิกพร้อมเหตุผล
- ถ้า Controller DB ถูก restore จนไม่มี lease แต่ payment ยัง confirmed ระบบจะไม่คืนเงินอัตโนมัติ (แสดง "ไม่ทราบสถานะ") — ควรตรวจกับ Controller ก่อน
- รายการเช่าที่สร้างก่อนมีระบบชำระเงิน (legacy) แสดงในประวัติโดยไม่มีตัวเลขเงินและต่อเวลาไม่ได้
- การคืนเงินทำงานเมื่อมีผู้เปิดหน้า history/poll; ไม่มี background job
- ฝั่งแอดมินดู lease ล่าสุด 500 รายการ; pagination ทั้งหมดเป็นงานอนาคต
- เงินคืนคิดตามเวลาจริงเป็นวินาที จึงอาจมีเศษสตางค์ในยอดเงินสมาชิก (การชำระเป็นบาทเต็มเสมอ)

## คำสั่งตรวจสอบ

```powershell
# Controller: schema 25, migration, ended_at, whole-baht validation
dotnet test services\Controller.Tests

# Next: ledger + routes + UI
pnpm test:rentals

# typecheck / lint / build
pnpm exec tsc --noEmit
pnpm lint
pnpm build
```

ในฐานข้อมูล:
```powershell
# Controller
& 'C:\Users\User\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe' -c "import sqlite3;c=sqlite3.connect(r'file:C:\Users\User\Documents\kamenraiderplay\data\controller.sqlite?mode=ro',uri=True);print(c.execute('pragma user_version').fetchone()[0]);print([r[1] for r in c.execute('pragma table_info(leases)')])"

# Auth DB: ตาราง/ยอดเงิน/รายการชำระ
& 'C:\Users\User\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe' -c "import sqlite3;c=sqlite3.connect(r'file:C:\Users\User\Documents\kamenraiderplay\data\auth.sqlite?mode=ro',uri=True);print(c.execute('select id,balanceSatang from user limit 10').fetchall());print(c.execute('select status,amount_satang,refunded_satang from rental_payments order by rowid desc limit 10').fetchall())"
```
