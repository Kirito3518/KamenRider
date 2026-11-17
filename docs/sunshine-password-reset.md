# รีเซ็ตรหัส Sunshine บนเครื่องลูก (ไม่ต้องรู้รหัสปัจจุบัน)

หน้าเว็บ Sunshine (`https://localhost:47990`) บังคับกรอกรหัสปัจจุบันถ้าจะเปลี่ยนรหัส
ถ้าจำรหัสไม่ได้ ให้รีเซ็ตผ่าน **`Sunshine.exe --creds <user> <password>`** (คำสั่งเดียวกับที่ helper ใช้)
ซึ่งเขียน credential ใหม่ลง `C:\Program Files\Sunshine\config\sunshine_state.json` โดยไม่ต้องใช้รหัสเดิม
และ **รักษา pairing state (`root`) เดิมไว้**

> LAB / TEST ONLY · รหัสจะโผล่ใน command line ชั่วคราวระหว่างรัน — เปลี่ยนภายหลังได้

## A) รีเซ็ตด้วย CLI (แนะนำ)

รันบนเครื่องลูก — **Administrator**:

```powershell
# หยุด Sunshine ก่อน (Stream Guard ตั้งเป็น Manual อยู่แล้ว)
Stop-Service 'SunshineService' -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# ตั้ง username/password ใหม่ (แก้ค่าได้ตามต้องการ)
& 'C:\Program Files\Sunshine\Sunshine.exe' --creds kamenrider 'KamenLab123!'

# ตรวจว่า state ถูกเขียน (username ต้องเปลี่ยนตามที่สั่ง)
Get-Content 'C:\Program Files\Sunshine\config\sunshine_state.json' |
  ConvertFrom-Json | Select-Object username, salt, password

# เปิด Sunshine กลับ (Guard ต้องให้มี stream authority; ถ้า Start ไม่ได้ให้เปิดผ่าน Agent/Guard)
Start-Service 'SunshineService' -ErrorAction SilentlyContinue
Get-Service 'SunshineService' | Format-Table Name,Status,StartType
```

หลังรีเซ็ต:
1. เข้า `https://kamanriderplay.com/admin/settings/sunshine`
2. ตั้ง fleet credential เป็น **username/password เดียวกัน** (`kamenrider` / `KamenLab123!`) — หน้านี้ไม่ถามรหัสเดิม
3. ที่เครื่องลูก เคลียร์ Agent credential store แล้ว restart Agent เพื่อบังคับให้ดึง revision ใหม่:
   ```powershell
   Remove-Item 'C:\ProgramData\KamenRaider\Agent\sunshine' -Recurse -Force -ErrorAction SilentlyContinue
   Restart-Service 'KamenRaider Agent' -Force
   Start-Sleep -Seconds 15
   ```
4. แจ้งแอดมินเพื่อตรวจ `appliedSunshineCredentialRevision` ให้เป็น revision ล่าสุด

## B) ถ้า `--creds` ใช้ไม่ได้ (fallback: ล้าง state แล้วตั้งใหม่จากหน้าเว็บ localhost)

คำเตือน: จะเสียข้อมูล pairing clients เดิม

```powershell
Stop-Service 'SunshineService' -Force -ErrorAction SilentlyContinue
$state = 'C:\Program Files\Sunshine\config\sunshine_state.json'
Copy-Item $state "$state.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')" -Force
Remove-Item $state -Force
Start-Service 'SunshineService' -ErrorAction SilentlyContinue
```

- เปิด `https://localhost:47990` **บนเครื่องลูก** (loopback ไม่ถูก firewall block) แล้วตั้ง username/password ใหม่ผ่านหน้าเว็บ (รอบนี้จะไม่มีรหัสเดิมให้กรอก)
- จากนั้นกลับไปทำข้อ A.1–A.4

## ตรวจหลังรีเซ็ต
```powershell
Get-Service 'SunshineService'
Get-Content 'C:\Program Files\Sunshine\config\sunshine_state.json' |
  ConvertFrom-Json | Select-Object username
```
- username ต้องตรงกับที่ตั้ง
- จากนั้นทดสอบ PIN: เปิด Moonlight → add `192.168.1.25:47989` → PIN → `/my-machines` → โหลดช่องจับคู่ → ส่ง PIN

## หมายเหตุ
- Stream Guard ตั้ง Sunshine เป็น Manual/Stopped และ firewall block จนกว่าจะมี active lease — เป็นปกติ
- อย่าเปิดพอร์ต 47990/47989 ออกอินเทอร์เน็ต; ใช้เฉพาะ LAN/localhost
- ถ้า Agent ยังไม่ apply credential (revision ค้าง 0) ให้ดู `Get-WinEvent -ProviderName Agent -MaxEvents 20` เพื่อดู warning (ACL/DPAPI)
