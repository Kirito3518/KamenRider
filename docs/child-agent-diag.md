# ตรวจวินิจฉัย: Agent service start ไม่ขึ้น (เครื่องลูก)

ใช้เมื่อติดตั้งผ่าน one-liner แล้วขึ้นข้อความประมาณ:

```
Start-Service : Service 'KamenRaider Agent (KamenRaider Agent)' cannot be started ...
```

> **อย่าเพิ่งรัน cleanup / อย่าลบ** `C:\ProgramData\KamenRaider` ก่อนเก็บผลตรวจ เพราะ log/หลักฐานอาจอยู่ที่นั่น

## วิธีรัน

1. เปิด **PowerShell แบบ Administrator** บนเครื่องลูก
2. พิมพ์คำสั่งด้านล่าง (ทั้งหมดเป็นบล็อกเดียว)
3. แคปหน้าจอผลลัพธ์ส่งกลับมาให้แอดมิน (หรือพิมพ์เฉพาะบรรทัด error)

```powershell
"=== Services ==="
Get-CimInstance Win32_Service -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -match 'KamenRaider' } |
  Select-Object Name, State, StartMode, StartName, PathName |
  Format-List

"=== Try start Agent ==="
sc.exe start "KamenRaider Agent"
"LASTEXITCODE=$LASTEXITCODE"

"=== System log (Service Control Manager) ==="
Get-WinEvent -LogName System -MaxEvents 400 -ErrorAction SilentlyContinue |
  Where-Object { $_.ProviderName -eq 'Service Control Manager' -and $_.Message -match 'KamenRaider|Sunshine' } |
  Select-Object -First 8 TimeCreated, Id, LevelDisplayName, Message |
  Format-List

"=== Application log (Agent) ==="
Get-WinEvent -LogName Application -MaxEvents 400 -ErrorAction SilentlyContinue |
  Where-Object { $_.ProviderName -match 'Agent|KamenRaider' -or $_.Message -match 'KamenRaider' } |
  Select-Object -First 8 TimeCreated, ProviderName, Id, LevelDisplayName, Message |
  Format-List

"=== Files ==="
Get-ChildItem "C:\Program Files\KamenRaider" -Recurse -ErrorAction SilentlyContinue |
  Select-Object -First 40 FullName | Format-Table -AutoSize
Get-ChildItem "C:\ProgramData\KamenRaider" -Recurse -ErrorAction SilentlyContinue |
  Select-Object -First 40 FullName | Format-Table -AutoSize

"=== Agent data files (ถ้ามี) ==="
Get-ChildItem "C:\ProgramData\KamenRaider\Agent" -Force -ErrorAction SilentlyContinue |
  Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
if (Test-Path "C:\ProgramData\KamenRaider\Agent\enrollment-receipt.json") {
  Get-Content "C:\ProgramData\KamenRaider\Agent\enrollment-receipt.json"
}
```

## เพิ่มเติม: Sunshine layout + .NET Runtime (สำหรับ error นี้)

`KamenRaider Stream Guard` รันด้วย `Agent.exe --stream-guard` และต้องหา Sunshine ให้เจอในรูปแบบที่รองรับ
ถ้า Sunshine ติดตั้งแบบ portable/คนละ layout จะทำให้ Guard crash → SCM timeout 7009/7000 → Agent start ไม่ได้ (7001)

รันเพิ่มบนเครื่องลูก (Administrator) แล้วแคปผล:

```powershell
"=== Sunshine service ==="
Get-CimInstance Win32_Service -Filter "Name='SunshineService'" -ErrorAction SilentlyContinue |
  Select-Object Name, State, StartMode, StartName, PathName | Format-List

"=== Sunshine layout ==="
Get-ChildItem "C:\Program Files\Sunshine" -Force -ErrorAction SilentlyContinue |
  Select-Object Name, Length | Format-Table -AutoSize
Get-ChildItem "C:\Program Files\Sunshine\tools" -Force -ErrorAction SilentlyContinue |
  Select-Object Name, Length | Format-Table -AutoSize

"=== .NET Runtime crash (stack) ==="
Get-WinEvent -LogName Application -MaxEvents 600 -ErrorAction SilentlyContinue |
  Where-Object { $_.ProviderName -eq '.NET Runtime' -or $_.ProviderName -eq 'Application Error' } |
  Select-Object -First 6 TimeCreated, ProviderName, Id, LevelDisplayName, Message |
  Format-List
```

สิ่งที่ต้องดู:
- `PathName` ของ `SunshineService` ต้องเป็น `...\tools\sunshinesvc.exe` (มี `sunshine.exe` อยู่ระดับบน)
- ถ้าเป็น `...\sunshine.exe --service` หรืออยู่ในโฟลเดอร์อื่น = layout ไม่ตรง แล้วแต่ต้องรอแพ็กเกจแก้/ติดตั้ง Sunshine ใหม่ให้ถูก layout
- `.NET Runtime` event จะบอก exception จริงว่า Guard crash เพราะอะไร (ข้อความต่อจากนี้)

## ถ้าต้องการเก็บเป็นไฟล์ก่อนส่ง

```powershell
$log = "C:\ProgramData\KamenRaider\diag.txt"
Start-Transcript -Path $log -Force | Out-Null
# ... รันบล็อกด้านบน ...
Stop-Transcript
notepad $log
```

## การอ่านผลเบื้องต้น

| สิ่งที่เห็น | ความหมาย |
|---|---|
| `sc.exe start` error **1067** | Agent.exe crash ตอนเริ่ม — ดู Application log source `Agent` |
| error **1068** | dependency ไม่ขึ้น (มักเป็น `KamenRaider Stream Guard`) — ดูสถานะ Guard |
| error **5** | access denied — สิทธิ์ service account / ACL ของ Program Files/ProgramData |
| error **1053** | service ไม่ตอบภายในเวลา — process ค้าง/รอ dependency |
| Application log มี `Agent` + exception | สาเหตุจริงของ crash (เช่น config ไม่ผ่าน, data dir, certificate) |

## หมายเหตุ
- หลังเก็บผลแล้วค่อยล้างด้วย:
  ```powershell
  irm "https://kamanriderplay.com/agent/Cleanup-AgentInstall.ps1" | iex
  ```
- จากนั้นขอ one-liner ใหม่จาก `/admin/machines` แล้วลองอีกครั้ง
