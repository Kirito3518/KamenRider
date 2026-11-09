# แผนระบบเช่าคอม KamenRaiderPlay ฉบับเต็ม

สถานะเอกสาร: roadmap รวมของระบบเว็บ, Controller, Agent และช่องทางดูแลเครื่อง  
อัปเดต: 2026-11-09

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
- การอัปเดต Agent ใช้ปุ่ม **Update Agent** แยกต่อเครื่อง พร้อม maintenance lock และ rollback ตามที่ผู้ใช้อนุมัติ; การ publish release ไม่เริ่ม rollout และไม่ทำให้เครื่องผ่าน approval/readiness โดยอัตโนมัติ

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
- ถ้า service หยุดหรือ ticket หมดอายุก่อน enrollment คำสั่ง admin ใหม่จะตรวจ service, binary, config และ identity เดิม, ซ่อม DACL ของ ticket ที่มีอยู่ให้ Agent อ่านและลบได้, แล้วเริ่ม/resume service โดยไม่แก้ identity/config; ปฏิเสธ installation ที่ไม่ครบหรือค่าไม่ตรงกัน
- service/ข้อมูลเดิมที่มีอยู่และ package ที่ hash ไม่ตรงถูกปฏิเสธ; service ที่สร้างโดยรอบติดตั้งนี้เท่านั้นที่ถูกลบเมื่อ rollback
- การติดตั้งจริงรอบแรกวันที่ 2026-11-08 rollback ก่อนสร้าง service เพราะ verifier ของ Stream Guard ไม่รับ `OverrideBlockRules=null`; รอบถัดมาผ่าน firewall gate และสร้าง Agent/Guard แล้ว แต่ Agent หยุดจากสิทธิ์ลบ enrollment ticket ไม่พอ และ TightVNC rollback เพราะไม่มี service แบบ Automatic. Inventory หลังรอบล่าสุดพบ Agent Stopped/Automatic, Guard Running/Automatic, Sunshine Stopped/Manual และไม่มี `tvnserver`; ยังต้องรันคำสั่ง Admin รุ่นแก้ไข (ดู `docs/agent-acceptance.md`). ผล software integration ของ lease/reconcile ใช้ Controller TestServer และ fake OS/Sunshine/evidence adapters จึงไม่ยืนยัน installation, Restore Rx, Windows firewall/SCM, TLS trust หรือ hardware readiness.
- Historical snapshot before Task 33: `.env.local` used localhost URLs (`http://localhost:3000`, `https://localhost:7443`) and `AGENT_INSTALL_ENABLED=true`; package `1.0.0+gf1bc320f49ab` from `f1bc320` (SHA-256 `8F18E57F4052770B02907A030C5574466B6C8E4D1F5762118979080522E85920`) was current at that time. Task 33 and the current status summary below supersede that package and machine-metadata snapshot.
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
- Controller ปฏิเสธ heartbeat ที่ `appliedConfigRevision` มากกว่า revision ที่ออกจริง; เมื่อ config เปลี่ยน เครื่องจะเช่าไม่ได้จนกว่าจะส่ง ACK revision ปัจจุบัน
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
- Agent บันทึก deadline ที่สั้นลงจาก Controller ก่อนรอ stream gate; regression tests ครอบคลุม restart ระหว่าง gate ค้าง, extension ที่ยังไม่ยืนยัน, confirmation ที่ storage ค้างข้าม recovery deadline และ shutdown timeout/retry ด้วย fake adapter; production shutdown adapter จำกัดเวลารอไว้ 15 วินาที; Task 21 เพิ่ม software restart-state tests สำหรับ orphaned staging files และ snapshot ที่ publish/clear แล้ว โดยไม่จำลอง physical power loss หรือพิสูจน์ Windows filesystem crash semantics; process crash จริงระหว่าง atomic replacement และการทดสอบ shutdown/retry บนเครื่องจริงยังเป็น acceptance แยกต่างหาก
- ระยะแรกใช้ trial lease ของแอดมิน ไม่หัก balance; payment/refund เป็นงานแยก
- เมื่อ boot transition ผ่าน proof และ Controller ยืนยัน `PrepareAvailable` แล้ว reconciliation ปิด lease เก่าเป็น `completed` และปล่อย allocation; migration 010 เปลี่ยน constraint แบบรักษา rowid และ child references

### 6. Moonlight/Sunshine และ stream fence — Guard/adapter และ installer Plan/mock ผ่าน; live installation และ hardware qualification ยังขาด

- Agent เรียก Sunshine management API บน localhost เท่านั้น; credential ไม่ออกไป browser
- Foundation Sunshine adapter ใช้ช่อง pairing เดียว ส่ง PIN/ชื่อ client และรอยืนยัน handshake จาก client list; token ที่เว็บเลือกเป็น pairing slot ของ Agent ไม่ใช่ client ID จาก Sunshine
- จับคู่ PIN เฉพาะ lease/owner ที่ active; PIN ไม่ลง log และล้างเมื่อหมดอายุ
- ล้าง client เก่าก่อนคืนเครื่องได้หลังมี fence ที่พิสูจน์ว่าปิด pending handshake และ stream เดิมแล้ว; Sunshine fork ไม่มี API ยกเลิก pending PIN
- มี LocalSystem Stream Guard, named-pipe protocol ที่จำกัด Agent service SID, startup fence และ lease deadline enforcement; Agent ยังคงใช้ virtual service account
- มี Windows Firewall/SCM provider และ coordinator ที่ block ก่อนหยุด Sunshine, เริ่ม service ขณะ block อยู่ และเปิดกฎหลังได้ lease grant ปัจจุบันเท่านั้น
- Task 5 installer prepares the initial block and Guard before Agent enrollment; Windows PowerShell 5.1/7 plan, rollback and package checks pass. The first live attempt exposed a verifier mismatch and rolled back. The second reached Agent enrollment but the Agent stopped while clearing its ticket; VNC setup also failed and rolled back. Current service state and corrective package are recorded in `docs/agent-acceptance.md`.
- Fence ต้องกันการเชื่อมต่อใหม่และตัด stream เดิม; ต้องพิสูจน์กฎ block กับ allow rules เดิม, ทุก profile/interface, IPv4/IPv6 และ stream ที่เปิดค้างบนเครื่องทดสอบ
- readiness ต้องตรวจ Sunshine, desktop/capture, encoder, audio และ input จริง
- ตรวจ source ของ Foundation Sunshine รุ่นที่เครื่องนี้ติดตั้งแล้วและปรับ adapter/schema tests ให้ตรง; Agent register `LazySunshinePairingSession` ซึ่งจะสร้าง `SunshineClient`/`PairingSession` เมื่อมี credential file ที่ป้องกันด้วย DPAPI/ACL และใช้ `SunshinePort` ล่าสุดจาก applied configuration; มี CLI provision/rotate แบบไม่ใส่ secret ใน command line. Guard maintenance boundary มี implementation ใน `StreamGuardPipeClient`/coordinator แล้ว แต่ยังไม่ได้ qualify API, credential TLS trust, cleanup, หรือการสตรีมบนเครื่องจริง
- ทดสอบ Moonlight client ใหม่/เก่าและ stream ที่เปิดค้างเมื่อหมดเวลา

