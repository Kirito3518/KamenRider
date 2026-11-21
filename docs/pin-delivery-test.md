# ทดสอบ PIN delivery: กดส่ง PIN จากเว็บถึง Agent โดยยังไม่มี Moonlight/port forwarding

อัปเดต: 18 พ.ย. 2026 · LAB ONLY

## เป้าหมาย

พิสูจน์ว่า **เมื่อลูกค้ากดส่ง PIN 4 หลักในเว็บ คำสั่ง `PairPin` ถูกส่งไปถึง Agent จริง**
โดยยังไม่ต้องรอผู้ให้บริการอินเทอร์เน็ตเปิด port forwarding และไม่ต้องมี Moonlight จริง

## สถานะที่เตรียมไว้

แอดมิน seed **pairing slot ปลอม** ใน Controller (lab only):

- lease: `090c0de9b534472d81034462a63f0ff9` (active, revision 1)
- pairingId: `foundation-sunshine-single-pending-slot`
- valid: ~1 ชั่วโมงจากเวลาที่ seed
- หมายเหตุ: ถ้า Agent sync สำเร็จ มันจะลบ offer นี้ (เพราะไม่มี Moonlight pending จริง) — ให้รีบกดส่ง PIN หรือแจ้งแอดมิน seed ใหม่

## ขั้นตอนฝั่งเว็บ (ลูกค้า)

1. เปิด/refresh `https://kamanriderplay.com/my-machines` (บัญชี `dung7732xv@gmail.com`)
2. กดปุ่ม **Moonlight** ในการ์ดเครื่อง (หน้านี้แสดงเฉพาะเครื่องที่กำลังเช่า)
   - dialog แสดง address + ปุ่มคัดลอก และตรวจหาช่องจับคู่ให้อัตโนมัติ
   - ควรเห็นช่อง **Foundation Sunshine** (จาก offer ที่ seed ไว้)
3. กรอก PIN 4 หลัก (อะไรก็ได้ เช่น `1234`) → กด **ยืนยัน PIN**
4. ผลที่ต้องได้: dialog แสดงสถานะเริ่มต้นเป็น **`queued`** (ไม่ใช่ `Error: 503: Controller unavailable`)

> ถ้าไม่มีช่องให้เลือก หรือได้ 503 อีก → แจ้งแอดมิน (offer อาจหมดอายุ/ถูกลบตอน Agent sync)

## ขั้นตอนฝั่งเครื่องลูก (Administrator)

หลังกดส่ง PIN แล้ว ให้เปิด Sunshine และ restart Agent เพื่อให้ Agent มารับคำสั่ง:

```powershell
Start-Service 'SunshineService' -ErrorAction SilentlyContinue
Restart-Service 'KamenRaider Agent' -Force
Start-Sleep -Seconds 15
Get-Service 'KamenRaider Agent','KamenRaider Stream Guard','SunshineService' |
  Format-Table Name, Status, StartType
```

## วิธีอ่านผล (แอดมินตรวจ)

แอดมินจะดูใน Controller DB ว่า command `PairPin` เปลี่ยนสถานะจาก `pending`:

- **`failed` (PairingUnavailable)** = คำสั่งถึง Agent จริง แต่ Sunshine ปฏิเสธเพราะไม่มี Moonlight pending → **ผ่านการทดสอบ delivery**
- **`applied`** = Sunshine pairing สำเร็จ (แปลว่ามี Moonlight pending อยู่จริง)
- **`pending` ค้าง** = Agent ยังไม่ได้รับคำสั่ง (มักเพราะ sync ยังล้ม/Sunshine ไม่รัน) — ต้องแก้ sync ก่อน

## หมายเหตุ

- การทดสอบนี้ไม่ต้องเปิด port forwarding และไม่ต้องมี Moonlight
- ถ้าต้องการทดสอบ pairing จริง (ได้ stream) ต้องมี client ที่ต่อ `192.168.1.25:47989` ได้ (LAN เดียวกัน หรือ port forwarding/Tailscale)
- `Error: 503: Controller unavailable` ที่เคยเห็น มาจากฝั่ง web/Controller ช่วงที่ lease/สถานะเปลี่ยน หรือตอนที่ยังไม่มี offer; การ seed offer ใหม่จะทำให้กดส่งได้
- อย่า restart Controller ระหว่างทดสอบ (DB จะเข้า quarantine)
