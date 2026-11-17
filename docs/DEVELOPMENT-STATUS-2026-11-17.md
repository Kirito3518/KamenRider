# รายงานสถานะการพัฒนาต่อเนื่อง — Sunshine Credential/GUI

วันที่จัดทำ: 2026-11-17 (Asia/Bangkok)

เอกสารก่อนหน้า: [DEVELOPMENT-HANDOFF-2026-11-16.md](DEVELOPMENT-HANDOFF-2026-11-16.md) — อ่านเพื่อรักษาโทนและรูปแบบการรายงาน แต่ไม่ได้แก้ไขไฟล์นั้น

งาน Sunshine credential/GUI ที่ handoff 2026-11-16 ระบุว่าถูก pause และยังค้าง ณ วันนี้ได้ทำเสร็จและพิสูจน์บน host นี้แล้ว รายงานนี้เพิ่มไฟล์นี้เพียงไฟล์เดียว ไม่ได้แก้โค้ด/ไฟล์อื่น รัน build, test, service หรือ commit ระหว่างจัดทำรายงาน หลักฐานทั้งหมดด้านล่างคือผลที่ตรวจสดบน host นี้เมื่อ 2026-11-17

การอ่านหลักฐาน: **ตรวจสด** = ผลที่พิสูจน์บน host นี้ในรอบงานนี้; **Pending** = ยังไม่มีหลักฐานจริงและห้ามถือว่าผ่าน; ไม่มีรายการใดถูกเปลี่ยนจาก Pending เป็นผ่านโดยไม่มีหลักฐาน

## 1. บทสรุปสถานะ

งานที่ค้างถูกปิดครบทั้งสามชั้น: credential ถูกตั้งใหม่ให้ตรงกันระหว่าง Sunshine กับ Agent DPAPI store, TLS certificate ที่ Windows validate ได้ถูก provision และ import trust, และ probe ที่ผ่าน fence ได้ HTTP 200 พร้อม TLS validation จริง ส่วน GUI เปิดได้จริงใน setup window ที่ Stream Guard เปิดชั่วคราว — ข้อจำกัดสำคัญ: HTTP 200 บน loopback ของ build นี้ไม่ใช่หลักฐานค่า credential เพราะ Foundation Sunshine อนุญาต origin `net::PC` อัตโนมัติก่อนตรวจ Basic Authorization header (ดูหัวข้อ 4.3) การพิสูจน์ credential จึงมาจาก state hash และ Agent DPAPI round-trip

| รายการที่ค้างใน handoff 2026-11-16 | สถานะ 2026-11-17 |
| --- | --- |
| Sunshine credential helper/wrapper ยังไม่มี/ไม่เคยรันจริง | **ตรวจสด** — มี `Program.cs` และ wrapper, รัน `--apply` สำเร็จบน host; alignment ตรวจจาก hash + DPAPI round-trip |
| Authenticated HTTPS ไม่ถึง HTTP response | **ตรวจสด** — fenced probe `RequestCompleted`, `HTTP 200`, `TlsValidated=true`; พิสูจน์ TLS chain/hostname, API reachability และ fence แต่ HTTP 200 บน loopback ไม่พิสูจน์ credential (หัวข้อ 4.3) |
| Credential alignment ระหว่าง Sunshine กับ Agent DPAPI | **ตรวจสด** — state hash ตรงสูตร Foundation Sunshine และ DPAPI store round-trip คืน username/plaintext เดิม |
| GUI เปิดไม่ได้ | **ตรวจสด** — setup window เปิด `https://localhost:47990` ได้ HTTP 200 และ dashboard |
| Setup-window WIP ยังไม่พิสูจน์ GUI | **ตรวจสด** — ใช้ `-OpenSetupUi` ผ่านพร้อม fenced cleanup |
| Remote 401-vs-200 authentication (non-loopback) | **Pending** — ต้องรอ LAN/remote phase เพราะ firewall fence บล็อก client ภายนอกโดยเจตนา |
| Moonlight pairing/stream | **Pending** — คาดว่าต้อง re-pair เพราะ certificate เปลี่ยน |
| Browser → real VNC E2E | **Pending** |
| WOL/shutdown ของเครื่องจริง | **Pending** |
| Clean install บนเครื่องที่สอง | **Pending** |

ไม่มี commit ใด ๆ ระหว่างงานนี้ ไฟล์ใหม่และไฟล์ที่แก้ยังอยู่ใน working tree

## 2. สาเหตุราก (Root Causes)

