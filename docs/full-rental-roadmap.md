# แผนระบบเช่าคอม KamenRaiderPlay ฉบับเต็ม

สถานะเอกสาร: roadmap รวมของระบบเว็บ, Controller, Agent และช่องทางดูแลเครื่อง  
อัปเดต: 2026-11-07

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
- `/admin/machines` ใช้ `instant = false` สำหรับ session ตรวจสิทธิ์ช่วง request-time; production build ไม่รายงาน uncached prerender error
- ต้องเริ่ม Next.js ใหม่หลังแก้ `.env.local`; Controller dev ต้องเปิดค้างระหว่างทดสอบ
- สถานะล่าสุด: Controller ตอบ health ที่ `127.0.0.1:5081` และ HTTPS ที่ `localhost:7443` บนเครื่องนี้

### 2. ติดตั้ง Agent จากหน้าแอดมิน — flow และ security checks เสร็จ; รอทดสอบเครื่องลูกจริง

- route แอดมินตรวจ session/admin และ environment; ถ้าค่า URL, hash หรือ trust thumbprint ไม่ผ่าน จะไม่ส่งคำสั่งออกไป
- คำสั่งรองรับ Windows PowerShell 5.1, ตรวจ SHA-256 ของ Bootstrap ในหน่วยความจำและ package ก่อนติดตั้ง แล้วส่ง URL กลับ `/admin/machines`; ไม่เขียน Bootstrap ลง shared TEMP
- HTTP อนุญาตเฉพาะ URL loopback สำหรับทดสอบเครื่องเดียว; Controller URL ต้องเป็น HTTPS
- API สร้าง enrollment ticket แบบสุ่มที่หมดอายุใน 24 ชั่วโมง เก็บเฉพาะ SHA-256 hash และใช้ได้ครั้งเดียวสำหรับ identity ใหม่; identity เดิมส่ง enrollment ซ้ำเพื่อกู้คืนได้
- installer วาง Agent และ staging ใน Program Files, จำกัด ACL ของไฟล์/config/data ตั้งแต่สร้าง directory, ปฏิเสธ directory ที่ถูกสร้างดักหรือมี ACL ไม่ปลอดภัย, ตั้ง Windows Service เป็น auto-start และเริ่ม enrollment
- หน้าแอดมินแสดงคำสั่งให้คัดลอก; หลังติดตั้งจะกลับมาหน้าเครื่อง และรายการ poll แสดงเครื่องที่ลงทะเบียนเป็น pending ให้ตั้งชื่อ ราคา พอร์ตและอนุมัติ
- ถ้า Controller ติดต่อไม่ได้ภายใน 90 วินาที service จะอยู่ต่อและ retry; script แจ้ง pending แล้วเปิดหน้าเว็บ; ticket ถูกลบหลังบันทึก receipt สำเร็จ
- ถ้า service หยุดหรือ ticket หมดอายุก่อน enrollment คำสั่ง admin ใหม่จะตรวจ service, binary, config และ identity เดิม แล้วเปลี่ยนเฉพาะ ticket ส่วนตัวก่อนเริ่ม/ resume service; ปฏิเสธ installation ที่ไม่ครบหรือค่าไม่ตรงกัน
- service/ข้อมูลเดิมที่มีอยู่และ package ที่ hash ไม่ตรงถูกปฏิเสธ; service ที่สร้างโดยรอบติดตั้งนี้เท่านั้นที่ถูกลบเมื่อ rollback
- ยังไม่ได้ติดตั้ง service จริงบนเครื่องพัฒนา. ผลตรวจ software baseline (2026-11-06 snapshot; ผลปัจจุบันเพิ่มเติมอยู่ท้ายเอกสาร): Agent 217/217 และ Controller 151/151 ผ่าน พร้อม integration test 1/1 สำหรับ signed enrollment → pending/approve → boot reconciliation → ready → lease prepare/active → pairing → extension → expiry/shutdown → next boot/completed/new allocation ด้วย TestServer และ fake OS/Sunshine/evidence adapters. Fake adapters ไม่ยืนยันการติดตั้ง, Restore Rx, Windows firewall/SCM, TLS trust หรือ hardware readiness. ผลเว็บ/PowerShell ใน snapshot นี้เป็นประวัติตามเอกสาร acceptance ด้านล่าง; Task 5 package/Stream Gate checks ภายหลังผ่านบน PowerShell 5.1/7 หลังผู้ใช้ allow/restore ไฟล์ โดยไม่ bypass antivirus.
- `.env.local` ตั้ง URL สำหรับ localhost (`http://localhost:3000`, `https://localhost:7443`) และ thumbprint ของ localhost root ไว้แล้ว; `AGENT_INSTALL_ENABLED=false` จนกว่าจะ build package และได้ SHA-256 จริง. `public/agent/AgentPackage.zip` ยังไม่มี. ก่อนติดตั้งต้องเปิดใช้เมื่อ hash ถูกต้อง แล้วทดสอบ enrollment receipt/pending, approve, service recovery และ reboot บนเครื่องลูกจริง
- Inventory แบบอ่านอย่างเดียวล่าสุด (2026-11-06): ไม่มี Windows service ชื่อ `KamenRaider Controller` หรือ `KamenRaider Agent`; มี service แยกชื่อ `0jay-agent` กำลังทำงานเป็น LocalSystem/Automatic ซึ่งยังไม่ได้ตรวจว่าเข้ากันได้; `SunshineService` ทำงานแบบ Automatic, มีกฎ inbound TCP/UDP ที่ local port เป็น Any, ไม่มี private-certificate candidate และการตรวจไม่ elevated; `StreamFenceQualified=false` และ `ReadyForRental=false`

