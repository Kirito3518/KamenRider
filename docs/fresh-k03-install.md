# ติดตั้ง K03 ใหม่หลังลง Windows (fresh install)

คู่มือนี้เริ่มจาก Windows ใหม่เปล่า ๆ จนถึงเครื่องพร้อมให้ลูกค้าเช่าและกดเปิดเครื่องได้

## 0) สิ่งที่ต้องมีก่อนเริ่ม

- K03 ต่อ LAN เดียวกับเครื่องแม่ และเข้าถึง `https://192.168.1.100:7443` ได้
- K03 ออกอินเทอร์เน็ตได้ (ใช้ดาวน์โหลด Agent package + Sunshine)
- ตั้งเวลา Windows ให้ตรง (Settings → Time & language → Sync now)
- เปิด PowerShell **ปกติ** (ไม่ต้อง Run as Administrator) — ตัวติดตั้งจะขอ UAC เอง
- ถ้า Windows ยังไม่มี `winget` ให้ติดตั้ง **App Installer** จาก Microsoft Store ก่อน

## 1) เปิด Wake-on-LAN ก่อน (สำคัญ)

หลังลง Windows ใหม่ ค่า WOL จะกลับเป็นปิด ต้องเปิดก่อน ถ้าไม่เปิด ปุ่ม Power ในเว็บจะสั่งไม่สำเร็จ

Run as Administrator:

```powershell
# ดูชื่อ adapter จริงก่อน (ไม่ต้องเปลี่ยนชื่อ)
Get-NetAdapter -Physical | Format-Table Name,InterfaceDescription,MacAddress,Status

# เลือก adapter LAN ที่ Status = Up อัตโนมัติ
$adapter = Get-NetAdapter -Physical |
  Where-Object { $_.Status -eq 'Up' -and $_.MacAddress } |
  Select-Object -First 1
$adapter.Name

# ดูว่ามี property อะไรให้เปิด
Get-NetAdapterAdvancedProperty -Name $adapter.Name |
  Where-Object { $_.DisplayName -match 'Wake|Magic|Shutdown' } |
  Format-Table DisplayName,DisplayValue

# เปิด WOL (ถ้า driver มี property เหล่านี้)
Set-NetAdapterAdvancedProperty -Name $adapter.Name `
  -DisplayName "Wake on Magic Packet" -DisplayValue "Enabled"
Set-NetAdapterAdvancedProperty -Name $adapter.Name `
  -DisplayName "Shutdown Wake-On-Lan" -DisplayValue "Enabled"

# ปิด Fast Startup
powercfg /h off
```

ใน Device Manager → Network adapters → adapter LAN → Properties → Power Management:
- ติ๊ก `Allow this device to wake the computer`
- ติ๊ก `Only allow a magic packet to wake the computer`

ใน BIOS/UEFI:
- เปิด **Wake on LAN / Power On by PCI-E**
- ปิด **ErP / Deep Sleep**
- ถ้ามี **Restore on AC Power Loss** ตั้งเป็น `Power On`

หลังปิดเครื่อง ต้องมีไฟ standby ที่ NIC (LED ที่พอร์ต LAN ต้องติด) และห้ามปิดสวิตช์ไฟ/ปลั๊ก

## 2) ติดตั้ง Agent (one-liner)

1. เข้า `https://kamanriderplay.com/admin/machines`
2. กด **ติดตั้ง Agent** → **ขอคำสั่งติดตั้ง**
3. คัดลอก one-liner แล้วรันบน K03 ใน PowerShell ปกติ

รูปแบบคำสั่ง:

```powershell
irm "https://kamanriderplay.com/api/agent/install.ps1?k=<TOKEN>" | iex
```

ตัวติดตั้งจะทำให้:
- ขอ UAC ครั้งเดียว
- ติดตั้ง Sunshine ผ่าน winget ถ้ายังไม่มี
- ดาวน์โหลด + import Controller certificate
- ตรวจ SHA-256 ของ package
- ติดตั้ง Stream Guard, Agent, TightVNC
- enroll กับ Controller