| อาการที่พบ | สาเหตุราก | วิธีปิดสาเหตุ |
| --- | --- | --- |
| probe authenticated ล้มด้วย `CredentialsUnavailable` | Agent Sunshine DPAPI credential store `C:\ProgramData\KamenRaider\Agent\sunshine\management-credentials.dpapi` ไม่มีอยู่จริง | helper สร้างและเขียน store ด้วย `WindowsSunshineCredentialStore` แล้วโหลดกลับมาตรวจ |
| TLS validation ของ `https://localhost:47990` ล้ม | certificate ที่ติดตั้งอยู่ `C:\Program Files\Sunshine\config\credentials\cacert.pem` เป็น self-signed `CN=Sunshine Gamestream Host` ไม่มี subjectAltName และไม่ถูก trust ใน Windows root store ใดเลย (ออกแบบให้ห้าม bypass) | generate certificate ใหม่ `CN=localhost` พร้อม SAN และ import trust ให้ Windows validate ได้ |
| probe ที่เพิ่งเปิด fence ล้มด้วย transport error | Sunshine build ที่ติดตั้งรายงาน Windows service เป็น Running ก่อน HTTPS Web manager listener จะ bind เสร็จ probe จึงแข่งกับ startup | เพิ่ม bounded readiness retry ฝั่ง probe และรอ listener ฝั่ง setup-window ก่อนเขียน ready marker |

## 3. โค้ดที่เพิ่มและแก้ไข

โค้ดทั้งหมด compile ผ่าน และ helper กับ wrapper ถูก run live บน host นี้แล้ว

| ไฟล์ | ประเภท | สิ่งที่ทำ |
| --- | --- | --- |
| `scripts/agent/dev/SunshineCredentialReset/Program.cs` | ใหม่ | operator tool โหมด `--provision-tls` และ `--apply`: backup state/certificate/key/Agent store, generate self-signed RSA-2048 `CN=localhost` พร้อม SAN `DNS:localhost`, `IP 127.0.0.1`, `IP ::1`, เขียน PEM certificate + PKCS#8 private key ลง credentials directory ของ Sunshine, import trust ลง LocalMachine Root และ CurrentUser Root, รัน `Sunshine.exe --creds <user> <password>`, ตรวจ hash ใน `sunshine_state.json` (SHA-256 ของ password+salt ตาม Foundation Sunshine `util::hex()` ที่ emit digest แบบ reverse byte order และ uppercase hex — สูตรเดียวกับ `confighttp::authenticate()` / `savePassword()`), ตรวจว่า pairing node `root` เหมือนเดิมทุก byte, แล้วเขียน plaintext เดียวกันลง Agent DPAPI machine-scope store ผ่าน `WindowsSunshineCredentialStore` และ reload มาตรวจซ้ำ ทุก mutation มี rollback step |
| `scripts/agent/dev/Reset-SunshineCredentials.ps1` | ใหม่ | elevated wrapper: gate ว่า Stream Guard Running, Agent Running, Sunshine Stopped, managed firewall block อยู่ และ port 47990 ปิด, build helper ลง staging directory สำหรับ administrator เท่านั้นที่ `C:\ProgramData\KamenRaider\SunshineSetup`, รัน `--apply`, แล้ว re-verify อย่างอิสระทั้ง state username, root store ทั้งสอง, Agent store, credential note, services และ network ที่ปิดอยู่ พร้อมเขียน JSON results |
| `services/Agent/Sunshine/AuthenticatedSunshineApiProbe.cs` | แก้ไข | เพิ่ม optional bounded readiness retry เฉพาะ connection-level failure (`HttpsRequestFailed`) เท่านั้น; ผล TLS, HTTP status และ malformed response ไม่ถูก retry |
| `scripts/agent/dev/StreamGuardFencedApiProbe/Program.cs` | แก้ไข | ส่ง readiness budget 25 วินาทีเข้า probe (อยู่ภายใน 45-second maintenance deadline ของ Stream Guard) และโหมด setup window รอ listener ของ port 47990 ก่อนเขียน `setup-ready.json` |
| `services/Agent.Tests/AuthenticatedSunshineApiProbeTests.cs` | แก้ไข | เพิ่ม 3 tests (retry แล้วสำเร็จ; TLS failure ไม่ retry; readiness budget หมด) — probe test class ผ่าน 8/8; mock suites `Test-SunshineLocalApiPreflight.ps1` และ `Test-SunshineLocalApiLifecycle.ps1` ผ่านทั้งคู่ |

rollback ถูกพิสูจน์แล้ว: dry run ครั้งแรกล้มโดยเจตนาที่ขั้น hash verification และ rollback คืน state เดิมพร้อมถอน trust entry ออกจริง ซึ่งตรวจยืนยันผลได้

