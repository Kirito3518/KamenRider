# PC Control (แอดมินควบคุมเครื่องที่ให้เช่า)

`/admin/pc-control` เป็นหน้าหลักสำหรับดูและสั่งงานเครื่องทุกเครื่องที่ลงทะเบียนไว้ โดยแต่ละเครื่องแสดงเป็นการ์ดหนึ่งใบและมีจำนวนการ์ดเท่ากับจำนวนเครื่องในระบบเสมอ (รวมเครื่องออฟไลน์/รออนุมัติ)

## สิ่งที่ทำได้จากการ์ด

- **สถานะเครื่อง/Agent**: ออนไลน์-ออฟไลน์, readiness, เวอร์ชัน Agent, สถานะ Agent update, สถานะการเช่า
- **จอ VNC แบบ on-demand**: ค่าเริ่มต้นซ่อนไว้ กด “เปิดจอ” แล้วจึงขอ ticket/เปิด viewer; กด “ปิดจอ” เพื่อหยุด; เปิดได้ครั้งละหนึ่งจอต่อหน้า
- **เปิดเครื่อง**: ส่ง Wake-on-LAN ผ่าน Controller (ปุ่มถูกปิดเมื่อเครื่องออนไลน์อยู่)
- **ปิดเครื่อง**: บังคับปิดได้แม้กำลังเช่า; ระบบจะสิ้นสุด rental เป็น `ending` ทันที **ไม่คืนเงิน** และส่งคำสั่ง `ShutdownMachine` ไปยัง Agent; allocation hold จะถูกปลดเมื่อ Agent ยืนยันการสิ้นสุด/รีบูต
- **เชื่อมต่อพิน**: แอดมินดู pairing slots ของ rental ปัจจุบัน กรอก PIN 4 หลัก แล้วส่งแทนผู้เช่าได้ (PIN เก็บเฉพาะในหน่วยความจำของ Controller ไม่ลง SQLite/log)
- **เพิ่ม/ลดเวลา**: ปุ่ม ±1 ชม. และ dialog กำหนดเอง; ลดเวลาได้โดย **ไม่มีการคืนเงิน/ปรับยอด**; ต้องมี revision ตรงและไม่มีคำสั่งค้าง
- **ยกเลิกการเช่า**: ส่งคำสั่ง `EndLease` ให้ Agent ปิด stream gate/เคลียร์ lease; allocation hold ถูกปลดเมื่อ Agent ตอบรับ
- **แสดงผู้เช่าและเวลาที่เหลือ**: ชื่อ/อีเมลผู้เช่า, เวลาคงเหลือ และเวลาหมดอายุ
- **สีและกลุ่มปุ่ม**: การ์ดแบ่งปุ่มเป็นกลุ่ม “เครื่อง”, “ซ่อมบำรุง Sunshine”, “เชื่อมต่อ”, “เวลาเช่า” และใช้สีตามระดับความเสี่ยง — เขียว = เปิดใช้งาน/เพิ่มเวลา, น้ำเงิน = เชื่อมต่อ/ตั้งค่า, เหลือง = ซ่อมบำรุง/ลดเวลา, แดง = ปิด/ยกเลิก; dialog ยืนยันใช้สีเดียวกับปุ่มต้นทาง และมี legend “สีของปุ่ม” ใต้ header

## สถาปัตยกรรม

```
/admin/pc-control (server auth)
  └─ PcControl (client, polling 5s)
       ├─ GET /api/admin/pc-control            -> Controller GET /internal/pc-control
       ├─ POST /api/admin/pc-control/[id]/power-off
       ├─ GET/POST /api/admin/pc-control/[id]/pairing (+ status)
       ├─ POST /api/admin/pc-control/[id]/lease-time
       ├─ POST /api/admin/pc-control/[id]/lease-cancel
       └─ POST /api/admin/machines/[id]/wake   (route เดิม)
```