### 3. Enrollment, identity และการอนุมัติเครื่อง — โค้ดหลักเสร็จ; ยังไม่ผ่านทดสอบติดตั้งจริง

- Agent สร้าง key และ machine ID ประจำเครื่อง เก็บด้วย ACL เฉพาะ
- Admin route ออก ticket ที่มีอายุ 24 ชั่วโมง; Controller เก็บ hash, บังคับ single-use ตอนสร้าง identity ใหม่ และอนุญาต same-identity retries โดยไม่สร้างเครื่องซ้ำ
- คำขอ enrollment ลงลายเซ็นและมี replay protection
- Controller สร้าง pending machine โดยไม่ให้เช่าได้
- รายงาน local IP, MAC, พอร์ตเริ่มต้น และ Agent informational version ผ่าน signed heartbeat; package ใส่ Git revision และติดธง dirty build เพื่อให้แอดมินแยกรุ่นได้; การส่งซ้ำใช้ identity เดิม
- Admin ตรวจ fingerprint, ตั้งชื่อ ราคา พอร์ต และอนุมัติ
- หน้าเว็บ poll รายการเครื่องทุก 5 วินาที
- ต้องทดสอบ flow จริงจาก PowerShell install จนเครื่องปรากฏในหน้าแอดมิน

### 4. Sync, configuration, heartbeat, Public IP และ WOL — โค้ดหลักมี; hardware ยังไม่ผ่าน

- Agent ส่ง heartbeat และสถานะ config revision
- Controller เป็นเจ้าของค่า config; Agent บันทึก config ใหม่แบบ atomic และรายงาน ACK
- เครื่องที่เงียบเกินกำหนดเป็น unreachable ไม่ถือว่าปิดสำเร็จ
- เครื่องที่มีสถานะ ready แต่ applied config revision ยังไม่ตรงกับ Controller revision ต้องยังเช่าไม่ได้
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
- Agent บันทึก deadline ที่สั้นลงจาก Controller ก่อนรอ stream gate; regression tests ครอบคลุม restart ระหว่าง gate ค้าง, extension ที่ยังไม่ยืนยัน, confirmation ที่ storage ค้างข้าม recovery deadline และ shutdown timeout/retry ด้วย fake adapter; production shutdown adapter จำกัดเวลารอไว้ 15 วินาที ส่วนการจำลอง process crash ระหว่าง atomic file replacement และการทดสอบ shutdown/retry บนเครื่องจริงยังต้องยืนยัน
- ระยะแรกใช้ trial lease ของแอดมิน ไม่หัก balance; payment/refund เป็นงานแยก
- เมื่อ boot transition ผ่าน proof และ Controller ยืนยัน `PrepareAvailable` แล้ว reconciliation ปิด lease เก่าเป็น `completed` และปล่อย allocation; migration 010 เปลี่ยน constraint แบบรักษา rowid และ child references