### 7. Reconcile หลังบูตและพร้อมเช่ารอบใหม่ — lease import ต่อ production แล้ว; boot evidence ยังขาด

- Agent ปิด stream fence ก่อน sync/reconcile ทุกครั้ง
- Agent ส่ง signed heartbeat สถานะ notReady ได้ระหว่างรอ reconciliation โดย Controller อัปเดต presence/config ACK แต่ไม่เปลี่ยน boot history, lease, command หรือผลคำสั่ง และไม่ส่ง command/lease confirmation; Agent เก็บผลคำสั่งค้างไว้จน Ready sync
- Agent dispatch คำสั่ง `PrepareMachine` แล้ว ตรวจ boot ID, อายุคำสั่ง และไม่มี lease ปัจจุบัน/lease ที่รอ commit ก่อนเรียก `IMachinePreparation`; ถ้าไม่มี provider ที่ใช้งานได้จะตอบ `PreparationUnavailable` แทนการทิ้งคำสั่งและรอ timeout. มี FencedMachinePreparation ใน Agent host: ตอบรับเฉพาะ ready sync ที่ผ่าน boot proof และ runtime health แล้ว, ปิด stream gate และตรวจ command/สถานะ lease ซ้ำ. ค่า boot/restoration/health เริ่มต้นยัง unavailable จึงยังเริ่มเช่ารอบใหม่จริงไม่ได้จนกว่าจะมี provider ที่ให้หลักฐานผ่านการ qualification
- สถานะปัจจุบัน: boot environment และหลักฐาน baseline ต่อผ่าน DI แล้ว โดยค่าเริ่มต้นเป็น StreamGuardBootEnvironment/Unvalidated; provider ต่อ boot pairing cleanup ผ่าน ISunshinePairingSession ซึ่งใช้ StreamGuardPipeClient และจะผ่านเมื่อ guarded cleanup สำเร็จเท่านั้น; BootReconciler เรียก cleanup ได้หลัง baseline และ restoration ผ่าน แต่ gate เริ่มต้นทั้งสองยัง unavailable จึงปกติหยุดก่อน cleanup และทิ้ง streamFence เป็น unknown; desktop health และ runtime health ยัง unavailable; BootReconciler ใช้ production lease importer ที่ต่อกับ LeaseEnforcer หลังได้รับ proof ครบและ Controller ยืนยัน ResumeLease แล้ว โดยนำ controller time กับเวลาเริ่ม request มาคำนวณ monotonic deadline; เมื่อ Controller ยืนยัน PrepareAvailable หลัง proof จะล้าง snapshot เก่าด้วย atomic tombstone เฉพาะข้าม boot และปฏิเสธการล้าง lease ที่ยังอยู่ใน boot เดียวกัน; หลัง reconciliation ผ่าน Agent ตรวจ runtime health ซ้ำทุกรอบ sync; เมื่อ health/boot/config ไม่ตรงจะปิด gate และรายงาน notReady; ถ้า config revision เปลี่ยนใน sync response Agent จะ apply config, ปิด gate, และไม่ประมวลผล command/confirmation จาก readiness proof เก่า; LeaseEnforcer เปิด gate ได้หลังมี ActiveLease confirmation ตรงกับ lease/boot/revision และคง confirmed=false พร้อมตั้ง fault หากเปิดไม่สำเร็จ; Agent host ใช้ StreamGuardPipeClient ซึ่ง fail closed เมื่อไม่มี Guard แต่เครื่องพัฒนานี้ยังไม่ได้ติดตั้ง Guard; ห้ามอ้างว่า Sunshine ถูกกั้นหรือเครื่องพร้อมเช่า
- หลักฐานที่มีอยู่ยังไม่ให้เกณฑ์ผ่านสำหรับ restoration หรือ desktop/runtime health: boot ID, uptime และ config revision ยืนยันได้เพียง boot/config continuity ไม่ได้ยืนยันว่า Restore Rx restore volume หรือ Sunshine capture/input/audio ทำงานจริง. Provider production จึงต้องคงสถานะ unavailable จนมีแหล่งหลักฐานที่ผ่าน qualification; unit tests และ fakes ตรวจ contract เท่านั้น ไม่ใช่หลักฐานจากเครื่องหรือ cold boot.
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
- แอดมินเห็น IP ที่ Agent รายงานและบันทึก IP เป้าหมาย VNC ที่ตรวจสอบกับเครื่องจริง; Controller ใช้ IP ที่แอดมินบันทึกไว้ ไม่ใช้ค่าที่ Agent เปลี่ยนใน heartbeat และปฏิเสธ IP ที่ไม่ใช่ private LAN. ปุ่มตรวจเป้าหมายทดสอบ RFB บน IP เอกชนที่ระบุและ TCP 5900 เท่านั้น จำกัดเวลา 3 วินาที และปิดก่อนส่งรหัสผ่าน
- Controller ออก ticket ใช้ครั้งเดียว อายุ 1 นาที ผูกกับ origin; WSS ใช้ IP ที่แอดมินยืนยันกับ port 5900 ที่กำหนดฝั่ง server ไม่รับ host/port จาก browser
- Controller จำกัด session ไว้ 1 ชั่วโมงและรับเฉพาะ RFB 3.7/3.8 ที่ใช้ VNC authentication; gateway ส่ง RFB handshake, Raw framebuffer, desktop-size และ control message ที่รู้ความยาวแน่นอน, ปฏิเสธ keyboard, mouse, clipboard จาก browser, XVP หรือข้อความที่ไม่รู้จัก, ตัด standard ServerCutText และปิด session เมื่อเจอ extended clipboard ที่ไม่ได้ต่อรอง
- noVNC เปิด view-only และตัด clipboard negotiation; viewer ไม่ส่ง clipboard ไปยังเครื่องหรือใส่ clipboard ของเครื่องแอดมิน; VNC server ต้องเริ่มด้วย true-color 32 bpp และรองรับ Raw encoding
- ฐานข้อมูลเก็บ admin, machine, เวลาเริ่ม/จบ; หน้าแอดมินแสดงจำนวน session ที่กำลังดูจาก session ที่ยังไม่จบและเริ่มภายใน 1 ชั่วโมงล่าสุด; noVNC ตั้ง view-only เพิ่มอีกชั้นหนึ่ง
- Controller ลบ ticket ที่หมดอายุและยังไม่ใช้, session ที่ค้างเกิน 65 นาทีหลัง Controller หยุดผิดปกติ, และเก็บ session ที่จบแล้วไว้ตรวจย้อนหลัง 30 วัน; มี regression test ครบทั้งสองกรณี
- มี PowerShell installer สำหรับ TightVNC 2.8.90 x64 รวมใน Agent package โดยดาวน์โหลด MSI แยก, ตรวจ SHA-256 ที่ pin ไว้, ลายเซ็น `OOO GlavSoft` และข้อมูล MSI, ตั้งเฉพาะ Server, ปิด viewer ผ่าน HTTP, บล็อก input และจำกัด firewall ไปยัง Controller IPv4 บน Private profile; ต้องเปิด Windows Firewall Private profile และ installer ปฏิเสธ TightVNC service/ไฟล์ที่ตรวจพบแต่ไม่มี managed marker
- การติดตั้งใหม่ตั้ง firewall source rule ก่อน แล้วเรียก MSI fresh install ครั้งเดียวพร้อมลงทะเบียน service, เปิด RFB โดยจำกัดต้นทางที่ Controller IPv4 และตั้งรหัสผ่าน/secure transform ใน transaction เดียว; maintenance API ใช้เฉพาะ repair ผลิตภัณฑ์ที่ติดตั้งและมี managed marker แล้ว. Transform เก็บใน data directory ที่ ACL จำกัด; ปฏิเสธ Windows Installer Debug policy เมื่อเปิด command-line logging bit `0x04` (ค่า 4–7); password ไม่อยู่ใน process command line
- Agent สร้างรหัสผ่าน VNC แยกต่อเครื่อง, เก็บไว้ใต้ data directory ด้วย ACL สำหรับ SYSTEM/Administrators และแสดงในหน้าต่างติดตั้งหนึ่งครั้ง; plan mode ไม่สร้างรหัสหรือแก้บริการ/ไฟร์วอลล์
- ตั้ง VNC หลัง enrollment สำเร็จเท่านั้น; ถ้า Agent ยัง pending ให้รันคำสั่งจากหน้าแอดมินซ้ำเมื่อเครื่องติดต่อ Controller ได้
- Historical snapshot ก่อน Task 5 follow-up: Agent checks ทั้ง 11 รายการรวม package build ผ่าน Windows PowerShell 5.1; PowerShell 7 checks ที่ไม่สร้าง package 10 รายการผ่าน แต่ Antivirus เคยบล็อก cleanup หลัง package-build assertions. หลังผู้ใช้ allow/restore, Task 5 follow-up ผ่าน package plan/install และ package build บน Windows PowerShell 5.1 และ PowerShell 7 โดยไม่พบ antivirus block ซ้ำ (ดูหัวข้อ Stream Guard installer ด้านล่าง). TightVNC MSI ทางการถูกดาวน์โหลดและตรวจ signer/hash/metadata/transform แล้ว แต่ยังไม่ได้ติดตั้ง MSI; ไม่มี VNC server บนเครื่องลูกที่ผ่านการทดสอบ
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
- Software regressions already cover concurrent lease allocation across database connections and durable retries (`LeaseTests.AllocationIsUniqueAcrossConnectionsAndRetriesAreDurable`), command replay and Controller restart (`CommandTests` and `BootChallengeTests`), offline boot and Agent lease-state restart (`BootTests`, `LeaseStoreRestartTests`, `LeaseEnforcerTests`), and expiry boundaries. These are simulated software checks; live Agent/Guard service crash recovery, disconnected expiry/shutdown, WOL, stream cutoff, and cold boot remain hardware acceptance items in `docs/agent-acceptance.md`.
- Hardware matrix: WOL, stream isolation, active session cutoff, old-client reconnect, cold boot, audio/input/capture, external ports
- บันทึก pass/fail/not-tested แยก fake tests กับ hardware tests
- Controller มี online verified SQLite backup/restore CLI พร้อม activity lock และ pre-restore snapshot; validator ตรวจ required columns/types/nullability/primary keys และ allocation/command-revision uniqueness ก่อนทำ snapshot หรือแทนที่ฐาน; ทดสอบด้วยฐานชั่วคราวเท่านั้น ยังต้องทำ production restore drill, รับรอง ACL/การเก็บ backup และทำ certificate/secret recovery กับ identity revoke/re-enroll runbook
- ห้ามประกาศ rental-ready จน gate, expiry, installation และ hardware acceptance ผ่านครบ