การรันซ้ำตรวจแล้วว่า idempotent: รัน `--provision-tls` อีกครั้งได้ `TlsAction=Reused` พร้อม thumbprint เดิม และทั้งสอง root store trust certificate เดิมอยู่แล้ว (ไม่ได้ generate ใหม่หรือเพิ่ม trust ซ้ำ)

ผล test suite ทั้งหมดที่รันในรอบนี้: `dotnet test services\Agent.Tests` ผ่าน 363, skipped 1, failed 0; probe test class ผ่าน 8/8; PowerShell mock suites ของ preflight และ lifecycle ผ่านทั้งคู่

## 4. หลักฐานสดบน host นี้ (ตรวจสด 2026-11-17)

### 4.1 Helper apply

| Field | ผล |
| --- | --- |
| Status | `Succeeded` |
| TlsAction | `Provisioned` |
| TlsThumbprint (certificate ใหม่) | `6052B84DE97D2B7CEB14BACCE2425341C2ED06F6` |
| CredentialStateVerification | `SunshineStateHashMatches` |
| PairingStatePreserved | `true` |
| AgentStoreVerified | `true` |
| Sunshine command exit code | `0` |

แถว `CredentialStateVerification` และ `AgentStoreVerified` คือหลักฐาน alignment ของ credential ตามหัวข้อ 4.3 ไม่ใช่ HTTP status

### 4.2 การตรวจยืนยันอิสระหลัง apply

| รายการ | ผล |
| --- | --- |
| `sunshine_state.json` username | `admin` |
| `root.uniqueid` | ไม่เปลี่ยนจากเดิม |
| `named_devices` | คงอยู่ 8 รายการ |
| Certificate SAN | `localhost`, `127.0.0.1`, `::1`; valid ถึงปี 2036 |
| Trust | LocalMachine Root และ CurrentUser Root |
| Agent store ACL | protected ACL ประกอบด้วย NT AUTHORITY\SYSTEM, BUILTIN\Administrators, NT SERVICE\KamenRaider Agent แบบ full control; owner BUILTIN\Administrators |
| Credential note ACL | protected |
| Services และ network | Sunshine Stopped, Stream Guard และ Agent Running, managed block rule อยู่, port 47990 ปิด |
| Backups | คงอยู่ทั้งสองชุด (ห้ามลบ) |

### 4.3 Fenced authenticated probe และข้อจำกัดของ loopback authorization

`Test-SunshineLocalApi.ps1` ให้ผล:

| Field | ผล |
| --- | --- |
| Status | `RequestCompleted` |
| ApiStatus | `HTTP 200` |
| TlsValidated | `true` |
| Cleanup | `FencedCleanupCompleted;StagingRemoved` |
| Final state | Sunshine Stopped, Stream Guard Running, firewall block ครบ, port ปิด, Agent ถูก restart กลับมา Running |

**ข้อจำกัดของ HTTP 200 บน loopback:** ใน Foundation Sunshine รุ่นที่ติดตั้ง (ตรวจใน fork source `src/confighttp/auth.cpp`) ฟังก์ชัน `confighttp::authenticate()` คืน `true` ทันทีสำหรับ origin `net::PC` (loopback) ก่อนตรวจ Basic Authorization header ดังนั้น HTTP 200 ของ fenced probe พิสูจน์ **TLS chain/hostname validation, API reachability และ closed-state/fence contract** แต่บน build นี้ **ไม่พิสูจน์ค่า credential**. มี negative control แบบสด: request ด้วย password ที่ตั้งใจให้ผิดก็ได้ HTTP 200 บน loopback เช่นกัน ซึ่งสอดคล้องกับ bypass นี้

Credential alignment จึงพิสูจน์จาก (a) หลัง `Sunshine.exe --creds` ค่า username/salt/hash ใน `sunshine_state.json` ตรงกับสูตร Foundation Sunshine ที่ recompute (SHA-256 ของ password+salt, emit ด้วย `util::hex()` แบบ reverse byte order และ uppercase hex — สูตรเดียวกับที่ `confighttp::authenticate()` / `savePassword()` ใช้) และ (b) Agent DPAPI store round-trip (`Save` แล้ว `Load`) คืน username และ plaintext เดิมทุกตัวอักษร

การตรวจ authentication แบบ 401-vs-200 จริงต้องใช้ client ที่ไม่ใช่ loopback ซึ่ง Stream Guard firewall fence บล็อกโดยเจตนา จึงยัง **Pending** และอยู่ใน LAN/remote phase

### 4.4 GUI proof