### 6. Moonlight/Sunshine และ stream fence — Guard/adapter และ installer Plan/mock ผ่าน; live installation และ hardware qualification ยังขาด

- Agent เรียก Sunshine management API บน localhost เท่านั้น; credential ไม่ออกไป browser
- Foundation Sunshine adapter ใช้ช่อง pairing เดียว ส่ง PIN/ชื่อ client และรอยืนยัน handshake จาก client list; token ที่เว็บเลือกเป็น pairing slot ของ Agent ไม่ใช่ client ID จาก Sunshine
- จับคู่ PIN เฉพาะ lease/owner ที่ active; PIN ไม่ลง log และล้างเมื่อหมดอายุ
- ล้าง client เก่าก่อนคืนเครื่องได้หลังมี fence ที่พิสูจน์ว่าปิด pending handshake และ stream เดิมแล้ว; Sunshine fork ไม่มี API ยกเลิก pending PIN
- มี LocalSystem Stream Guard, named-pipe protocol ที่จำกัด Agent service SID, startup fence และ lease deadline enforcement; Agent ยังคงใช้ virtual service account
- มี Windows Firewall/SCM provider และ coordinator ที่ block ก่อนหยุด Sunshine, เริ่ม service ขณะ block อยู่ และเปิดกฎหลังได้ lease grant ปัจจุบันเท่านั้น
- Task 5 installer now prepares the initial block and Guard before Agent enrollment; Windows PowerShell 5.1/7 plan, rollback and package checks pass. No live setup has been applied.
- Fence ต้องกันการเชื่อมต่อใหม่และตัด stream เดิม; ต้องพิสูจน์กฎ block กับ allow rules เดิม, ทุก profile/interface, IPv4/IPv6 และ stream ที่เปิดค้างบนเครื่องทดสอบ
- readiness ต้องตรวจ Sunshine, desktop/capture, encoder, audio และ input จริง
- ตรวจ source ของ Foundation Sunshine รุ่นที่เครื่องนี้ติดตั้งแล้วและปรับ adapter/schema tests ให้ตรง; Agent register `LazySunshinePairingSession` ซึ่งจะสร้าง `SunshineClient`/`PairingSession` เมื่อมี credential file ที่ป้องกันด้วย DPAPI/ACL และใช้ `SunshinePort` ล่าสุดจาก applied configuration; มี CLI provision/rotate แบบไม่ใส่ secret ใน command line. Guard maintenance boundary มี implementation ใน `StreamGuardPipeClient`/coordinator แล้ว แต่ยังไม่ได้ qualify API, credential TLS trust, cleanup, หรือการสตรีมบนเครื่องจริง
- ทดสอบ Moonlight client ใหม่/เก่าและ stream ที่เปิดค้างเมื่อหมดเวลา

### 7. Reconcile หลังบูตและพร้อมเช่ารอบใหม่ — lease import ต่อ production แล้ว; boot evidence ยังขาด