> token ใช้ได้ครั้งเดียวและมีอายุ ~24 ชั่วโมง

## 3) อนุมัติเครื่องที่หน้า Admin

หลังติดตั้ง กลับไปที่ `https://kamanriderplay.com/admin/machines`:

1. เครื่องใหม่จะขึ้นเป็น **pending**
2. กดตั้งค่า/อนุมัติ ใส่ fingerprint จาก `C:\ProgramData\KamenRaider\Agent\enrollment-receipt.json`
3. ตั้งค่า:
   - ชื่อ: `K03`
   - ราคา: `25` บาท/ชั่วโมง (หรือตามที่ต้องการ)
   - Moonlight port: **10300**
   - Sunshine port: **10301**
   - VNC LAN IP: IP ของ K03 เช่น `192.168.1.103`
   - สถานะการให้เช่า: **เปิดให้เช่า**
4. กดอนุมัติ

> เครื่อง K03 ชุดเก่าถูกตั้งเป็น `disabled` ไว้แล้ว ไม่ต้องลบ

## 4) ตั้ง Sunshine ให้ตรงกับ Controller

ติดตั้ง/ตั้งค่า Sunshine บน K03:
- GameStream/Moonlight port = **10300**
- Sunshine management port = **10301**
- username/password ให้ตรงกับที่ตั้งใน `https://kamanriderplay.com/admin/settings/sunshine`

ตรวจว่า Sunshine ฟังพอร์ตถูก:

```powershell
Get-NetTCPConnection -State Listen |
  Where-Object LocalPort -in 10300,10301 |
  Select-Object LocalPort,OwningProcess
```

## 5) เปิด lab mode สำหรับทดสอบ lifecycle (ถ้ายังไม่ qualify readiness จริง)

ค่าเริ่มต้น Agent จะยัง `notReady` เพราะยังไม่มีหลักฐาน boot/restore/desktop health ครบ ทำให้หลัง WOL เครื่องอาจไม่ activate lease

สำหรับทดสอบเท่านั้น (ห้ามใช้กับเครื่องที่ปล่อยเช่าจริง):

```powershell
New-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\KamenRaider Agent' `
  -Name Environment -PropertyType MultiString `
  -Value 'KAMENRAIDER_AGENT_LAB_READY=1' -Force
Restart-Service 'KamenRaider Agent' -Force
Start-Sleep -Seconds 10
Get-Service 'KamenRaider Agent','KamenRaider Stream Guard','SunshineService' |
  Format-Table Name,Status
```

## 6) ทดสอบ flow

1. Login ลูกค้าที่มีเงิน → `/rent` → เห็น K03
2. เช่า → ไป `/my-machines` → กดปุ่ม **Power**
3. K03 ต้องตื่นภายใน ~30–60 วินาที
4. Controller ต้องเปลี่ยนเป็น `active`; หน้า `/my-machines` แสดงเวลานับถอยหลัง
5. ทดสอบเชื่อม Moonlight ด้วย `IP:10300` และ PIN
6. ทดสอบยกเลิก → ต้องเห็น clearing 30 วิ → K03 ถูกสั่งปิด และกลับมาเช่าได้

## 7) ตรวจสอบเมื่อมีปัญหา

```powershell
Get-Service 'KamenRaider Agent','KamenRaider Stream Guard','SunshineService'
Get-NetTCPConnection -State Listen | Where-Object LocalPort -in 10300,10301,7443
Get-WinEvent -ProviderName 'Agent' -MaxEvents 20 |
  Select-Object TimeCreated,Id,LevelDisplayName,Message | Format-List
Get-Content 'C:\ProgramData\KamenRaider\Agent\enrollment-receipt.json'
```

ถ้า WOL ไม่ตื่น:
- ตรวจ NIC property `Wake on Magic Packet` = Enabled
- ตรวจ BIOS Wake on LAN / ErP
- ตรวจว่าไฟ LED ที่ NIC ติดตอนเครื่องปิด
- ตรวจว่า MAC ที่ Controller เห็นตรงกับ LAN adapter จริง