- รัน `Test-SunshineLocalApi.ps1 -OpenSetupUi` แล้ว setup window เปิดจริง
- `https://localhost:47990/` คืน HTTP 200 พร้อม `<title>Sunshine</title>` จาก normal user session ด้วย certificate ที่ trust แล้ว (HTTP 200 บน loopback มี loopback authorization ตามหัวข้อ 4.3; หลักฐานนี้ยืนยันว่า GUI เปิดและโหลด dashboard ได้ ไม่ได้ใช้พิสูจน์ credential)
- headless screenshot แสดง Foundation Sunshine dashboard เต็มหน้าจอที่ `.next-local/sunshine-gui.png`
- window จบเองตามดีไซน์เมื่อ 44-second helper budget หมด จึงรายงาน `SetupWindowTimedOut` พร้อม fenced cleanup ที่เสร็จและ final state ที่ตรวจยืนยันแล้ว — ไม่ใช่ความล้มเหลวของ GUI

### 4.5 Secret handling และ backups

- รหัสผ่านไม่ถูกเก็บใน repository, chat หรือ log ใด ๆ
- เขียนครั้งเดียวลง `C:\Users\User\sunshine-web-credentials.txt` (ACL แบบ protected จำกัดเฉพาะผู้ใช้ปัจจุบัน) โดยใช้ username `admin`
- backups ก่อนเปลี่ยนยังอยู่ที่ `C:\Program Files\Sunshine\config\setup-backup-20261116T173226Z` (state + certificate + key) และ backup จากรอบที่ล้มก่อนหน้า `setup-backup-25691116T172017Z` — ห้ามลบทั้งสอง

## 5. Decisions & Side Effects

1. Sunshine management certificate ถูกเปลี่ยน แม้ pairing state (root + named devices 8 รายการ) จะไม่ถูกแตะ แต่ Moonlight clients pin certificate ของ host เดิมไว้ ดังนั้นการทดสอบ Moonlight pairing จึงคาดว่าจะต้อง re-pair
2. GUI ของ Sunshine เข้าถึงได้เฉพาะขณะที่ fenced maintenance window เปิด เพราะ Stream Guard หยุด Sunshine ทุกครั้งที่ไม่มี stream authority — เป็น behavior ที่ออกแบบไว้ และ `-OpenSetupUi` คือวิธีที่รองรับในการขอ window
3. ไม่มี commit; ไฟล์ใหม่และที่แก้ทั้งหมดยังอยู่ใน working tree
4. งาน WIP ฝั่ง AgentMaintenance/Readiness และ untracked directories `obj-apply-isolated/` ถูกปล่อยไว้ไม่แตะต้อง
5. ไม่มีการลดความปลอดภัยเพื่อให้ผ่าน: ไม่มี HTTP downgrade และไม่ bypass certificate validation โดยดีไซน์

## 6. งานที่ยังเหลือ

เรียงตามลำดับความสำคัญ:

| ลำดับ | งาน | สถานะ / ข้อกำหนด |
| --- | --- | --- |
| (a) | ทดสอบ Moonlight pairing และ stream จริงด้วย certificate ใหม่ | **Pending** — ต้องยืนยันว่า re-pair แล้ว stream ได้; certificate เปลี่ยนอาจกระทบ client เดิม |
| (b) | ทดสอบ browser → real VNC แบบ end-to-end | **Pending** — ต้องเป็นเส้นทางจริง ไม่ใช่ synthetic frame |
| (c) | ทดสอบ WOL/shutdown ของเครื่องจริงพร้อมแผนกู้คืนเครื่อง | **Pending** — ต้องวางวิธีเปิดกลับก่อนปิด host ที่ใช้งาน |
| (d) | Clean install บนเครื่องที่สองด้วย Controller URL/certificate ที่ reachable | **Pending** — localhost ใช้ข้ามเครื่องไม่ได้ |
| (e) | เก็บงาน release/maintenance remediation และ readiness WIP แยกไว้ ไม่แตะต้อง | **ขอบเขตคงเดิม** — ไม่เริ่มก่อนงานทดสอบหลัก |

หมายเหตุ: การตรวจ authentication แบบ 401-vs-200 จาก client ที่ไม่ใช่ loopback ยัง **Pending** และต้องทำใน LAN/remote phase เพราะ Stream Guard firewall fence บล็อก client ภายนอกโดยเจตนา

## 7. วิธีรันซ้ำ

รันจาก elevated PowerShell เท่านั้น (ทั้งสองคำสั่งหยุด/restart Agent service ชั่วครู่):

