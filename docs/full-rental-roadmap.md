# แผนระบบเช่าคอม KamenRaiderPlay ฉบับเต็ม

สถานะเอกสาร: roadmap รวมของระบบเว็บ, Controller, Agent และช่องทางดูแลเครื่อง  
อัปเดต: 2026-11-05

## เป้าหมายระบบ

แอดมินสร้างคำสั่ง PowerShell จากหน้าเว็บ นำไปรันบน Windows เครื่องลูก แล้ว Agent ติดตั้งและลงทะเบียน เครื่องขึ้นเป็น pending ในหน้าแอดมิน แอดมินตั้งชื่อ ราคา และพอร์ต แล้วอนุมัติ เครื่องที่อนุมัติจึงรับ lease ได้ ระบบบังคับเวลาหมดอายุจาก Agent แม้ Controller ขาดการเชื่อมต่อ และคืนเครื่องให้พร้อมรอบใหม่เมื่อ Windows บูตกลับมาและผ่านการตรวจที่กำหนด

VNC/noVNC เป็นความสามารถของระบบฉบับเต็มสำหรับแอดมิน ส่วน Moonlight/Sunshine เป็นทางสตรีมให้ผู้เช่า Reboot Restore Rx เป็นระบบที่ผู้ใช้จัดการเอง โครงการนี้ไม่ติดตั้งหรือสั่งงาน Rx

## ข้อตกลงที่ยืนยันแล้ว

- เครื่องแม่รันเว็บ Next.js และ Controller C#/.NET บนเครื่องเดียวกัน
- เครื่องลูกติดตั้ง Agent C#/.NET Windows Service จากคำสั่ง PowerShell ในหน้าแอดมิน
- เริ่มทดสอบบนเครื่องแม่เครื่องเดียวผ่าน `localhost`; URL สำหรับเครื่องอื่นตั้งผ่าน environment ภายหลัง
- Agent ลงทะเบียนด้วย identity ของเครื่อง แล้วขึ้น pending; แอดมินตั้งชื่อ ราคา พอร์ต และอนุมัติบนเว็บ
- การเปลี่ยนพอร์ตในเว็บเป็น metadata/configuration ของระบบ ไม่แก้ Sunshine หรือ router ให้เอง
- หมดเวลาเช่าให้สั่งปิดทันที ไม่มีคำเตือนและไม่มีช่วงผ่อนผัน
- ใช้ Moonlight/Sunshine สำหรับผู้เช่า; noVNC/VNC ใช้ดูเครื่องในฐานะแอดมิน
- Public IP เป็นค่าระดับร้าน; ผู้ดูแลตั้ง port forwarding เอง
- Restore Rx ทำงานตามการตั้งค่าที่มีอยู่แล้ว ผู้พัฒนา Agent ไม่เข้าไปควบคุมผลิตภัณฑ์นี้
- Parsec และการหักเงินจริงไม่รวมในรุ่นส่งมอบแรก; ต้องวางแผนย่อยเมื่อยืนยันวิธีให้/ถอนสิทธิ์และธุรกรรมแล้ว

## Roadmap และสถานะ

### 1. เว็บและ Controller สำหรับพัฒนาในเครื่องเดียว — เสร็จบางส่วน

- Next.js ใช้ internal Controller URL แบบ loopback พร้อม service credential
- Controller มี internal HTTP listener และ HTTPS listener แยกพอร์ต
- มีสคริปต์ dev สำหรับเปิด Controller ด้วยค่าจาก `.env.local`
- `/admin/machines` อ่านรายการเครื่อง แสดง pending ตั้งชื่อ/ราคา/พอร์ต อนุมัติหรือปฏิเสธ และสั่ง WOL
- ต้องเริ่ม Next.js ใหม่หลังแก้ `.env.local`; Controller dev ต้องเปิดค้างระหว่างทดสอบ
- สถานะล่าสุด: Controller ตอบ health ที่ `127.0.0.1:5081` และ HTTPS ที่ `localhost:7443` บนเครื่องนี้

### 2. ติดตั้ง Agent จากหน้าแอดมิน — ยังไม่เสร็จ

- หน้าแอดมินออกคำสั่ง PowerShell โดยใช้ URL และ package hash จาก environment
- สคริปต์ตรวจสิทธิ์ Administrator, ดาวน์โหลด package ผ่าน HTTPS และตรวจ SHA-256
- ติดตั้งไฟล์ใน Program Files; เก็บ identity/config ใน data directory ที่ ACL จำกัดสิทธิ์
- ติดตั้งและเริ่ม Windows Service จากนั้นรอ Controller ยืนยัน enrollment
- เมื่อสำเร็จ เครื่องขึ้น pending โดยไม่ต้องสร้างรายการเองบนเว็บ
- แก้กรณีติดตั้งซ้ำ, network timeout, package เสีย, service เริ่มไม่สำเร็จ และ rollback
- สถานะล่าสุด: มี UI/route/bootstrap scaffold แต่ยังปิดด้วย `AGENT_INSTALL_ENABLED=false`; package installer ยังหยุดก่อนสร้าง service