### 11. Agent release/update — software validation ผ่าน; final fix wave รอ scoped re-review

- มีหน้า `/admin/agent-releases` สำหรับสถานะ fleet, version/outcome counts, stable/beta history และ publish complete `AgentPackage.zip`. Release build ใช้ version ชัดเจนและอยู่นอก `public/`; Controller ตรวจ manifest กับ binary, clean release metadata, archive limits และ SHA-256 ก่อนเก็บแบบ immutable. ไม่ใช้ raw exe และไม่เริ่ม update จากการ publish/poll.
- Workflow ที่ยืนยัน: แอดมินเลือก release ที่ต่างจาก version ปัจจุบันและ Generate **Update Agent** สำหรับเครื่อง pending หรือ approved โดยไม่เปลี่ยน approval, heartbeat ล่าสุดมี version และสดไม่เกิน 20 วินาที, ไม่มี held lease. Controller จอง operation กับ maintenance lock ใน transaction เดียวกับ lease allocation; ระหว่าง lock `canRent=false` และ API ปฏิเสธ lease ใหม่. คำสั่งต้องใช้ elevated maintenance session บนเครื่องทดสอบที่พร้อม; Task 6 ไม่ได้รันบน host จริง.
- Ticket ที่ยังไม่ start หมดอายุใน 10 นาทีและปล่อยเฉพาะ pending reservation. หลัง start แล้ว failed/interrupted/uncertain stop, restore หรือ restart ต้องคง lock; **Recovery** เปลี่ยน ticket ของ operation เดิม ไม่สร้าง operation ใหม่หรือปลด lock. ตรวจว่าไม่มี updater รุ่นเก่าที่ไม่ใช้ local lock ทำงานอยู่ก่อนทดสอบจริง.
- Updater รายงาน `awaitingHeartbeat` หลัง swap/start ที่สำเร็จ หรือ `rollingBack` หลัง restore/restart ที่ยืนยันแล้ว; ต้องมี signed heartbeat ที่ตามมาจาก target/previous version จึงเป็น `completed`/`rolledBack`. Early heartbeat, target ระหว่าง rollback และ rollback ที่ยังยืนยันไม่ได้ไม่ปลด lock. Approval, ready/fresh heartbeat, current configuration ACK และ hardware qualification ยังเป็น gate แยก; ห้ามปลด lock ผ่านการแก้ฐานข้อมูล.
- เพิ่ม Controller HTTP acceptance 3 cases: publish → signed enrollment/pending/approve → reserve ไม่มี lease → start/download/outcome → heartbeat confirmation → rental gates. ใช้ production Controller TestServer, temp SQLite/artifacts และ ECDSA จริง; updater outcome/readiness เป็น fixture, transport/listener metadata จำลอง, DLL เป็น version fixture และ exe/scripts ใน ZIP ไม่ถูก execute. PowerShell suites แยกกันรัน updater/workflow กับ temp files และ fake HTTP/SCM/firewall/MSI; ACL/file-lock/child-process checks ใช้ OS primitives เฉพาะใน TEMP. ไม่ใช่ deployment หรือ hardware proof.
- Final isolated `.NET` solution ผ่าน Agent **244/244**, Controller **224/224**, ไม่มี skip; acceptance ใหม่ **3/3** รวมอยู่แล้ว. Full run แรกพบ Stream Guard concurrency failure (Agent 237/238); ตรวจพบ production race ระหว่าง semaphore release กับ close generation และช่องว่าง contended drain. Fix แยก `a2120e3` ปิด grant admission ระหว่าง explicit closure และ contended closure/expiry; uncontended expiry ยังอาจให้ allow รอจน closure จบได้; deterministic regression 3 cases red ก่อน fix แล้วผ่าน รวม Guard startup/composition, delayed-close retry และ fresh grant หลัง drain. Independent review อนุมัติ fix โดยไม่มี findings และขอ coverage เพิ่มอีก 3 cases สำหรับ overlapping closers กับ cleanup หลัง permanent failure; ผ่านพร้อม mutation checks ที่จับ counter regression ได้. Task 6 ผ่าน independent review แล้ว; final fix wave ยังรอ scoped re-review ไม่ใช่แค่ rerun แล้วกลบ failure.
- Windows PowerShell **5.1.22621.6133** และ PowerShell **7.6.9** ผ่าน 15 updater/package/install mock suites ต่อเวอร์ชัน (รวม 55 Sunshine cases); package build รันซ้ำหลัง Agent fix. Isolated Next.js **16.4.3** Turbopack build ผ่าน **22/22** pages; `test:machines` ผ่าน **39** UI + **9** helper tests และครบ **22** API scenarios, `test:auth` **8/8**, TypeScript exit 0, lint 0 errors กับ warning `<img>` เดิม 1 รายการ. รายละเอียด failure/warnings และขอบเขต simulation อยู่ใน [Agent acceptance](agent-acceptance.md).
- ข้อมูลอ่านอย่างเดียวที่บันทึก 2026-11-09: Agent/Guard `Running/Automatic`, Sunshine `Stopped/Manual`, TightVNC ไม่มี และ shell ไม่ elevated. Task 6 ไม่ได้แก้ services/firewall/MSI หรือข้อมูลเครื่องจริง ไม่ได้ติดตั้ง release updater หรือ refresh package ที่ใช้งานจริง และไม่เข้าถึง/ควบคุม Reboot Restore Rx.
- Final fix wave: causal update challenge ผูก report/ticket/operation/Controller epoch, ตรวจ manifest และ actual Agent assembly capability ก่อน publish/reserve, heartbeat ล่าสุดต้องมี version, อัปเดต pending ได้โดยไม่อนุมัติ และ backup/restore รวม verified ZIP sidecar. Final source ผ่าน Agent **245/245**, Controller **259/259**, PS 15 suites ต่อ engine และ rerun 8 affected suites, web **41 UI + 9 helper + 23 API scenarios (28 cases)**, auth **8/8**, TypeScript/build และ mobile 320/390px. ยังรอ scoped re-review.
- ยังต้องทดสอบ live updater/service transitions/rollback, private identity/config/ACL preservation, effective firewall/stream cutoff, Sunshine, VNC, disconnected expiry, WOL, external network, cold boot และ hardware/boot evidence. สถานะระบบเช่าโดยรวมยัง **NOT READY** และ hardware acceptance **NOT TESTED**; Task 6 ผ่าน independent review แล้ว แต่ release-management plan ยังไม่ปิดจน scoped final re-review ผ่าน.

