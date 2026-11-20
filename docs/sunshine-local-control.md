# Sunshine บนเครื่องลูกค้า — โมเดล 0jay (ไม่มี Stream Guard)

ตั้งแต่การเปลี่ยนสถาปัตยกรรม (2026-11-20) ระบบ **ไม่มี `KamenRaider Stream Guard` อีกต่อไป**:

- `SunshineService` ถูกตั้งเป็น **Automatic และรันตลอดเวลา** — Agent ไม่หยุด/สตาร์ต service และไม่แตะ firewall
- Agent คุย Sunshine ผ่าน REST บน loopback (`https://127.0.0.1:<sunshinePort>`) เท่านั้น
- **ประตูสตรีมคือการจับคู่ (pairing)** — Sunshine จะเชื่อมต่อได้เฉพาะอุปกรณ์ที่ Controller สั่ง `PairPin` ระหว่างการเช่าที่ active
- จบการเช่า/หมดเวลา/boot: Agent สั่ง `POST /api/clients/unpair-all` เพื่อล้างอุปกรณ์ที่จับคู่ไว้ (ตัดสตรีมทันที) แล้วปิดเครื่องตามปกติ
- "พร้อม" ของเครื่องคิดจาก **liveness** ของสองพอร์ต: `base/serverinfo` (GameStream XML) และ `base+1/api/apps` — ตรวจทุก 5 วินาที (พลาด 3 ครั้งติดจึงออกจากชั้นวาง)

## สิ่งที่ต้องตั้งใน image (สำคัญ)

Sunshine ปล่อย request จาก `127.0.0.1` ผ่านโดยไม่ต้อง Basic auth — **ลูกค้าที่นั่งหน้าเครื่องจึงควบคุม Sunshine ผ่านเบราว์เซอร์ได้ถ้าไม่กัน**:

1. ตั้ง `origin_web_ui_allowed = pc`
2. ใส่ outbound firewall rule รายผู้ใช้ บล็อกพอร์ต `<sunshinePort>` (ค่าเริ่มต้น 47990) เฉพาะบัญชีที่ลูกค้าใช้ล็อกอิน — Agent รันเป็น LocalSystem จึงไม่โดนตัด
3. **ห้าม bind web UI ออกจาก loopback** (Agent จะเรียกไม่ได้)

สคริปต์ช่วยตั้ง: `scripts/agent/Set-SunshineImageHardening.ps1` (ถ้ามีในแพ็กเกจ) — รันบนเครื่องตอนทำ image และตรวจผลก่อน rollout

## งาน admin ที่ยังมี

- **Restart Sunshine**: ส่ง `RestartSunshine` ผ่าน PC Control; Agent เรียก `POST /api/restart` ของ Sunshine เอง รอจน `/serverinfo` ตอบ (≤15 วิ)
- **Unpair Sunshine**: ส่ง `UnpairSunshine`; Agent เรียก `unpair-all` และตรวจว่า `named_certs` ว่าง
- ทั้งสองคำสั่งเป็น machine-scoped และส่งถึง Agent ได้แม้เครื่อง `notReady` (กรณี Sunshine พังคือกรณีที่ต้องใช้)

## สิ่งที่ถูกถอดออก

- `KamenRaider Stream Guard` service + firewall block rule + named pipe + maintenance mode 45 วินาที
- `Enable-SunshineGui.ps1` (ไม่จำเป็นแล้ว: Sunshine GUI/WebUI เปิดได้ตลอด แต่ต้องป้องกันตามข้อ "สิ่งที่ต้องตั้งใน image")
- Fleet Sunshine credential ยังใช้ได้ถ้าตั้ง (Basic auth สำหรับ build ที่บังคับ) แต่ **ไม่บังคับ** สำหรับ loopback

## Troubleshooting

| อาการ | สาเหตุ/ทางแก้ |
|---|---|
| เครื่องไม่พร้อม แต่ Sunshine รันอยู่ | ตรวจ `/serverinfo` (base port) และ `/api/apps` (web port) จากเครื่องเอง; ดูค่า `SUNSHINE_*` ใน readiness diagnostic `streamFence`/`runtimeHealth` |
| Pair ไม่สำเร็จ | ต้องมี lease active + เครื่อง ready; PIN ต้องมีอุปกรณ์ใหม่ปรากฏใน `/api/clients/list` ภายใน 10 วิ (`PAIR_NOT_CONFIRMED` = นาฬิกาไม่เริ่ม) |
| จบการเช่าแล้วอุปกรณ์เก่ายังต่อได้ | ตรวจว่า Agent ออนไลน์และ `unpair-all` สำเร็จ; boot ครั้งถัดไปจะล้างซ้ำ |
| รีสตาร์ต Sunshine ไม่กลับมา | ดู service `SunshineService`, log Sunshine; ใช้ `RestartSunshine` อีกครั้งหรือตัดไฟตามขั้นตอนร้าน |