### 3. Enrollment, identity และการอนุมัติเครื่อง — โค้ดหลักเสร็จ; ยังไม่ผ่านทดสอบติดตั้งจริง

- Agent สร้าง key และ machine ID ประจำเครื่อง เก็บด้วย ACL เฉพาะ
- คำขอ enrollment ลงลายเซ็นและมี replay protection
- Controller สร้าง pending machine โดยไม่ให้เช่าได้
- รายงาน local IP, MAC และพอร์ตเริ่มต้น; การส่งซ้ำใช้ identity เดิม
- Admin ตรวจ fingerprint, ตั้งชื่อ ราคา พอร์ต และอนุมัติ
- หน้าเว็บ poll รายการเครื่องทุก 5 วินาที
- ต้องทดสอบ flow จริงจาก PowerShell install จนเครื่องปรากฏในหน้าแอดมิน

### 4. Sync, configuration, heartbeat, Public IP และ WOL — โค้ดหลักมี; hardware ยังไม่ผ่าน

- Agent ส่ง heartbeat และสถานะ config revision
- Controller เป็นเจ้าของค่า config; Agent บันทึก config ใหม่แบบ atomic และรายงาน ACK
- เครื่องที่เงียบเกินกำหนดเป็น unreachable ไม่ถือว่าปิดสำเร็จ
- Controller ตรวจ Public IP ผ่าน API และคงค่าล่าสุดเมื่อ API ล้มเหลว
- Controller ส่ง Wake-on-LAN ตาม interface/broadcast ที่ตั้งไว้
- ทดสอบ WOL จาก shutdown จริง, subnet จริง และ adapter ที่เลือก
- ยืนยัน `publicIp:moonlightPort` จาก client นอก LAN; metadata อย่างเดียวไม่ยืนยันว่า router forward แล้ว

### 5. Lease, allocation และเวลาหมดอายุ — state machine มี; ยังต้องปิดช่องว่างและทดสอบจริง

- หนึ่งเครื่องมี allocation ได้หนึ่ง lease; การจองพร้อมกันต้องได้ผู้ชนะคนเดียว
- สถานะ `reserved → preparing → active → ending → completed` และ `failed`
- เริ่มคิดเวลาหลัง Agent ACK ว่าพร้อม ไม่คิดระหว่างบูต
- ต่อเวลาเพิ่ม revision และยังไม่รายงานสำเร็จจน Agent ACK
- เวลาใช้ UTC, deadline ฝั่ง Agent ใช้ monotonic clock และทำงานได้เมื่อ offline
- หมดเวลาแล้ว Agent สั่ง Windows shutdown ทันทีโดยไม่มี warning/grace period
- แก้และพิสูจน์ crash-durable deadline เมื่อ Agent ล่มก่อนบันทึก server time; ทดสอบ shutdown failure และ retry
- ระยะแรกใช้ trial lease ของแอดมิน ไม่หัก balance; payment/refund เป็นงานแยก

### 6. Moonlight/Sunshine และ stream fence — pairing มี; ตัวกั้นสตรีมจริงยังขาด

- Agent เรียก Sunshine management API บน localhost เท่านั้น; credential ไม่ออกไป browser
- จับคู่ PIN เฉพาะ lease/owner ที่ active; PIN ไม่ลง log และล้างเมื่อหมดอายุ
- ล้าง pending pairing และ client เก่าก่อนคืนเครื่อง
- สร้าง fence ที่ปิดตั้งแต่ boot, Agent/service crash และไม่มี active lease
- Fence ต้องกันการเชื่อมต่อใหม่และตัด stream เดิม; firewall allow rules ที่ Sunshine ติดตั้งไว้ต้องถูกพิจารณาครบ
- readiness ต้องตรวจ Sunshine, desktop/capture, encoder, audio และ input จริง
- เครื่องนี้ติดตั้ง Sunshine รุ่น fork; ตรวจ schema/API กับ binary รุ่นนี้ก่อนเปิด adapter
- ทดสอบ Moonlight client ใหม่/เก่าและ stream ที่เปิดค้างเมื่อหมดเวลา

### 7. Reconcile หลังบูตและพร้อมเช่ารอบใหม่ — runtime heartbeat ต่อแล้ว; boot evidence ยังขาด