## สถานะสรุปวันนี้

เว็บ/Controller/Agent มีโครงและ business logic หลายส่วนแล้ว; Controller dev และ Next dev ตอบใน localhost ได้. Inventory แบบอ่านอย่างเดียวล่าสุดพบ Agent และ Stream Guard เป็น `Running/Automatic`, Sunshine เป็น `Stopped/Manual` และไม่มี service `tvnserver`. Controller แสดงเครื่อง `PC ทดสอบ 1` ราคา `20 THB/hour` เป็น `pending/notReady/canRent=false` โดย applied config revision 2; Agent ที่รายงาน heartbeat คือ `1.0.0+g3179c802b681` และ readiness diagnostics ทั้ง 8 รายการยังเป็น `unknown`. Local package `1.0.0+g0d4fdeb686a3` มี TightVNC service-registration fix อยู่แล้ว แต่ยังไม่ได้ติดตั้ง; การ retest บนเครื่องจริงยัง pending. Rental readiness ยังคง **NOT READY** และ hardware acceptance ยังคง **NOT TESTED**; software regression ไม่ได้ยืนยันการติดตั้งหรือการทำงานจริงของ VNC, service, firewall หรือ hardware. Reboot Restore Rx ยังคง untouched.

### Stream Guard installer — Task 5 verification, 2026-11-06