- Agent ปิด stream fence ก่อน sync/reconcile ทุกครั้ง
- Agent ส่ง signed heartbeat สถานะ notReady ได้ระหว่างรอ reconciliation โดย Controller อัปเดต presence/config ACK แต่ไม่เปลี่ยน boot history, lease, command หรือผลคำสั่ง และไม่ส่ง command/lease confirmation; Agent เก็บผลคำสั่งค้างไว้จน Ready sync
- Agent dispatch คำสั่ง `PrepareMachine` แล้ว ตรวจ boot ID, อายุคำสั่ง และไม่มี lease ปัจจุบัน/lease ที่รอ commit ก่อนเรียก `IMachinePreparation`; ถ้าไม่มี provider ที่ใช้งานได้จะตอบ `PreparationUnavailable` แทนการทิ้งคำสั่งและรอ timeout. มี FencedMachinePreparation ใน Agent host: ตอบรับเฉพาะ ready sync ที่ผ่าน boot proof และ runtime health แล้ว, ปิด stream gate และตรวจ command/สถานะ lease ซ้ำ. ค่า boot/restoration/health เริ่มต้นยัง unavailable จึงยังเริ่มเช่ารอบใหม่จริงไม่ได้จนกว่าจะมี provider ที่ให้หลักฐานผ่านการ qualification
- สถานะปัจจุบัน: boot environment และหลักฐาน baseline ต่อผ่าน DI แล้ว โดยค่าเริ่มต้นเป็น UnavailableBootEnvironment/Unvalidated; BootReconciler ใช้ production lease importer ที่ต่อกับ LeaseEnforcer หลังได้รับ proof ครบและ Controller ยืนยัน ResumeLease แล้ว โดยนำ controller time กับเวลาเริ่ม request มาคำนวณ monotonic deadline; เมื่อ Controller ยืนยัน PrepareAvailable หลัง proof จะล้าง snapshot เก่าด้วย atomic tombstone เฉพาะข้าม boot และปฏิเสธการล้าง lease ที่ยังอยู่ใน boot เดียวกัน; หลัง reconciliation ผ่าน Agent ตรวจ runtime health ซ้ำทุกรอบ sync; เมื่อ health/boot/config ไม่ตรงจะปิด gate และรายงาน notReady; ถ้า config revision เปลี่ยนใน sync response Agent จะ apply config, ปิด gate, และไม่ประมวลผล command/confirmation จาก readiness proof เก่า; LeaseEnforcer เปิด gate ได้หลังมี ActiveLease confirmation ตรงกับ lease/boot/revision และคง confirmed=false พร้อมตั้ง fault หากเปิดไม่สำเร็จ; Agent host ใช้ StreamGuardPipeClient ซึ่ง fail closed เมื่อไม่มี Guard แต่เครื่องพัฒนานี้ยังไม่ได้ติดตั้ง Guard และค่าเริ่มต้นของ boot/restoration/health/fenced-cleanup providers ยัง unavailable; ห้ามอ้างว่า Sunshine ถูกกั้นหรือเครื่องพร้อมเช่า
- ดึง config ล่าสุดและ reconcile lease/boot ก่อนรายงาน ready
- lease ที่ยัง active และไม่หมดมีทาง import เข้า LeaseEnforcer ด้วย expiry เดิมหลัง proof ครบ; lease เก่าจะถูกล้างเมื่อ Controller ยืนยัน PrepareAvailable หลัง proof และ OS boot เปลี่ยนแล้ว; ห้ามเพิ่มเวลา; ยังต้องทดสอบกับ Agent/Controller และ boot จริง
- lease ที่จบแล้วไม่สั่ง shutdown ซ้ำ; allocation ปล่อยได้หลัง cleanup/health ผ่าน
- ถ้า Controller ติดต่อไม่ได้ ให้คง gate ปิดและ not-ready
- ใช้ Restore Rx ตามการตั้งค่าภายนอกของผู้ใช้ ไม่เรียก CLI/API และไม่แก้ baseline จาก Agent
- กำหนดวิธีตรวจผล restore ร่วมกับผู้ดูแล โดยไม่แอบอ้างว่า boot ID อย่างเดียวพิสูจน์การล้างข้อมูล
- ทดสอบ cold boot, Agent restart ใน boot เดิม, network outage, lease หมดระหว่าง offline และ config revision เก่า

