# เปิด/ใช้งาน Sunshine GUI บนเครื่องลูก (โมเดลใหม่ — ไม่มี Stream Guard)

> เอกสารนี้แทน `sunshine-gui-maintenance.md` เดิม ตั้งแต่ 2026-11-20 Stream Guard ถูกถอดออกแล้ว

Sunshine รันตลอดเวลาและ Agent ไม่หยุด/บล็อก Sunshine อีกต่อไป — เปิด Sunshine GUI/WebUI ได้ตามปกติ:

- บนเครื่องลูกเอง: `https://localhost:47990` (หรือพอร์ต Sunshine web UI ที่ตั้งไว้)
- จากเครื่องอื่นใน LAN: ต้องอนุญาต firewall เองเป็นการชั่วคราว และ **ห้ามเปิดพอร์ตนี้ออกอินเทอร์เน็ต**

สิ่งที่ต้องระวัง: loopback ของ Sunshine ไม่ต้องใช้รหัส therefore **อย่าให้บัญชีลูกค้าเข้าถึงพอร์ต web UI ได้** — ตั้ง `origin_web_ui_allowed = pc` และ outbound firewall block พอร์ต Sunshine web UI เฉพาะบัญชีลูกค้าใน image (ดู [sunshine-local-control.md](sunshine-local-control.md))

งานแอดมินที่เคยใช้ maintenance mode:

- รีสตาร์ต Sunshine → ใช้ปุ่ม **Restart Sunshine** ใน PC Control (คำสั่ง `RestartSunshine`)
- ล้างอุปกรณ์ที่จับคู่ไว้ → ใช้ปุ่ม **Unpair Sunshine** (คำสั่ง `UnpairSunshine`)
- เปิด GUI เพื่อตั้งค่า → ทำได้ทันทีเมื่อเครื่องไม่ได้อยู่ระหว่างให้เช่า