- Agent ปิด stream fence ก่อน sync/reconcile ทุกครั้ง
- Agent ส่ง signed heartbeat สถานะ notReady ได้ระหว่างรอ reconciliation และไม่รับคำสั่ง/lease confirmation จนกว่าจะ Ready
- สถานะปัจจุบัน: runtime heartbeat ต่อแล้ว แต่ production ยังใช้ UnavailableBootEnvironment และ ClosedStreamGate; ห้ามอ้างว่า Sunshine ถูกกั้นหรือเครื่องพร้อมเช่า
- ดึง config ล่าสุดและ reconcile lease/boot ก่อนรายงาน ready
- lease ที่ยัง active และไม่หมดให้กลับเข้า lease เดิมด้วย expiry เดิม; ห้ามเพิ่มเวลา
- lease ที่จบแล้วไม่สั่ง shutdown ซ้ำ; allocation ปล่อยได้หลัง cleanup/health ผ่าน
- ถ้า Controller ติดต่อไม่ได้ ให้คง gate ปิดและ not-ready
- ใช้ Restore Rx ตามการตั้งค่าภายนอกของผู้ใช้ ไม่เรียก CLI/API และไม่แก้ baseline จาก Agent
- กำหนดวิธีตรวจผล restore ร่วมกับผู้ดูแล โดยไม่แอบอ้างว่า boot ID อย่างเดียวพิสูจน์การล้างข้อมูล
- ทดสอบ cold boot, Agent restart ใน boot เดิม, network outage, lease หมดระหว่าง offline และ config revision เก่า

### 8. VNC/noVNC สำหรับแอดมิน — ยังไม่เริ่ม

- ติดตั้ง VNC server ในเครื่องลูกและ noVNC gateway ในเครื่องแม่ หรือเลือก implementation ที่ผ่านการตรวจความปลอดภัย
- เปิด viewer เฉพาะ admin ที่ login; ตรวจสิทธิ์เครื่องทุกครั้งก่อนออก short-lived session token
- เริ่มด้วยดูอย่างเดียว; ถ้าจะเปิด keyboard/mouse control ให้เป็นสิทธิ์แยกและมี audit
- VNC endpoint ต้องไม่รับ host/port จาก browser; gateway เลือกปลายทางจากเครื่องที่ Controller รู้จัก
- จำกัด VNC ให้เข้าผ่าน gateway/LAN ตามนโยบาย; ห้ามเปิด VNC ตรงสู่อินเทอร์เน็ต
- บันทึก admin, machine, เวลาเริ่ม/จบ และโหมดดู/ควบคุม; แสดงสถานะว่ามี admin ดูอยู่
- ทดสอบ desktop จริง, Windows lock/UAC, การสลับ session, ระหว่าง Moonlight stream และเมื่อ Agent offline

### 9. หน้าผู้เช่าและการเชื่อมต่อภายนอก — มีเส้นทางพื้นฐาน; ยังเป็นระบบทดลอง

- หน้า rent แสดงเฉพาะเครื่อง approved, ready, config ACK ตรง และไม่มี allocation
- เจ้าของ lease ดูเวลา/สถานะและส่ง PIN ได้; API ตรวจ owner ทุกครั้ง
- แสดง public IP และ Moonlight port ให้เฉพาะเจ้าของ lease
- เตรียม/ต่อเวลา/หมดเวลา/unreachable ต้องแสดงสถานะจริงจาก Controller ไม่ใช้ข้อมูล mock
- ตั้ง router port forwarding เอง; ทดสอบ public IP, CGNAT, TCP/UDP ports และจากเครือข่ายนอก LAN
- ยังไม่เปิดซื้อ/ต่อเวลาแบบหักเงินจริงจนมี payment, idempotency, refund และ audit design

### 10. Acceptance, operations และส่งมอบ — ยังไม่ครบ

- Integration test: install/enroll → pending → metadata/approve → ready → lease → pair → extend → expiry/shutdown → boot/reconcile → available
- ทดสอบ collision สองผู้เช่า, command replay, Controller restart, Agent crash, network offline และ expiry boundary
- Hardware matrix: WOL, stream isolation, active session cutoff, old-client reconnect, cold boot, audio/input/capture, external ports
- บันทึก pass/fail/not-tested แยก fake tests กับ hardware tests
- จัดทำ Controller DB backup/restore, certificate/secret recovery, identity revoke/re-enroll และ runbook
- ห้ามประกาศ rental-ready จน gate, expiry, installation และ hardware acceptance ผ่านครบ

## สถานะสรุปวันนี้

เว็บ/Controller/Agent มีโครงและ business logic หลายส่วนแล้ว; Controller dev เปิด localhost ได้ Agent runtime ส่ง heartbeat notReady ระหว่างรอหลักฐาน boot ได้แล้ว แต่ยังไม่ได้ติดตั้งเป็น Windows Service บนเครื่องนี้ การติดตั้งจากหน้าแอดมินยังถูกปิด VNC/noVNC ยังไม่เริ่ม และ production readiness ยังไม่ผ่านเพราะ stream fence จริง, restoration evidence, installer และ hardware acceptance ยังขาด
