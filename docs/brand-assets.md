# สินทรัพย์แบรนด์ของไคลเอนต์สตรีม (Brand assets)

อัปเดต: 2026-11-22

ไฟล์ที่ใช้ในเว็บ: `src/components/brands/streaming-logos.tsx` — เป็น inline SVG
React component ไม่มีไฟล์ภาพเพิ่มและไม่เพิ่ม dependency ใหม่

## Moonlight

- แหล่งที่มา: <https://raw.githubusercontent.com/moonlight-stream/moonlight-qt/master/app/res/moonlight.svg>
  (รีโป: <https://github.com/moonlight-stream/moonlight-qt>, ไฟล์ `app/res/moonlight.svg`)
- ต้นฉบับ: วงกลมสีเทา `rgb(86,92,100)` + วงกลมในสีขาว + สัญลักษณ์แปดแฉกสีเทา
- การดัดแปลงสำหรับเว็บ: เอากรอบวงกลมเทาออกเป็นวงแหวน `currentColor` และตัดวงกลมในสีขาว
  ออกเพื่อให้วางบนพื้นหลังใดก็ได้ (พื้นหลังโปร่งใส) โดยคงสัญลักษณ์แปดแฉกเดิมไว้
- Component: `MoonlightMark` (decorative โดยค่าเริ่มต้น, รับ `title` เพื่อใส่ชื่อสำหรับ screen reader)

## Parsec

- แหล่งที่มา: หน้าแรก <https://parsec.app/> — โลโก้ฝังเป็น inline SVG แบบ base64
  (path สีฟ้า `#26ABFF` + สีชมพู `#F50049`, canvas เดิม `viewBox="0 0 1824 441"`)
- การดัดแปลงสำหรับเว็บ: ตัดเฉพาะไอคอน (ประมาณ 290 หน่วยแรกของ canvas) ตั้ง
  `viewBox="0 0 290 441"` และตัด wordmark ออก; ใช้สีแบรนด์ตามต้นฉบับทุกค่า
- Component: `ParsecMark` (decorative โดยค่าเริ่มต้น, รับ `title` เพื่อใส่ชื่อสำหรับ screen reader)
- สีแบรนด์ในระบบธีม: `--parsec-blue: #26abff` และ `--parsec-pink: #f50049`
  (ประกาศใน `src/app/globals.css` ทั้ง `:root` และ `.dark`, map เป็น
  `--color-parsec-blue` / `--color-parsec-pink` ผ่าน `@theme inline`)
  ใช้กับปุ่ม variant `parsec` (gradient ฟ้า→ชมพู)

## ข้อกำหนดด้านเครื่องหมายการค้า (Trademark notice)

Moonlight เป็นโครงการโอเพนซอร์สของ Moonlight Game Streaming Project; Parsec เป็นผลิตภัณฑ์
ของ Parsec Cloud, Inc. โลโก้และเครื่องหมายการค้าทั้งหมดเป็นทรัพย์สินของเจ้าของแต่ละราย

- ใช้โลโก้เหล่านี้เพื่อ **อ้างอิงถึงซอฟต์แวร์/บริการของเจ้าของเท่านั้น** (nominative use)
  เช่น ปุ่ม "เชื่อมต่อด้วย Moonlight" หรือ "เชื่อมต่อด้วย Parsec"
- ห้ามนำไปใช้แสดงความเป็นพันธมิตร การรับรอง หรือการสนับสนุน (endorsement) จากเจ้าของแบรนด์
  และห้ามดัดแปลงเป็นโลโก้ของผลิตภัณฑ์นี้
- หากต้องใช้ในสื่อการตลาดภายนอก ควรตรวจสอบแนวทางแบรนด์ของเจ้าของอีกครั้ง