The default-closed Sunshine installer transaction is implemented and verified in Plan/mock mode. The package prepares the exact closed firewall rule and automatic LocalSystem `KamenRaider Stream Guard` before starting virtual-account `KamenRaider Agent` enrollment; Sunshine becomes manual-start with prior dependencies preserved plus Guard. At this 2026-11-06 snapshot, ownership-aware rollback and existing-install validation were covered by 29 mocked cases; the final Task 5 follow-up later reported 53 mocked setup/rollback cases. All Agent PowerShell plan/install/package checks passed on Windows PowerShell 5.1 and PowerShell 7, including ZIP/SHA-256 and repeated package builds. The antivirus read block did not recur. No live install, rollback, firewall/service mutation, Restore Rx action or `AGENT_INSTALL_ENABLED` change occurred. Real-host stream isolation and boot/recovery qualification are still outstanding; the generated test packages were disposable and were not published to `public/agent`.

### Task 7: ผลซอฟต์แวร์และข้อจำกัดการ qualification (2026-11-07 snapshot; ผลล่าสุดอยู่ใน Task 14–16 ด้านล่าง)

- ใน snapshot ก่อน Task 16: Task 5 software Plan/mock/package verification ผ่านแล้ว แต่ package ยังไม่ถูก publish และ live install/Apply ยังไม่เกิด
- หลักฐานล่าสุดหลัง Task 6 fix: Agent full suite ผ่าน 218/218, ชุด focused pipe/coordinator ผ่าน 43/43 และ Controller ผ่าน 151/151. ก่อน final fix, Agent full suite ผ่าน 217/217; ใช้ผลหลัง fix เป็นสถานะล่าสุด
- Windows PowerShell 5.1.22621.6133 และ PowerShell 7.6.9 ผ่าน Stream Guard setup/rollback mock 53 cases, package plan/install checks และ package build. Allow/restore ของผู้ใช้สำเร็จและไม่พบ antivirus block ซ้ำ; Task 5, Task 6 และ review ของ fix ผ่าน
- Web checks ที่บันทึกไว้: TypeScript ผ่าน, lint ไม่มี error (มี img warning เดิมหนึ่งจุด), Webpack production build ผ่าน; Turbopack build ล้มจาก Babel loader subprocess crash ใน UI imports เดิม
- Read-only host check วันที่ 2026-11-07 (ก่อน Task 16) พบ firewall Domain/Private/Public enabled และอนุญาต local rules; ไม่มี KamenRaider Agent/Controller/Stream Guard service, package ยังไม่มี และ `AGENT_INSTALL_ENABLED=false`. ค่าตรวจ profile ไม่พิสูจน์ว่ามี managed fence หรือว่าปิดการเชื่อมต่อได้จริง
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
- At Task 15 completion, the package was unpublished and `AGENT_INSTALL_ENABLED=false`; Task 16 below supersedes that package/command status. No Agent/Guard service was installed, and rental readiness remains NOT READY. Hardware acceptance and Restore Rx qualification remain NOT TESTED; no Restore Rx state was queried or changed.

### Task 16: Local Agent package and Admin command (2026-11-07)

- Built a local `win-x64` Agent package and independently verified its recorded SHA-256, ZIP contents, required installer scripts, and absence of a bundled TightVNC MSI or default `appsettings.json`. The package and hash remain local ignored files; this document intentionally does not record a specific hash or build version.
- Enabled localhost Admin install-command generation after package verification. Existing localhost URLs, VNC source, and trust-root thumbprint were preserved.
- No install command was run. No KamenRaider Agent/Guard service, firewall rule, Sunshine setting, or Restore Rx state was changed. Readiness remains NOT READY; hardware and Restore Rx qualification remain NOT TESTED.

### Task 17: Preserve confirmed VNC target on Admin PATCH (2026-11-07)

- `/api/admin/machines/[machineId]` PATCH now preserves the Controller's saved Admin-confirmed VNC target when `vncLocalIp` is omitted. Explicit `null` still clears the target, and a supplied valid IP still saves. The Controller's `expectedRevision` check rejects concurrent stale updates.
- TDD RED reproduced the bug (`null` forwarded instead of the saved target); GREEN passed for omission, explicit clear, and explicit IP save. `pnpm test:machines` passed, including 8/8 VNC draft/source tests, 22/22 machine configuration scenarios, and the new PATCH regression. Isolated Webpack build, `pnpm test:auth` (8/8), TypeScript, and lint passed; lint retained one existing `<img>` warning. Commit: `fee9b448`.
- No Agent install, service, firewall, Sunshine, or Restore Rx state changed. Production readiness remains NOT READY; live VNC connectivity and hardware/Restore Rx qualification remain NOT TESTED.