- ทุก route เป็น admin-only + same-origin ผ่าน `machineApi`; Controller ตรวจ `X-Authenticated-Actor-Id/Role` จาก trusted forwarding เท่านั้น
- Snapshot ฝั่ง web enrich ชื่อ/อีเมลผู้เช่าจาก auth DB; Controller ไม่รู้จัก auth users
- Migration `023-pc-control-commands.sql`:
  - `command_outbox` เพิ่ม type `EndLease`, `ShutdownMachine` (rebuild ตาราง; `ShutdownMachine` ไม่ผูก rental)
  - `machine_admin_actions` เก็บ audit/idempotency ของ action แอดมิน (append-only; ไม่เก็บ PIN)
- Agent: `LeaseEnforcer.EndLeaseAsync` ปิด gate/เคลียร์ lease; `ShutdownMachineAsync` ตรวจ boot/maintenance fence แล้วสั่ง shutdown; `ApplyAsync` รองรับ expiry ที่สั้นลงเพื่อลดเวลา

## ความปลอดภัยและ fail-closed

- ต้องมี admin session และ trusted forwarding; browser ส่ง actor/lease id เองไม่ได้ (route/Controller derive จาก machine)
- ระหว่าง restore quarantine, maintenance chain active หรือ Agent update ค้าง: action ที่เสี่ยงจะถูกปฏิเสธ (409) และ UI แสดงเหตุผล
- คำสั่งทุกตัวมี idempotency/revision guard; คำขอยกเลิก/ปรับเวลาซ้ำจะไม่สร้างคำสั่งซ้ำ
- VNC ticket ออกเฉพาะตอนกดเปิด, ผูก origin+machine, มีวันหมดอายุ; ไม่มี background streaming
- ไม่คืนเงิน/ไม่ปรับ balance ในฟีเจอร์นี้; ระบบเช่า/คิดเงินแยกออกไป

## UI/Design system

- ใช้ shadcn/ui ที่มีอยู่: `Card`, `Badge`, `Button`, `Dialog`, `AlertDialog`, `Empty`, `Skeleton`, `Spinner`, `Field`, `Input`, `Select`, `ToggleGroup`, `Alert`, `Separator`
- ใช้ semantic token เท่านั้น (`bg-background`, `bg-card`, `text-muted-foreground`, `border-border`, `bg-status-*` ผ่าน Badge variants) — ไม่มี raw palette colors หรือ `dark:` override ใน component ใหม่
- เพิ่ม `alert-dialog` และ `spinner` ตาม registry ของโปรเจกต์; ไม่แก้ `globals.css` หรือ token/radius/font เดิม

## การทดสอบ

- Controller: `PcControlTests` (snapshot, force power off, EndLease, adjust ±, admin pairing, endpoint auth) + migration/schema tests
- Agent: `PcControlAgentCommandTests` (EndLease/ShutdownMachine dispatch, boot/fence guards, shrunk expiry)
- Web: `tests/pc-control.test.tsx`, `tests/pc-control-routes.test.ts`, และ machines-manager regression
- คำสั่ง:

```
dotnet test services/Controller.Tests/Controller.Tests.csproj --filter "FullyQualifiedName~PcControl|FullyQualifiedName~Migration"
dotnet test services/Agent.Tests/Agent.Tests.csproj --filter "FullyQualifiedName~PcControl"
pnpm exec vitest run --configLoader runner tests/pc-control.test.tsx tests/pc-control-routes.test.ts tests/machines-manager.test.tsx
pnpm exec tsc --noEmit
pnpm lint
```

## ข้อจำกัด / สิ่งที่ยังไม่ทดสอบ

- ยังไม่มีการรันคำสั่งปิดเครื่อง/VNC กับเครื่องจริงในสภาพแวดล้อมนี้; ต้องมี Agent ออนไลน์และหน้าต่าง qualification
- Controller ที่เปิดจาก DB เดิมหลังอัปเกรดจะอยู่ใน **restore quarantine** (fail-closed) จนกว่าจะมี external recovery history ที่ผ่านการรับรอง; snapshot ยังดูได้แต่ action ส่วนใหญ่จะถูกปฏิเสธ
- การลดเวลา/ยกเลิกการเช่าไม่ทำ refund; ถ้าต้องการคิดเงินคืนต้องออกแบบ billing แยก
- VNC ต้องมี target ที่ reachable และ ticket ยังไม่หมดอายุ; การดูหลายแท็บขึ้นกับ session ของแต่ละแท็บ