```powershell
# 1) apply setup ซ้ำ: certificate/trust + Sunshine credentials + Agent DPAPI store
scripts\agent\dev\Reset-SunshineCredentials.ps1 -UserName admin

# 2) ตรวจ fenced authenticated API (ผลที่คาด: RequestCompleted, HTTP 200, TlsValidated=true)
scripts\agent\dev\Test-SunshineLocalApi.ps1

# 3) เปิด GUI ภายใน fenced setup window (window จะปิดเองเมื่อ helper budget หมด)
scripts\agent\dev\Test-SunshineLocalApi.ps1 -OpenSetupUi
```

- คำสั่งที่ 1 ไม่พิมพ์รหัสผ่านออกมา และเขียน credential note ไว้ที่ path ในหัวข้อ 4.5
- คำสั่งที่ 2 และ 3 หยุด Agent ชั่วคราวเพื่อคืน pipe session แล้ว restart ใน `finally`; Stream Guard ต้อง Running ตลอด
- คำสั่งที่ 3 รายงาน `SetupWindowTimedOut` เป็นผลปกติเมื่อหมดเวลา ตราบใดที่ cleanup และ final state ผ่าน

## 8. สรุปสถานะหลักฐาน

| รายการ | สถานะ |
| --- | --- |
| Credential reset (Sunshine + Agent DPAPI) | **ตรวจสด** — state hash ตรงสูตร Foundation Sunshine และ DPAPI round-trip คืน username/plaintext เดิม |
| TLS certificate/trust และ Windows chain validation | **ตรวจสด** |
| Fenced API reachability + TLS chain/hostname validation + fence contract | **ตรวจสด** |
| Credential alignment (state hash + Agent DPAPI round-trip) | **ตรวจสด** |
| Remote 401-vs-200 authentication (non-loopback) | **Pending** — LAN/remote phase; fence บล็อกโดยเจตนา |
| Sunshine GUI ผ่าน trusted certificate | **ตรวจสด** (ภายใน setup window; ไม่ได้พิสูจน์ credential) |
| Moonlight pairing | **ตรวจสด** — pair จริงผ่าน fenced window (รายละเอียดหัวข้อ 9) |
| Moonlight stream จริง | **Pending** — ต้องมี stream grant/lease ซึ่งยังไม่เปิด rental |
| Browser → real VNC E2E | **Pending** |
| WOL/shutdown จริง | **Pending** |
| Clean install เครื่องที่สอง | **Pending** |

## 9. Moonlight real pairing (เพิ่ม 2026-11-17)

พิสูจน์บน host นี้ด้วย Moonlight client ที่ติดตั้งจริง (`C:\Program Files\Moonlight Game Streaming`) ภายใน fenced maintenance window ของ Sunshine:

- เปิด window ด้วย `Test-SunshineLocalApi.ps1 -OpenSetupUi`; web manager (`https://localhost:47990`), GameStream HTTP 47989 และ HTTPS 47984 เปิดListen ระหว่าง window
- `POST /api/qr-pair` คืน preset PIN (status=true, expires_in=120)
- รัน `Moonlight.exe pair <host LAN IP> --pin <pin>` — Moonlight ทำ GameStream pairing ครบทุก phase ผ่าน Sunshine: `phrase=getservercert` → `clientchallenge` → `serverchallengeresp` → `clientpairingsecret` → `https://...:47984/pair?...phrase=pairchallenge`
- ระหว่าง window `GET /api/clients/list` เห็น client list เพิ่มจาก 4 → 5 รายการ และ pairing **persist ลง `sunshine_state.json`** จริง (state mtime 2026-11-17 01:24 local; named_devices array เพิ่มขึ้น 1 รายการ)
- หมายเหตุ: entry ใหม่ชื่อ `admin1` ซ้ำ เนื่องจาก Moonlight ใช้ชื่อจาก host record เดิมของฝั่ง client; ยังไม่ได้ทดสอบ streaming และการ unpair/ตัด active session เพราะต้องมี stream grant/lease ตามข้อจำกัดห้ามเปิด rental เพื่อทดสอบ
- หลักฐานดิบ: `.next-local/moonlight-pair5-evidence.txt`; สถานะแผน superpowers ทุกไฟล์: `docs/superpowers/plans/STATUS-2026-11-17.md`

**Final status: SUNSHINE CREDENTIAL/GUI — COMPLETED & VERIFIED LIVE ON THIS HOST** — credential proof มาจาก `sunshine_state.json` hash และ Agent DPAPI round-trip; HTTP 200 บน loopback ไม่ใช่หลักฐาน credential และ remote authentication check ยัง Pending งานทดสอบปลายทางที่เหลือยังเป็น Pending ตามตารางข้างต้น ยกเว้น Moonlight pairing ที่พิสูจน์สดแล้ว ไม่มี commit และห้ามลบ backups