### Task 19: Align Foundation Sunshine pairing slot contract

- The Agent adapter represents Foundation Sunshine's single pending PIN slot with the exact ID `foundation-sunshine-single-pending-slot`. The Controller must accept this exact documented slot ID in signed pairing offers and owner pairing requests, while preserving support for existing lowercase 32-character hexadecimal pairing IDs and rejecting all other values.
- Define the Foundation slot ID once in the shared Contracts assembly and use it from both Agent and Controller validation; add regression coverage proving an offer reaches the renter and can be queued as a `PairPin` command, plus Agent coverage proving the adapter returns the shared value.
- Verify with targeted Agent and Controller tests. This is software-only; do not install services or modify firewall/Sunshine/Restore Rx state. Real Moonlight pairing and stream qualification remain separate hardware acceptance.

### Task 20: Show enrollment ticket expiry in Admin installer

- The authenticated Admin install-command response returns the Controller ticket's exact `expiresAtUtc` value along with the generated command.
- `/admin/machines` shows when the displayed command expires, changes to an expired state and disables copy at/after the deadline, and lets the administrator explicitly refresh to request a new command/ticket. Do not issue replacement tickets automatically in the background.
- Add route coverage for the expiry value and focused tests for the expiry boundary/state logic. This is software-only; it does not enroll or install a machine.

### Task 21: Verify lease snapshot restart boundaries

- Use the real `LeaseStore` and isolated temporary directories to cover restart after a durable snapshot is published, restart with an orphaned/incomplete `.tmp` file from a write interrupted before publication, and restart after a clear tombstone is published. Also verify a rejected `SaveIfAsync` keeps the prior committed snapshot.
- Keep these as software restart-state tests; they must not claim to simulate a physical power loss or prove Windows filesystem crash semantics. Update the roadmap to keep process-crash and machine shutdown acceptance separate.
- Run the full Agent test suite. Do not read or modify Restore Rx, install services, or change machine state.
- Verification: the four `LeaseStoreRestartTests` passed 4/4 and the full `Agent.Tests` suite passed 226/226 with zero failures or skips. A new `LeaseStore` instance loads the saved directory contents; this does not terminate a process mid-write, simulate physical power loss, prove Windows atomic replacement/crash behavior, or qualify shutdown/retry on a real machine. Those remain separate acceptance work.

### Task 22: Reconcile latest software evidence in acceptance record

- Add a latest-software update to `docs/agent-acceptance.md` with the Task 19–21 evidence recorded in the SDD ledger: Agent.Tests 226/226 with zero failures/skips; focused LeaseStoreRestartTests 4/4 after the final filename-fixture correction; Controller.Tests 164/164 for the pairing contract; and Task 20 machine tests, TypeScript check, and isolated Webpack build passing.
- Keep prior test counts clearly historical, and continue to state rental readiness as NOT READY with hardware acceptance NOT TESTED. Software tests must not be presented as live installation, boot restoration, firewall, Sunshine, VNC, or external-network proof.
- Documentation-only. Do not run tests, install services, change machine state, or read or modify Restore Rx.
- Complete: the acceptance record now labels older suite counts as historical and records Task 19–21 evidence. Agent.Tests 226/226 predates the fixture filename correction; focused LeaseStoreRestartTests 4/4 follows it. Task 19 Controller.Tests passed 164/164, Task 20 machine tests/TypeScript/isolated Webpack build passed, and readiness plus live hardware/Restore Rx status remain NOT READY / NOT TESTED. No tests or host operations were run for this documentation update.

### Task 23: Keep Agent package dependency metadata on the current revision

- The actual local ZIP showed its Agent assembly informational version at the current build revision while `Agent/Agent.deps.json` still named an older dirty revision. The package build must publish both identities from the same source-specific `AgentVersion`.
- Add a package-build regression that compares the Agent project identity in `Agent/Agent.deps.json` with the returned `AgentVersion`; demonstrate the existing stale-metadata failure before changing the publish pipeline.
- Make the minimal publish/restore change that refreshes dependency metadata when `Version` changes, without changing runtime behavior. Verify with the Agent package-plan and package-build PowerShell checks on PowerShell 7.
- After the code commit, rebuild the local win-x64 package from clean main and update only `AGENT_PACKAGE_SHA256` in `.env.local`; verify version, required ZIP entries, absence of TightVNC MSI/default appsettings, and matching archive/record/environment hashes. No tests are required after this final artifact refresh.
- Do not install services or change machine/firewall/Sunshine state, disclose other environment values, or read or modify Restore Rx.
- Complete: the package-build regression reproduced the stale `Agent/Agent.deps.json` identity before the fix and passed after it; the package-plan check passed. Publishing now cleans the selected Release/RID outputs so dependency metadata is regenerated for the requested version. Fix committed as `5468891`; runtime behavior and dependencies are unchanged.

### Task 24: Display readiness diagnostics only to administrators (2026-11-07)

- The Admin machine list now renders the Agent's latest bounded readiness check IDs and statuses with Thai labels, a diagnostic-only caption, and a clear not-reported state for older Agents. The renter-facing `/api/machines` response continues to omit diagnostics.
- The diagnostics are display-only: readiness, rental eligibility, lease allocation, and stream-gate behavior are unchanged.
- Verification: the focused Admin UI suite passed 6/6; `NEXT_DIST_DIR=.next-task2-validation pnpm test:machines` passed, including 9/9 VNC/installer helper checks and the public machine route regression; `pnpm exec tsc --noEmit` passed; isolated Webpack production build passed with `/admin/machines` as Partial Prerender. Targeted ESLint reports one pre-existing `react-hooks/set-state-in-effect` error at the unchanged install-command effect in `machines-manager.tsx:128`.
- Rental readiness remains NOT READY and hardware acceptance remains NOT TESTED. This display change does not qualify boot/restoration/desktop evidence, installed Agent/Guard, live VNC/Sunshine/WOL, or a machine for rental; no host, service, firewall, Sunshine, or Restore Rx state was changed.