### 8. VNC/noVNC สำหรับแอดมิน — gateway/viewer เสร็จบางส่วน; ยังไม่พร้อมใช้กับเครื่องจริง

- ผู้ใช้เลือก TightVNC 2.8.90 สำหรับเครื่องลูก; ติดตั้งจาก upstream แยกขณะติดตั้ง ไม่ bundle MSI เข้า KamenRaider package; ใช้เฉพาะ Server component, ปิด firewall exception ที่ MSI เพิ่มเอง, ตั้ง VNC auth และ port 5900, และจำกัด firewall ให้ Controller เข้าถึงได้เท่านั้น. GPL edition ใช้ RFB VNC auth ที่รหัสผ่านมีผล 8 ตัวอักษรและไม่มี session encryption ดังนั้น hop VNC ต้องอยู่บน LAN ที่เชื่อถือได้; ห้ามทำ port forwarding ออกอินเทอร์เน็ต. ดู [TightVNC 2.8.90 download](https://www.tightvnc.com/download/index.php), [MSI deployment guide](https://www.tightvnc.com/doc/win/TightVNC_2.7_for_Windows_Installing_from_MSI_Packages.pdf), [security FAQ](https://www.tightvnc.com/faq.php) และ [license terms](https://www.tightvnc.com/licensing.php)
- API คำสั่งติดตั้ง Agent ส่ง IP ต้นทาง VNC แบบ IPv4 ส่วนตัว RFC1918 ที่แน่นอนไปกับ bootstrap; ถ้า Controller URL เป็น DNS name หรือ localhost ให้ตั้ง `AGENT_VNC_ALLOWED_SOURCE_IP` เป็น LAN IPv4 ที่เครื่องลูกเห็นเป็นต้นทาง (localhost ห้ามเดาเป็น 127.0.0.1 เพราะ gateway เชื่อมไปยัง IP LAN ที่แอดมินยืนยัน)
- มี noVNC core แบบ vendored, ปุ่ม viewer ในหน้าเครื่องแอดมิน และ API ที่ตรวจ session/admin และ same-origin
- แอดมินเห็น IP ที่ Agent รายงานและต้องบันทึก IP เป้าหมาย VNC ที่ตรวจสอบกับเครื่องจริง; Controller ใช้ IP ที่แอดมินบันทึกไว้ ไม่ใช้ค่าที่ Agent เปลี่ยนใน heartbeat และปฏิเสธ IP ที่ไม่ใช่ private LAN
- Controller ออก ticket ใช้ครั้งเดียว อายุ 1 นาที ผูกกับ origin; WSS ใช้ IP ที่แอดมินยืนยันกับ port 5900 ที่กำหนดฝั่ง server ไม่รับ host/port จาก browser
- Controller จำกัด session ไว้ 1 ชั่วโมงและรับเฉพาะ RFB 3.7/3.8 ที่ใช้ VNC authentication; gateway ส่ง RFB handshake, Raw framebuffer, desktop-size และ control message ที่รู้ความยาวแน่นอน, ปฏิเสธ keyboard, mouse, clipboard จาก browser, XVP หรือข้อความที่ไม่รู้จัก, ตัด standard ServerCutText และปิด session เมื่อเจอ extended clipboard ที่ไม่ได้ต่อรอง
- noVNC เปิด view-only และตัด clipboard negotiation; viewer ไม่ส่ง clipboard ไปยังเครื่องหรือใส่ clipboard ของเครื่องแอดมิน; VNC server ต้องเริ่มด้วย true-color 32 bpp และรองรับ Raw encoding
- ฐานข้อมูลเก็บ admin, machine, เวลาเริ่ม/จบ; หน้าแอดมินแสดงจำนวน session ที่กำลังดูจาก session ที่ยังไม่จบและเริ่มภายใน 1 ชั่วโมงล่าสุด; noVNC ตั้ง view-only เพิ่มอีกชั้นหนึ่ง
- Controller ลบ ticket ที่หมดอายุและยังไม่ใช้, session ที่ค้างเกิน 65 นาทีหลัง Controller หยุดผิดปกติ, และเก็บ session ที่จบแล้วไว้ตรวจย้อนหลัง 30 วัน; มี regression test ครบทั้งสองกรณี
- มี PowerShell installer สำหรับ TightVNC 2.8.90 x64 รวมใน Agent package โดยดาวน์โหลด MSI แยก, ตรวจ SHA-256 ที่ pin ไว้, ลายเซ็น `OOO GlavSoft` และข้อมูล MSI, ตั้งเฉพาะ Server, ปิด viewer ผ่าน HTTP, บล็อก input และจำกัด firewall ไปยัง Controller IPv4 บน Private profile; ต้องเปิด Windows Firewall Private profile และ installer ปฏิเสธ TightVNC service/ไฟล์ที่ตรวจพบแต่ไม่มี managed marker
- การติดตั้งใหม่เริ่มด้วย MSI ที่ยังไม่ลงทะเบียน service และปิด RFB ทุกต้นทาง, ตั้ง firewall source rule แล้วจึงลงทะเบียน/เปิด service; ขั้น configure/repair ใช้ MSI maintenance API และ secure transform ที่เติมชื่อ password properties ลง `MsiHiddenProperties` ของ MSI พร้อมเก็บ transform ไว้ใน data directory ที่ ACL จำกัด; ปฏิเสธ Windows Installer Debug policy เมื่อเปิด command-line logging bit `0x04` (ค่า 4–7); password ไม่อยู่ใน process command line
- Agent สร้างรหัสผ่าน VNC แยกต่อเครื่อง, เก็บไว้ใต้ data directory ด้วย ACL สำหรับ SYSTEM/Administrators และแสดงในหน้าต่างติดตั้งหนึ่งครั้ง; plan mode ไม่สร้างรหัสหรือแก้บริการ/ไฟร์วอลล์
- ตั้ง VNC หลัง enrollment สำเร็จเท่านั้น; ถ้า Agent ยัง pending ให้รันคำสั่งจากหน้าแอดมินซ้ำเมื่อเครื่องติดต่อ Controller ได้
- Agent checks ทั้ง 11 รายการรวม package build ผ่าน Windows PowerShell 5.1; PowerShell 7 checks ที่ไม่สร้าง package 10 รายการผ่าน ส่วน package-build assertions ผ่านแต่ Antivirus บล็อก cleanup ของไฟล์ชั่วคราวใน PowerShell 7; ดาวน์โหลด TightVNC MSI ทางการและตรวจ signer/hash/metadata/transform แล้ว แต่ยังไม่ได้ติดตั้ง MSI และไม่มี VNC server บนเครื่องลูกที่ผ่านการทดสอบ
- ห้ามเปิด VNC port ตรงสู่อินเทอร์เน็ต; ห้ามเปิด keyboard/mouse control จนกว่าจะเพิ่มสิทธิ์แยกและ audit
- ยังไม่ผ่าน desktop จริง, Windows lock/UAC, การสลับ session, ระหว่าง Moonlight stream และกรณี Agent offline
- ทดสอบ desktop จริง, Windows lock/UAC, การสลับ session, ระหว่าง Moonlight stream และเมื่อ Agent offline

### 9. หน้าผู้เช่าและการเชื่อมต่อภายนอก — มีเส้นทางพื้นฐาน; ยังเป็นระบบทดลอง

- หน้า rent แสดงเฉพาะเครื่อง approved, ready, config ACK ตรง และไม่มี allocation
- เจ้าของ lease ดูเวลา/สถานะและส่ง PIN ได้; API ตรวจ owner ทุกครั้ง
- เจ้าของ lease ที่ active ส่งคำขอต่อเวลาทดลองได้ผ่าน API ที่ตรวจ session, origin, input bounds และส่ง actor identity ให้ Controller; UI แสดงผลรอ Agent ACK และยังไม่หักเงิน
- แสดง public IP และ Moonlight port ให้เฉพาะเจ้าของ lease
- เตรียม/ต่อเวลา/หมดเวลา/unreachable ต้องแสดงสถานะจริงจาก Controller ไม่ใช้ข้อมูล mock
- ตั้ง router port forwarding เอง; ทดสอบ public IP, CGNAT, TCP/UDP ports และจากเครือข่ายนอก LAN
- ยังไม่เปิดซื้อ/ต่อเวลาแบบหักเงินจริงจนมี payment, idempotency, refund และ audit design

### 10. Acceptance, operations และส่งมอบ — ยังไม่ครบ

- Software integration test is implemented and passing for signed enrollment → pending/approve → ready → lease → pair → extend → expiry/shutdown → boot/reconcile → available/new allocation using Controller TestServer and fake machine adapters. Installer/package and real machine/hardware acceptance remain separate and unverified.
- ทดสอบ collision สองผู้เช่า, command replay, Controller restart, Agent crash, network offline และ expiry boundary
- Hardware matrix: WOL, stream isolation, active session cutoff, old-client reconnect, cold boot, audio/input/capture, external ports
- บันทึก pass/fail/not-tested แยก fake tests กับ hardware tests
- Controller มี online verified SQLite backup/restore CLI พร้อม activity lock และ pre-restore snapshot; validator ตรวจ required columns/types/nullability/primary keys และ allocation/command-revision uniqueness ก่อนทำ snapshot หรือแทนที่ฐาน; ทดสอบด้วยฐานชั่วคราวเท่านั้น ยังต้องทำ production restore drill, รับรอง ACL/การเก็บ backup และทำ certificate/secret recovery กับ identity revoke/re-enroll runbook
- ห้ามประกาศ rental-ready จน gate, expiry, installation และ hardware acceptance ผ่านครบ

## สถานะสรุปวันนี้

เว็บ/Controller/Agent มีโครงและ business logic หลายส่วนแล้ว; Controller dev และ Next dev ตอบใน localhost ได้ โดย `/api/machines` และ session API ตอบ 200. Agent package ยังไม่ถูก publish, `AGENT_INSTALL_ENABLED=false`, และยังไม่มี KamenRaider Agent/Stream Guard service ติดตั้ง จึงยังไม่เปิดคำสั่งติดตั้งในหน้าแอดมิน. noVNC viewer, read-only WSS gateway, TightVNC installer, Stream Guard และ SQLite backup/restore มีโค้ดและ software tests แต่ยังไม่มี live installation/qualification บนเครื่องลูก. Production readiness ยังคง NOT READY เพราะ boot/restoration/desktop-health providers, trusted Sunshine TLS/credentials, live fence behavior และ hardware acceptance ยังขาด.

### Stream Guard installer — Task 5 verification, 2026-11-06

The default-closed Sunshine installer transaction is implemented and verified in Plan/mock mode. The package prepares the exact closed firewall rule and automatic LocalSystem `KamenRaider Stream Guard` before starting virtual-account `KamenRaider Agent` enrollment; Sunshine becomes manual-start with prior dependencies preserved plus Guard. At this 2026-11-06 snapshot, ownership-aware rollback and existing-install validation were covered by 29 mocked cases; the final Task 5 follow-up later reported 53 mocked setup/rollback cases. All Agent PowerShell plan/install/package checks passed on Windows PowerShell 5.1 and PowerShell 7, including ZIP/SHA-256 and repeated package builds. The antivirus read block did not recur. No live install, rollback, firewall/service mutation, Restore Rx action or `AGENT_INSTALL_ENABLED` change occurred. Real-host stream isolation and boot/recovery qualification are still outstanding; the generated test packages were disposable and were not published to `public/agent`.

### Task 7: ผลซอฟต์แวร์และข้อจำกัดการ qualification (2026-11-07 snapshot; ผลล่าสุดอยู่ใน Task 14–15 ด้านล่าง)

- สถานะนี้อัปเดต summary ด้านบน: Task 5 software Plan/mock/package verification ผ่านแล้ว แต่ package ยังไม่ถูก publish และ live install/Apply ยังไม่เกิด
- หลักฐานล่าสุดหลัง Task 6 fix: Agent full suite ผ่าน 218/218, ชุด focused pipe/coordinator ผ่าน 43/43 และ Controller ผ่าน 151/151. ก่อน final fix, Agent full suite ผ่าน 217/217; ใช้ผลหลัง fix เป็นสถานะล่าสุด
- Windows PowerShell 5.1.22621.6133 และ PowerShell 7.6.9 ผ่าน Stream Guard setup/rollback mock 53 cases, package plan/install checks และ package build. Allow/restore ของผู้ใช้สำเร็จและไม่พบ antivirus block ซ้ำ; Task 5, Task 6 และ review ของ fix ผ่าน
- Web checks ที่บันทึกไว้: TypeScript ผ่าน, lint ไม่มี error (มี img warning เดิมหนึ่งจุด), Webpack production build ผ่าน; Turbopack build ล้มจาก Babel loader subprocess crash ใน UI imports เดิม
- Read-only host check วันที่ 2026-11-07 พบ firewall Domain/Private/Public enabled และอนุญาต local rules; ไม่มี KamenRaider Agent/Controller/Stream Guard service, ไม่มี public/agent/AgentPackage.zip และ AGENT_INSTALL_ENABLED=false. การตั้งค่า profile ไม่พิสูจน์ว่ามี managed fence หรือว่าปิดการเชื่อมต่อได้จริง
- ยังไม่มีการ provision Sunshine credentials, ติดตั้ง service, live firewall/service Apply, actual stream test หรือ Restore Rx interaction. Boot/restoration/desktop-health providers ยัง unavailable จึงคง NOT READY.
- Hardware rows ยังคง NOT TESTED: effective IPv4/IPv6 block, termination ของ existing connection, Agent/Guard crash/recovery, Moonlight pairing จริง, expiry และ cold boot/Restore Rx rollback.

### Task 14: SQLite backup/restore validation (2026-11-07)

- Commits `9f01200` and `14bc2da` validate required Controller columns/types/nullability/primary keys and the full allocation and command-revision uniqueness rules before backup publication or restore replacement.
- The validator accepts the genuine v2 schema built by migrations 001/002, rejects a downgraded current schema missing its post-003 partial index, and rejects expression/extra-term legacy indexes. Failed restore validation preserves the destination.
- Focused `DatabaseMaintenanceTests` passed 19/19; full `Controller.Tests` passed 161/161 with zero skipped. Test databases were temporary and artifacts used isolated paths on D:. Independent Luna Max review approved the final change.
- No production database backup/restore, service change, firewall change, Sunshine action, or Restore Rx interaction occurred. Production restore drill, backup ACL/retention, and certificate/secret recovery remain outstanding.

### Task 15: Documentation and test harness reconciliation (2026-11-07)

- Ignored all root-level `.next-*` isolated build outputs without deleting or moving them. The active `.next` junction and `.next-local` dev output were left untouched; `NEXT_DIST_DIR` remains available for isolated runs.
- An isolated Webpack production build at `.next-task15-validation` passed after one transient Google Fonts timeout/retry; `/admin/machines` is Partial Prerender. `pnpm test:machines` passed 8/8 VNC draft/source-address tests and 22/22 machine configuration scenarios; `pnpm test:auth` passed 8/8; `pnpm exec tsc --noEmit` passed; `pnpm lint` passed with zero errors and one existing `<img>` warning.
- The Agent package remains unpublished, `AGENT_INSTALL_ENABLED=false`, no Agent/Guard service was installed, and rental readiness remains NOT READY. Hardware acceptance and Restore Rx qualification remain NOT TESTED; no Restore Rx state was queried or changed.