### Task 25: Fix live Stream Guard block-rule verification (2026-11-08)

- The first same-host install reached Windows Firewall publication, then failed because the verifier required explicit `OverrideBlockRules=False` on a plain Block rule. Firewall events 2097/2052 and a post-failure read-only inventory confirm the rule was created then removed during rollback; no KamenRaider service or install directory remains and Sunshine was restored to its prior Running/Automatic configuration.
- A TDD null-property fixture reproduced the exact line 353 failure. The verifier now accepts null or False for the inapplicable property and rejects True; it retains the exact enabled inbound Block, all-profile, Sunshine-program and filter checks.
- Windows PowerShell 5.1 and PowerShell 7.6.9 passed 55 mocked setup/rollback cases. Windows PowerShell 5.1 `Test-AgentPackageBuild.ps1` passed. The published local package was rebuilt from clean source commit `656becd` as Agent version `1.0.0+g656becdb146c`; SHA-256 and contained verifier hash match their records/source.
- The installer has not been retried. Rental readiness remains NOT READY; hardware acceptance remains NOT TESTED. No live Apply or Restore Rx operation was performed by this fix.

### Task 26: Repair enrollment ticket permissions and fresh TightVNC installation (2026-11-08)

- The next same-host install passed Stream Guard publication and registered the Agent and Guard. Enrollment produced a pending machine receipt, but Agent then stopped because its protected `enrollment-ticket.txt` ACL did not grant file Delete. TightVNC setup also failed because the MSI maintenance reconfiguration did not register `tvnserver`; rollback removed the partial VNC product. A read-only service inventory now shows Agent Stopped/Automatic, Guard Running/Automatic, Sunshine Stopped/Manual, and no `tvnserver` service.
- Commit `7b71017` gives the Agent ticket only Read+Delete through a DACL-only update, and repairs an existing ticket ACL before restarting the existing Agent. Fresh TightVNC setup now publishes the exact Controller-source Private-profile firewall rule before one `MsiInstallProduct` call containing service-registration, VNC-authentication, input-blocking, source-filter and secure-transform properties. Existing managed TightVNC repair still uses the MSI maintenance API.
- Agent.Tests passed 236/236; targeted Agent install, private-file, TightVNC, Stream Guard, bootstrap and package-plan checks passed on Windows PowerShell 5.1/PowerShell 7; the package build test passed. The local win-x64 package version is `1.0.0+g7b710174a8f0`, SHA-256 `FE1C9B4FDB6085D709D84A63EC122975B8CAFAAEA597633C9761B2E07F3C1BFA`; `.env.local` matches and the local package URL responds HTTP 200.
- The repair package has not yet been rerun on the machine. Rental readiness remains NOT READY; VNC, live fence, boot/restoration, external networking, and hardware acceptance remain NOT TESTED. No Reboot Restore Rx interaction occurred.

### Task 27: Match enrollment ACL access rules by SID (2026-11-08)

- The next repair attempt stopped during ticket ACL verification. Read-only inspection showed the ticket DACL had the intended Agent rights (`Read`, `Delete`, `Synchronize`), but `Get-Acl.Access` exposed the service identity as an `NTAccount` while the verifier compared it directly to a `SecurityIdentifier`.
- The verifier now requests explicit and inherited access rules as `SecurityIdentifier` objects before matching the service SID. The regression test exercises that lookup on a Windows ACL; review found no security regression.
- `Test-PrivateInstallFiles.ps1` passed on Windows PowerShell 5.1 and PowerShell 7 using temporary files only; `Test-AgentPackageInstallPlan.ps1` passed as well. Commit `9d0582c` contains the SID-based verifier and regression. The local win-x64 package was rebuilt as `1.0.0+g9d0582c3c0e3`, SHA-256 `5AE4A5928C91A38B130C85BDBDF4BA007B216A3A30399A571EA4706618DDAB48`; only `AGENT_PACKAGE_SHA256` was updated in `.env.local`, and HTTP 200 from the local website returned matching archive bytes with required entries.
- The local Admin page and machine API returned HTTP 200 with one machine record during an Admin-session check. The repair package has not been run on the host. Rental readiness remains NOT READY; Restore Rx remains untouched.

### Task 28: Compose boot pairing cleanup through Stream Guard (2026-11-08)

- Production `AgentHost` now wires `IBootEnvironment` to `StreamGuardBootEnvironment`, which delegates cleanup through the registered `ISunshinePairingSession`. `Passed` requires successful guarded cleanup; false maps to `Blocked`, provider exceptions to `Unavailable`, and caller cancellation propagates.
- `BootReconciler` reaches cleanup only after baseline and restoration evidence pass. The default baseline/restoration providers remain unavailable, so current reconciliation stops before cleanup and later diagnostics remain unknown. Desktop and runtime health also remain unavailable; this does not qualify Reboot Restore Rx or any hardware condition.
- TDD RED was observed for missing cleanup composition and failed cleanup. Focused composition tests passed 7/7, and full `Agent.Tests` passed 238/238 using isolated artifacts. No Restore Rx, host service, firewall or Sunshine state was changed. Rental readiness remains NOT READY.

### Task 29: Enforce rental eligibility and refresh renter lease status (2026-11-08)

- Public `/api/machines` now filters to approved rows with `canRent === true`; a fresh Next build and machine route harness verified that non-rentable, unapproved, missing and non-boolean rentability rows are excluded. Existing public projection/redaction remains in place.
- `/api/my-leases` adds only a bounded reachability state and last heartbeat from Controller machine status, without returning local machine/network fields. `/my-machines` polls every 15 seconds while visible and uses mutation-aware snapshot handling so older GETs or delayed extension responses cannot overwrite newer state; current snapshots remove omitted leases and clear canceled pending revisions.
- TDD regressions passed for both changes. Focused renter Vitest passed 2 files/7 tests; TypeScript, scoped ESLint, `pnpm test:machines` and a fresh Webpack build passed. `/admin/machines` remained Partial Prerender with no uncached-data error, and local Admin page/API returned HTTP 200 with one machine record.
- These web changes do not establish Agent installation or machine readiness. Hardware, VNC, Moonlight, WOL, external networking and Restore Rx acceptance remain incomplete.

### Task 30: Refresh local Agent package and validate Admin account (2026-11-08)

- Rebuilt the local win-x64 Agent package after commit `f1bc320`, version `1.0.0+gf1bc320f49ab`, SHA-256 `8F18E57F4052770B02907A030C5574466B6C8E4D1F5762118979080522E85920`. The package build check passed on Windows PowerShell 5.1; archive, sidecar, `.env.local` hash and localhost-served bytes matched. The ZIP contains required installer files and excludes default `appsettings.json` and the TightVNC MSI.
- Created a local test Admin account and verified `/admin/machines` and `/api/admin/machines` with HTTP 200. This confirms local web access only; the Agent repair package has not been rerun on the host.
- Rental readiness remains NOT READY; the current inventory still reports Agent Stopped/Automatic, Stream Guard Running/Automatic, Sunshine Stopped/Manual, and no TightVNC service. Hardware, live VNC/streaming, network and Restore Rx qualification remain outstanding.

### Task 31: Guard configuration ACKs and verify Admin VNC targets (2026-11-08)

- Controller now rejects a heartbeat that claims an `appliedConfigRevision` greater than the latest revision it issued. The regression showed the old behavior accepted the future ACK; after the fix, the configuration change keeps `canRent=false` until an ACK for the actual revision arrives. Full `Controller.Tests` passed 177/177.
- Admin VNC target entry no longer reads a deferred React event after it is invalidated. Admins can probe a target only for a machine with a heartbeat from the last 20 seconds, using one private LAN IP and fixed TCP 5900. The Controller applies a 3-second timeout, accepts only RFB 3.7/3.8 advertising VNC authentication, and closes before credentials. The check neither saves the target nor creates an audited VNC session; no host/port is accepted from the browser. The UI labels successful results with local probe time and clears them when the draft IP changes.
- TDD regressions passed: MachinesManager UI 10/10, VNC target-draft 3/3, and Controller VNC checks 10/10. `pnpm test:machines` passed, including the new same-origin API assertions; TypeScript, scoped ESLint and a fresh Webpack production build passed. The build exposes `/api/admin/vnc-target-check` and retains `/admin/machines` as Partial Prerender.
- Live Controller state remains one pending machine with readiness unknown, no heartbeat/config ACK or Agent version, and `canRent=false`. The latest Agent package has not been rerun. Live TightVNC/viewer, stream, WOL, cold-boot and external-network acceptance remain outstanding; Reboot Restore Rx remains untouched.

### Task 32: Require explicit POST for Agent install commands (2026-11-08)

- Commit `b6af8ac` changes the install-command route to accept only an authenticated, same-origin Admin POST. GET does not issue an install command or mint an enrollment ticket.
- The `/admin/machines` page no longer requests a command on mount. An Admin must explicitly click the install button to request a fresh command.
- `pnpm test:machines` and `pnpm build` passed. The machine test suite covers the focused MachinesManager UI and install-command route regressions, including no request on mount, explicit POST, and the authenticated same-origin Admin boundary.
- This is software-flow evidence only. No installed service or machine state changed. Rental readiness remains **NOT READY** and hardware acceptance remains **NOT TESTED**; these checks do not qualify installation, live services, VNC, streaming, WOL, restoration, external networking, or Restore Rx behavior.

### Task 33: Refresh the local Agent package from current HEAD (2026-11-08)

- Rebuilt the local win-x64 package from clean HEAD `0d4fdeb686a3` as Agent version `1.0.0+g0d4fdeb686a3`, SHA-256 `60F6245B241134826130A003DDD09DE6C7A30FEBE254E8A08BCB37D3C2F1F5F8`.
- Windows PowerShell 5.1 `Test-AgentPackageBuild.ps1` passed. The package assembly informational version and `Agent.deps.json` project identity both match the source revision reported by the build; the required archive entries are present, and the default `appsettings.json` and TightVNC MSI are excluded.
- The archive SHA-256, sidecar, `.env.local` `AGENT_PACKAGE_SHA256`, and localhost-served bytes match. The static package URL returned HTTP 200 and 36,597,772 bytes.
- This verifies the local package artifact and served bytes only. The package has not been run on the host. Rental readiness remains **NOT READY** and hardware acceptance remains **NOT TESTED**; no enrollment ticket, service, machine, firewall, Sunshine, or Restore Rx state was changed.

### Task 34: Cover TightVNC service registration regression (2026-11-08)

- The root cause was the prior fresh-install split: its first MSI call used `SERVER_REGISTER_AS_SERVICE=0`, then `MsiConfigureProductEx` attempted to enable service registration. That maintenance API does not replay the MSI service-registration install action, so the `tvnserver` service was not created.
- Existing source fix `7b71017` sends `SERVER_REGISTER_AS_SERVICE=1` in the single fresh `MsiInstallProduct` call, then verifies and starts the service. Managed repair keeps its separate `MsiConfigureProductEx` path. This task adds regression coverage and documentation only; production installer behavior was not changed.
- The focused Windows PowerShell 5.1 regression exercises `Invoke-TightVncMsiInstall` through a recording API test double, verifies the captured fresh-install properties and call counts, and verifies managed repair still uses `MsiConfigureProductEx`. The check passed without installing an MSI or service. A temporary `SERVER_REGISTER_AS_SERVICE=0` mutation failed at the new fresh-install assertion as expected.
- Refreshed package `1.0.0+g0d4fdeb686a3` contains the existing fix and remains uninstalled. The real-machine retest is pending. Rental readiness remains **NOT READY** and hardware acceptance remains **NOT TESTED**; this software regression does not qualify live VNC, services, firewall, or hardware behavior.
