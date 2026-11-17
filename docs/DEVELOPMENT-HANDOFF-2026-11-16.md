# Development Handoff Report

วันที่ส่งต่อ: 2026-11-16 (Asia/Bangkok)

เอกสารนี้สร้างตามคำสั่งให้หยุดพัฒนาและส่งต่องาน ไม่ได้แก้โค้ด รัน build/test ติดตั้ง เปลี่ยนบริการ หรือ commit ระหว่างจัดทำ มีเพียงไฟล์รายงานนี้ที่เพิ่มขึ้น

การอ่านหลักฐาน: **ตรวจสด** = อ่าน filesystem/Git/service ในรอบจัดทำรายงาน; **บันทึกเดิม** = ผลในเอกสารหรือบริบทการทำงานก่อนหน้า ไม่ได้รันทวน; **UNKNOWN** = ยังยืนยันไม่ได้ ห้ามเปลี่ยนเป็น PASS เอง

## 1. Project Overview

โครงการ KamenRaiderPlay มีเว็บ Next.js, Controller และ Windows Agent สำหรับจัดการเครื่องลูก เป้าหมายเฉพาะที่ผู้ใช้ย้ำล่าสุดคือ Agent ที่ติดตั้งจริง เชื่อมต่อ Moonlight/Sunshine และ VNC และเปิด/ปิดเครื่องได้ ทดสอบบนเครื่องพัฒนานี้ก่อนให้ผู้ใช้นำไปทดสอบเครื่องอื่น ไม่ใช่การเปิดให้เช่าจริงหรือขยายงานระบบธุรกิจ

- Working checkout ที่ต้องใช้: `C:\Users\User\Documents\kamenraiderplay`
- เว็บ: Next.js 16.4.3, React 19.3.0, TypeScript, pnpm, Better Auth/SQLite
- Services: C#/.NET 10, ASP.NET Core Controller, SQLite, Windows Service, PowerShell 5.1/7
- เว็บติดต่อ Controller ผ่าน internal loopback พร้อม credential; Agent ติดต่อ HTTPS และใช้ identity/signature; Controller เก็บ machine/config/command state
- Moonlight เป็น client, Sunshine เป็น streaming host; TightVNC 2.8.90 เป็น VNC server; Controller มี WebSocket gateway สำหรับ admin noVNC แบบ read-only
- บริการ Stream Guard ที่เพิ่มในโครงการควบคุม Sunshine และ firewall ตาม stream authority การมี Guard Running ไม่ได้หมายความว่า Sunshine จะ Running

ข้อจำกัดจากผู้ใช้: ไม่แยก worktree; ไม่แตะ Reboot Restore Rx; URL ใช้ localhost ทดสอบก่อนแล้วตั้งผ่าน env; อ่าน/เขียน `.env.local` ได้แต่ห้ามเปิดเผย secrets; ถ้าใช้ผู้ช่วยให้ใช้ **gpt-6-luna / max เท่านั้น** ตามคำสั่งล่าสุด

## 2. Current Task & Objective

งานล่าสุดก่อนหยุดคือแก้ Sunshine GUI ที่เปิดไม่ได้ และตั้ง Sunshine username/password ให้ผู้ใช้ ผู้ใช้ระบุว่าไม่เคยเปลี่ยนจากค่าเริ่มต้นและอนุญาตให้ตั้งเองแล้ว **ไม่ได้ยืนยันว่ามี default credential คู่ใดที่ใช้ได้**

อาการที่พบ: Foundation GUI ติดต่อ Sunshine ไม่ได้; API probe จบ `HttpRequestException` ก่อนถึง HTTP response ขณะที่ Guard มี firewall block และ Sunshine ถูกหยุด ภาพและผล probe จึงยังไม่พิสูจน์ว่าเป็นปัญหารหัสผ่านเพียงอย่างเดียว

เป้าหมายค้าง: ตั้ง credential ของ Sunshine และ Agent ให้ตรงกันโดยรักษา pairing/config เดิม แล้วพิสูจน์ HTTPS authentication และเปิด GUI ได้จริง ไม่ใช่เพียง compile ผ่าน

## 3. Completed Work

| งาน | Implementation / ไฟล์เกี่ยวข้อง | หลักฐานและขอบเขต |
| --- | --- | --- |
| Agent install/enrollment | `public/agent/Bootstrap-Agent.ps1`, `scripts/agent/Install-AgentPackage.ps1`, `services/Controller/Machines/MachineEndpoints.cs`, `docs/agent-onboarding.md` | Implemented; ผู้ใช้เคยส่งผล Installed + enrollment pending; ตรวจสด Agent Running/Automatic ไม่ใช่หลักฐาน clean install บนเครื่องใหม่ |
| Controller และ machine configuration | `services/Controller/Program.cs`, `services/Controller/Agents/SyncEndpoint.cs`, `services/Controller/Data/`, `scripts/agent/dev/Run-Controller-Local.ps1` | มีโค้ดและผลเดิม heartbeat; ผู้ใช้เลือกชื่อ `PC ทดสอบ 1`, ราคา 20 บาท/ชม. การบันทึกค่าจริงล่าสุดยังไม่ได้ตรวจ DB รอบนี้ |
| Stream Guard / fencing | `services/Agent/Sunshine/StreamGuardPipeClient.cs`, `StreamGuardPipeServer.cs`, `StreamGateCoordinator.cs`, `WindowsStreamIngressPolicy.cs` | Implemented; Guard ตรวจสด Running/Automatic; ยังไม่ยืนยัน live streaming/expiry/crash matrix ครบ |
| Authenticated Sunshine probe | `services/Agent/Sunshine/AuthenticatedSunshineApiProbe.cs`, `SunshineCredentialStore.cs`, `SunshineCredentialProvisionCommand.cs`; commit `51242bc` | Implemented; มีรายงานเดิม focused tests ผ่าน แต่ live probe ของผู้ใช้ยัง FAIL ก่อนถึง API |
| VNC server/gateway | `services/Controller/RemoteDesktop/AdminVncEndpoints.cs`, `ReadOnlyVncProtocol.cs`, `AdminVncTargetProbe.cs`; `docs/agent-operations.md` | ตรวจสด `tvnserver` Running/Automatic; บันทึกเดิมระบุ real gateway ได้ raw frame 1920×1080 ขนาด 8,294,400 bytes; browser ทดสอบด้วย synthetic frame แยกกัน ยังไม่ใช่ browser→real VNC E2E |
| WOL | `services/Controller/Network/WakeService.cs`; commit `8e06be2` | บันทึกเดิมทดสอบ production sender ส่ง packet 102 bytes จาก Ethernet ไป broadcast และรับได้ ใช้ MAC ทดสอบ จึงไม่พิสูจน์ปลุกเครื่องจริง |
| Shutdown privilege inspection | `scripts/agent/dev/Test-AgentShutdownPrivilege.ps1`; commit `d9928c1` | ผู้ใช้ส่งผล `SeShutdownPrivilegePresent=True`, `Enabled=False`; เป็น read-only inspection ไม่ใช่การปิดเครื่องจริง |
| Release/update พื้นฐาน | `services/Controller/Agents/AgentReleaseEndpoints.cs`, `AgentReleaseStore.cs`, `services/AgentMaintenance/`; แผน 2026-11-08/09 | มี implementation และ software acceptance เดิม แต่ remediation ยังมี WIP; ไม่ถือว่าระบบ live update สมบูรณ์ |

อย่าอ้างว่าทุกรายการด้านบน verified end-to-end หรือพร้อมเปิดเช่า หลักฐานคนละระดับ และเอกสารบางฉบับเก่ากว่าสถานะติดตั้งปัจจุบัน

## 4. Work in Progress

### Sunshine credential reset — จุดหยุดล่าสุด

ตรวจสดพบเพียง `scripts/agent/dev/SunshineCredentialReset/SunshineCredentialReset.csproj` อ้างอิง Agent และ target `net10.0-windows` ยังไม่มี `Program.cs` และ **ไม่มี** `scripts/agent/dev/Reset-SunshineCredentials.ps1`

`services/Agent/Agent.csproj` เพิ่ม `InternalsVisibleTo` สำหรับ `SunshineCredentialReset` แล้ว แต่ helper ยังใช้ไม่ได้ ไม่มีหลักฐานว่าเปลี่ยน credential บนเครื่องจริง

ผู้ช่วย `/root/sunshine_creds_helper_luna` และ `/root/sunshine_creds_wrapper_luna` อยู่สถานะ interrupted ตอนตรวจ; ผู้ช่วยวิเคราะห์ credential เสร็จแล้ว ไม่ได้เรียก reset จริง

### Setup-window WIP

ไฟล์แก้ค้าง:

- `scripts/agent/dev/Test-SunshineLocalApi.ps1`: เพิ่ม `-OpenSetupUi`, รอ ready marker ก่อนเปิด browser
- `scripts/agent/dev/StreamGuardFencedApiProbe/Program.cs`: เพิ่ม `--setup-window`, `setup-ready.json`, signal/deadline
- `scripts/agent/dev/Test-SunshineLocalApiLifecycle.ps1`
- `scripts/agent/dev/Test-SunshineLocalApiPreflight.ps1`

บริบทเดิม: helper ใช้ Guard fenced cleanup ที่มีหน้าต่างสูงสุดประมาณ 45 วินาที โดยเวลารวมการเริ่ม Sunshine ด้วย; ตั้งใจหยุด Agent ชั่วคราวเพื่อคืน pipe session และ restart ใน finally โดย Guard ยังทำงาน รายงาน reviewer เดิมพบ stale ready marker, error status และตรวจ Guard หลังจบไม่ครบ ผู้เขียนแจ้งว่าแก้แล้ว แต่รายงานนี้ยังไม่ได้ทวน diff/รัน tests หรือยืนยัน GUI จริง

### WIP อื่นที่ต้องรักษาไว้

AgentMaintenance และ Readiness มี modified/untracked หลายไฟล์ตามหัวข้อ 7 ไม่ใช่งานที่ต้องเริ่มทำต่อก่อน Sunshine และไม่สามารถยืนยันผู้แก้ทุกไฟล์ได้ ห้าม reset/ลบทิ้ง/commit รวมโดยถือว่าเป็นงานของตน

## 5. Pending / Not Started / Blocked

| ประเภท | งาน | Priority | เหตุผล / dependency / สิ่งที่ต้องทำต่อ |
| --- | --- | --- | --- |
| Incomplete | Sunshine credential helper/wrapper | High | มีเพียง project skeleton; ต้องอ่าน store/CLI และทำ transaction + verification ให้ครบ |
| Blocked | Sunshine authenticated HTTPS และ GUI | High | live probe ไม่ถึง HTTP, TLS ยังไม่ validated, service ถูก Guard หยุด; ต้องตรวจสาเหตุ startup/listener/TLS แยกจาก auth |
| Incomplete | Setup-window | High | มี uncommitted code และ mock tests; ต้อง review และพิสูจน์ GUI ในช่วงที่อนุญาตได้จริง |
| Incomplete | Moonlight pairing/stream | High | รอ Sunshine API/credential/TLS และทดสอบ client จริง |
| Incomplete | Browser→real VNC E2E | High | gateway จริงและ viewer fixture เคยผ่านแยกกัน; ต้องทดสอบเส้นทางเดียวกัน |
| Not Started (ยังไม่มีหลักฐานเริ่ม live test) | Shutdown และ WOL ของเครื่องจริง | High | privilege/UDP checks ไม่เท่ากับ power cycle; ต้องวางวิธีเปิดกลับก่อนปิด host ที่กำลังใช้งาน |
| Not Started (ยังไม่มีหลักฐานทดสอบครบ) | Clean install บนเครื่องลูกอีกเครื่อง | High | localhost ใช้ข้ามเครื่องไม่ได้; ต้องตั้ง reachable URL/certificate/package ก่อนผู้ใช้นำไปทดสอบ |
| Incomplete | Release remediation / AgentMaintenance | Medium | มี code ค้างจำนวนมากและ protocol ต่างรุ่น; เก็บไว้ ไม่แทรกก่อนงาน Agent หลัก |
| Incomplete | Readiness probe-file security | Low สำหรับงานเร่งด่วนนี้ | มี untracked code/tests; ผู้ใช้ไม่ต้องการให้ readiness/rental rollout ขวางการทดสอบ Agent |

ไม่ได้จัดแผน checkbox ที่ยังว่างทั้งหมดเป็น Not Started เพราะพบโค้ดที่ทำแล้วแต่เอกสารไม่อัปเดต

## 6. Plan Files & Reference Paths

ทุก path ด้านล่าง relative ต่อ `C:\Users\User\Documents\kamenraiderplay`:

| Path | ใช้เพื่อ / ความต่างจากของจริง |
| --- | --- |
| `docs/full-rental-roadmap.md` | ภาพรวมข้อตกลงและลำดับระบบทั้งหมด อัปเดต 2026-11-09; กว้างกว่างานเร่งด่วนที่ผู้ใช้จำกัดล่าสุด |
| `docs/superpowers/specs/2026-10-29-agent-design.md` | ข้อตกลง Agent/Controller, network, Sunshine, expiry |
| `docs/superpowers/plans/2026-10-29-agent.md` | แผนฐาน Tasks 1–9; Task 6 Sunshine, Task 5 shutdown, Task 9 acceptance; checkbox ไม่ใช่หลักฐานสถานะล่าสุด และหัวข้อเก่าเกี่ยวกับ Rx ไม่ให้สิทธิ์ไปยุ่ง Rx |
| `docs/superpowers/plans/2026-11-05-agent-install.md` | Build/install/bootstrap/install-to-pending; มีรายการ checked แต่ต้องแยก test fixtures จาก live host |
| `docs/superpowers/specs/2026-11-05-agent-install-design.md` | เป้าหมายและขอบเขต install/enrollment |
| `docs/superpowers/plans/2026-11-05-agent-presence.md` | Heartbeat/presence ระหว่าง boot reconciliation |
| `docs/superpowers/plans/2026-11-06-agent-stream-gate.md` | Stream Guard/fencing; ใช้ร่วมกับ `docs/superpowers/specs/2026-11-06-agent-stream-gate-design.md` |
| `docs/superpowers/plans/2026-11-06-tightvnc-agent-install.md` | TightVNC install/firewall; เครื่องปัจจุบันมี service แล้ว |
| `docs/superpowers/plans/2026-11-07-agent-readiness-diagnostics.md` | Diagnostics; ไม่ใช่เหตุให้ขยายงานเปิดเช่า |
| `docs/superpowers/plans/2026-11-07-admin-install-command-ui-regression.md` | UI คำสั่งติดตั้ง |
| `docs/superpowers/plans/2026-11-08-controller-lease-readiness-guard.md` | Lease/readiness gate; ไม่ใช่งานที่ต้องทำก่อนแก้ GUI |
| `docs/superpowers/plans/2026-11-08-agent-release-management.md` | Release/update รุ่นแรก Tasks 1–6 |
| `docs/superpowers/plans/2026-11-09-agent-release-remediation.md` | แผน remediation ของ update; อ้าง spec ชื่อเดียวกันใน `docs/superpowers/specs/` เป็น binding spec; protocol 2 และ gate ปิดจน qualification ครบ |
| `docs/agent-onboarding.md` | วิธีติดตั้งและ provision credentials; ข้อความสถานะท้ายเอกสารบางส่วนเก่า |
| `docs/agent-operations.md` | คู่มือปฏิบัติการ/credential/VNC |
| `docs/agent-acceptance.md` | บันทึกผล software acceptance พร้อมข้อจำกัด simulation; ไม่ใช่ผลของ dirty tree ปัจจุบัน |
| `docs/sunshine-compatibility.md` | Compatibility/fence evidence เดิม; ข้อความว่าไม่มี service ไม่ตรงกับ inventory ปัจจุบัน |
| `docs/post-boot-probe-file-format.md` | รูปแบบ restore probe; เก็บไว้อ้างอิง ไม่ต้องขยายงานตอนนี้ |

ลำดับอำนาจ: คำสั่งผู้ใช้ล่าสุด > ขอบเขตที่ตกลง > spec/plan ที่ตรง feature > checkbox เก่า แผน 2026-11-09 แทนแนวทาง update บางส่วนของ 2026-11-08 แต่ไม่ได้แทนแผน Agent ทั้งหมด ไม่พบแผนเฉพาะ credential reset ในรายการเอกสารที่ตรวจ

## 7. Modified Files & Git Status

ตรวจสดก่อนเพิ่มรายงาน:

- Main: `C:\Users\User\Documents\kamenraiderplay`, branch `master`
- HEAD: `51242bc176f9a6836434dde1099fdf0e52fb0d0a` — Authenticate guarded Sunshine API probe
- Old worktree: `C:\Users\User\.codex\worktrees\agent-controller\kamenraiderplay`, detached HEAD `880b16d`, untracked `.playwright-cli/`; **อย่าพัฒนาที่นี่เพียงเพราะ tool cwd ชี้มา**
- อีก worktree: `D:\codex-task5-validation\staged-snapshot`, detached HEAD `8f0abd6`; ไม่มีการเปลี่ยนแปลงในรอบนี้
- Tracked diff: 19 ไฟล์, 1,706 insertions / 130 deletions ณ ตอนตรวจ; ไม่มี deleted files ใน `git status --short`

Modified files ทั้งหมด:

```text
scripts/agent/dev/StreamGuardFencedApiProbe/Program.cs
scripts/agent/dev/Test-SunshineLocalApi.ps1
scripts/agent/dev/Test-SunshineLocalApiLifecycle.ps1
scripts/agent/dev/Test-SunshineLocalApiPreflight.ps1
services/Agent/Agent.csproj
services/AgentMaintenance/AgentMaintenanceCli.cs
services/AgentMaintenance/InstalledAgentSourceInspector.cs
services/AgentMaintenance/LocalUpdateEngine.cs
services/AgentMaintenance/MaintenanceApplyCommand.cs
services/AgentMaintenance/MaintenanceApplyOperation.cs
services/AgentMaintenance/MaintenanceBackupPublisher.cs
services/AgentMaintenance/MaintenanceSourceBindingOperation.cs
services/AgentMaintenance/Program.cs
services/AgentMaintenance/WindowsMaintenanceApplyServiceLifecycle.cs
services/AgentMaintenance/WindowsMaintenanceFence.cs
services/Controller.Tests/AgentMaintenanceCliTests.cs
services/Controller.Tests/InstalledAgentSourceInspectorTests.cs
services/Controller.Tests/MaintenanceApplyCommandTests.cs
services/Controller.Tests/MaintenanceBackupPublisherTests.cs
```

Untracked source ก่อนเพิ่มรายงาน:

```text
scripts/agent/dev/SunshineCredentialReset/SunshineCredentialReset.csproj
services/Agent.Tests/ProbeStoreValidationTests.cs
services/Agent.Tests/ProbeStoreWindowsSecurityTests.cs
services/Agent/Readiness/ManagedProbeFile.cs
services/Agent/Readiness/ProbeFileReadValidator.cs
services/Agent/Readiness/ProbeStorePath.cs
services/Agent/Readiness/WindowsManagedProbeFile.cs
services/Agent/Readiness/WindowsProbeAccessEvaluator.cs
services/Agent/Readiness/WindowsProbeFileIo.cs
services/AgentMaintenance/MaintenanceLifecycleCommands.cs
services/AgentMaintenance/MaintenanceReconcileCommand.cs
services/AgentMaintenance/MaintenanceSourceBindingPreparation.cs
services/AgentMaintenance/MaintenanceTerminalStateVerifier.cs
services/Controller.Tests/MaintenanceHttpDeadlineTests.cs
services/Controller.Tests/MaintenanceHttpProtocolTests.cs
services/Controller.Tests/MaintenanceLifecycleCommandTests.cs
services/Controller.Tests/MaintenanceLifecycleOrchestrationTests.cs
services/Controller.Tests/MaintenanceReconcileCommandTests.cs
```

Untracked generated directories: `obj-apply-isolated/` ใน `services/Agent`, `AgentMaintenance`, `AgentPackage`, `Contracts`, `Controller`, `Controller.Tests` และ `Controller.Tests/Fixtures/{AgentMetadataFixture,AgentVersionThree,AgentVersionTwo,LegacyAgentMetadataFixture,LegacyInstalledAgent}` ห้ามลบทิ้งโดยถือว่าได้รับอนุญาตจากรายงาน

รายงานนี้เพิ่ม `docs/DEVELOPMENT-HANDOFF-2026-11-16.md` อีกหนึ่งไฟล์ ไม่มี commit/push/reset/revert

Schema: มี SQL migrations `001`–`020` ใน `services/Controller/Data/Migrations/` รวม maintenance chains, sync proof, release contract, source binding, restore quarantine และ recovery intents ไม่มี migration dirty ใน status แต่ **ยังไม่ได้ตรวจว่า live DB apply ถึงเลขใด**

Environment names จาก `.env.example` ที่เกี่ยวข้อง: `CONTROLLER_INTERNAL_URL`, `CONTROLLER_SERVICE_CREDENTIAL`, `CONTROLLER_AGENT_URL`, `CONTROLLER_DATABASE_PATH`, `AGENT_INSTALL_ENABLED`, `AGENT_INSTALL_BASE_URL`, `AGENT_PACKAGE_URL`, `AGENT_PACKAGE_SHA256`, `AGENT_CONTROLLER_URL`, `AGENT_VNC_ALLOWED_SOURCE_IP`, `AGENT_TRUST_ROOT_THUMBPRINT`; auth ใช้ `BETTER_AUTH_SECRET`, `BETTER_AUTH_URL`, `AUTH_DATABASE_PATH` ไม่คัดลอกค่า secrets ลงเอกสาร

Process/service snapshot: Agent Running/Automatic, Stream Guard Running/Automatic, SunshineService Stopped/Automatic, tvnserver Running/Automatic มี dotnet PID 91524 และหลาย node/PowerShell processes แต่ไม่ได้ตรวจ command line จึงไม่ระบุว่าแต่ละ process เป็น build/server ใด ไม่ได้ kill/stop process ใดระหว่าง handoff

## 8. Technical Decisions & Known Issues

1. **Sunshine GUI ยังใช้ไม่ได้จริง**: ผลผู้ใช้ล่าสุด `Status=RequestFailed`, `ApiStatus=NotReached`, `TlsValidated=False`, `FailureCategory=HttpRequestException`; cleanup ทำให้ Sunshine กลับ Stopped และ Agent Running การ restart อย่างเดียวอาจถูก Guard หยุดซ้ำ
2. **HTTPS และ authentication จำเป็น**: ห้ามแก้ด้วยการเปลี่ยนเป็น HTTP หรือ bypass certificate validation ต้องวินิจฉัย listener/TLS/auth เป็นคนละขั้น
3. **Agent credential store มีอยู่จริง**: `SunshineCredentialStore.cs` และ `SunshineCredentialProvisionCommand.cs` ใช้ DPAPI/ACL มีผู้ช่วยเคยอ่านผิด checkout แล้วรายงานว่าไม่มี store; ข้อสรุปนั้นผิด ตรวจสดพบไฟล์ใน main
4. บันทึกการตรวจ Foundation รุ่นที่ติดตั้งเดิม: `v2026.1031.121458.杂鱼`, `C:\Program Files\Sunshine\sunshine.exe`; ต้องตรวจรุ่นซ้ำก่อนใช้ เพราะ binary อาจเปลี่ยนระหว่างงาน
5. จากการตรวจ source Foundation ก่อนหน้า คำสั่ง `sunshine.exe --creds <username> <password>` แก้ credential fields ใน `config\sunshine_state.json` โดยรักษา pairing fields แต่ password อยู่ใน process arguments ชั่วคราว, initialization อาจ truncate `sunshine.log`, และ exit 0 ไม่รับประกัน save สำเร็จ ต้องตรวจข้อมูลหลังเขียน ไม่ใช้ exit code อย่างเดียว
6. Reset ต้องรักษา state/config/ACL และ Agent DPAPI blob เดิมให้ rollback ได้หากอัปเดตฝั่งใดล้มเหลว ห้ามเก็บ plaintext secret ใน chat/log/ไฟล์ชั่วคราวทั่วไป ห้ามคิดว่าการเขียน credential เท่ากับ authenticated API ผ่าน
7. บันทึกก่อนหน้าพบ ACL staging บางตำแหน่งให้ interactive user เขียนได้; ไม่ควรนำ helper ไปให้ SYSTEM รันจากตำแหน่งดังกล่าวโดยไม่ตรวจ ACL ใหม่ แนวทางค้างใช้ protected staging ใต้ Windows System32
8. `Start-Process -Verb RunAs` เคยถูก host policy ปฏิเสธ ห้ามหาวิธีเลี่ยงหรืออ้างว่า UAC ได้รับอนุมัติแล้ว เตรียมคำสั่งให้ผู้ใช้รันใน Administrator PowerShell หากยังจำเป็น
9. `SeShutdownPrivilegeEnabled=False` ไม่ได้พิสูจน์ shutdown ล้มเหลว เพราะ process อาจ enable privilege ก่อนใช้ ยังต้องทดสอบจริงแบบมีวิธีคืนเครื่อง
10. local package/source/installed Agent/Controller อาจคนละรุ่น บันทึกเดิม installed Agent เป็น `1.0.0+g3179c802b681`; package dev มี dirty source และ maintenance protocol ต่างรุ่น ห้ามถือว่า HEAD ถูก deploy แล้ว
11. มีโค้ด update/restore quarantine จำนวนมากที่ยังไม่จบ ผู้ใช้ไม่ต้องการให้ขยายงานนี้ก่อนทำ Sunshine/VNC/power ให้ทดสอบได้
12. ห้ามแตะ Reboot Restore Rx, ห้ามเปิด rental/approve machine เพื่อหลบข้อจำกัด test, ห้ามปิด firewall ทั้งระบบเพื่อให้ GUI ผ่าน

## 9. Testing & Validation Status

ไม่มีการรัน tests/build ในรอบ handoff ตารางนี้แยกหลักฐานเดิมจาก tree ปัจจุบัน

| รายการ | สถานะหลักฐาน | ขอบเขต |
| --- | --- | --- |
| TypeScript | PASS (เอกสารเดิม), UNKNOWN (current tree) | `docs/agent-acceptance.md` บันทึก tsc exit 0 |
| ESLint | PASS (เอกสารเดิม), UNKNOWN (current tree) | 0 errors, existing img warning 1 รายการ |
| Unit tests .NET | PASS (เอกสารเดิม), UNKNOWN (current tree) | acceptance final fix wave: Agent 245/245, Controller 259/259; มีรายงานบริบทภายหลัง Agent 359 pass/1 skip แต่ไม่ได้เปิด raw log ยืนยันซ้ำ |
| Web integration/auth | PASS (เอกสารเดิม) | acceptance: 41 UI + 9 helper, 23 API scenarios/28 cases; auth 8/8 ไม่ใช่ live service E2E |
| Build | PASS (เอกสารเดิม), UNKNOWN (current tree) | Next build และ isolated .NET เคยผ่าน; credential helper ยังไม่มี entry point และไม่ได้ build |
| Installer/PowerShell | PASS (เอกสารเดิม) | PS5.1/7 mock/ACL suites; ผู้ใช้เคยส่ง Test-ServicePlans และ Test-PrivateInstallFiles ผ่าน |
| Enrollment บนเครื่องนี้ | PASS (ผลผู้ใช้เดิม) | ได้ machine receipt pending; ตรวจสด service Running ไม่ใช่ clean-install matrix |
| Sunshine live API | FAIL | ผลผู้ใช้ HttpRequestException, API not reached, TLS not validated |
| Sunshine reset ใหม่ | NOT RUN | ไม่มี helper implementation/wrapper และไม่มีหลักฐาน reset จริง |
| Setup-window WIP | UNKNOWN | มีรายงานผู้ช่วยว่า mock tests ผ่าน แต่ไม่มี live GUI success |
| VNC gateway จริง | PASS (บันทึกก่อนหน้า) | ได้ framebuffer; browser กับ real server พร้อมกัน NOT RUN/ไม่มีหลักฐาน |
| WOL packet | PASS (บันทึกก่อนหน้า) | packet send/receive; actual hardware wake NOT RUN/ไม่มีหลักฐาน |
| Shutdown จริง | NOT RUN | ตรวจ token อย่างเดียว |
| Migration/live DB compatibility | UNKNOWN | SQL files มีอยู่ แต่ไม่ได้ query schema/live DB |
| เครื่องลูกแยกเครื่อง / WAN / reboot cycle | NOT RUN / ไม่มีหลักฐานครบ | ห้ามนับ localhost หรือ mock เป็นผลจริงเหล่านี้ |

## 10. Prioritized Next Steps

ทำต่อเมื่อผู้ใช้สั่ง resume เท่านั้น:

1. **อ่าน diff Sunshine ที่มีอยู่และตรวจ runtime แบบ read-only** — ใช้ไฟล์ในหัวข้อ 4 กับ credential store, service/listener/log ที่เกี่ยวข้อง เพื่อแยก Guard stop, startup failure, TLS และ auth; acceptance: ระบุสาเหตุ GUI/API ไม่ได้โดยมีหลักฐาน ไม่สุ่มแก้หลายอย่างพร้อมกัน
2. **ทำ credential reset ที่ค้างให้เสร็จอย่างแคบที่สุด** — เติม helper/wrapper เฉพาะที่จำเป็นและทดสอบ temporary state/rollback; dependency คือ behavior ของ Sunshine รุ่นติดตั้งและ Agent store; acceptance: credential สองฝั่งตรงกัน รักษา pairing/config/ACL และไม่รั่ว secret การตั้งค่าจริงต้องมีการยืนยันหลังเขียน
3. **พิสูจน์ HTTPS และ GUI บน host นี้** — ทวน setup-window WIP และใช้ lifecycle ที่ไม่แข่งกับ Guard; acceptance: authenticated HTTPS response จริง, GUI ใช้ได้ตามวิธีที่ผู้ใช้ทำซ้ำได้, รายงาน service/firewall หลังจบตรงจริง ไม่เรียก success จาก marker อย่างเดียว
4. **ทดสอบ Moonlight จริง** — ใช้ `SunshineClient.cs`, `PairingSession.cs`, `LazySunshinePairingSession.cs` และ Controller pairing endpoint; dependency ข้อ 3; acceptance: pair และ stream ได้จริง พร้อมรายงานสิ่งที่ยังไม่ได้ทดสอบ
5. **ต่อ browser กับ VNC จริง** — ใช้ gateway เดิม; acceptance: เห็น framebuffer ผ่าน admin browser โดยไม่ส่ง secret ใน log และไม่ขยายสิทธิ์เกิน read-only ที่มี
6. **ทดสอบ power อย่างมีแผนคืนเครื่อง** — ตรวจ WOL NIC/MAC/broadcast และเครื่องส่งก่อน shutdown; acceptance: ปิด/เปิดจริงสำเร็จ ไม่ใช้เพียง UDP test เป็นข้อสรุป
7. **เตรียม package ให้ผู้ใช้ทดสอบอีกเครื่อง** — หลังงานหลักผ่าน ตั้ง reachable URL/env/certificate และตรวจ artifact ตรง source; acceptance: เครื่องใหม่ติดตั้งจาก admin command, ขึ้น pending และทดสอบ Sunshine/VNC/power ตามขอบเขตได้ ไม่ต้องเปิดเช่าจริง

Update remediation/Readiness WIP ให้เก็บไว้และรายงานแยก ไม่ทำ refactor หรือ architecture เพิ่มเพื่อเลี่ยงงานหลัก

## 11. Exact Resume Point

เริ่มที่ main checkout `C:\Users\User\Documents\kamenraiderplay` บน `master`, HEAD `51242bc176f9a6836434dde1099fdf0e52fb0d0a` ตรวจ status ซ้ำเพื่อเห็นงานคนอื่นก่อนแก้

เปิดตามลำดับ:

1. `scripts/agent/dev/SunshineCredentialReset/SunshineCredentialReset.csproj` — มีเพียง skeleton; **Program.cs ยังไม่มี**
2. `services/Agent/Agent.csproj` — friend assembly เพิ่มแล้ว
3. `services/Agent/Sunshine/SunshineCredentialStore.cs`
4. `services/Agent/Sunshine/SunshineCredentialProvisionCommand.cs`
5. `services/Agent/Sunshine/AuthenticatedSunshineApiProbe.cs`
6. `scripts/agent/dev/StreamGuardFencedApiProbe/Program.cs` และ diff
7. `scripts/agent/dev/Test-SunshineLocalApi.ps1` และ lifecycle/preflight tests

`scripts/agent/dev/Reset-SunshineCredentials.ps1` **ยังไม่มี ณ ตรวจสด** อย่าบอกผู้ใช้ให้รันไฟล์นี้ก่อนสร้างและตรวจจริง

ขั้นตอนสำเร็จล่าสุด: วิเคราะห์วิธี reset และเตรียม project/friend declaration งานที่ยังไม่สำเร็จ: helper implementation, wrapper, live reset, authenticated API และ GUI ไม่มีเหตุให้ทำ enrollment ใหม่หรือหมุน identity เพื่อแก้ credential

## 12. Instructions for Next Agent

- อ่านรายงานนี้, `docs/agent-onboarding.md`, `docs/agent-operations.md`, Sunshine compatibility และแผน Agent Task 6/stream gate ก่อนเริ่ม
- ทำงาน main checkout เท่านั้น ระบุ workdir ทุกคำสั่ง อย่าอ่าน old worktree แล้วสรุปว่าไฟล์/feature ไม่มี
- ใช้หลักฐานโค้ดและ live test แยกจาก checkbox/documentation และรายงานผู้ช่วย ห้ามบอกเปอร์เซ็นต์เสร็จโดยไม่มี acceptance mapping
- ห้ามรัน service/firewall/credential mutation หรือ tests ที่มี live side effects เพียงเพื่อทำ handoff; ต้องรอคำสั่งทำงานต่อ
- เมื่อ resume แล้ว อย่าขอ username/password ซ้ำ ผู้ใช้อนุญาตให้ตั้งเอง แต่ต้องไม่เผย secret ใน chat และต้องมีทางให้ผู้ใช้ทราบ credential อย่างเหมาะสมบนเครื่อง
- รักษา pairing state, identity, existing config, private ACL และ cleanup/finally ที่ restart Agent อย่าปิด Guard/firewall ถาวรเพื่อให้ probe ผ่าน
- ถ้าต้องทำงานแบบผู้ช่วย ใช้ luna max ตามคำสั่งล่าสุด และแบ่ง ownership ชัดเจน ไม่แก้ไฟล์เดียวกันพร้อมกัน
- ถ้าแก้เว็บ ต้องอ่าน relevant guide ใน `node_modules/next/dist/docs/` ตาม AGENTS.md ก่อน เพราะ Next.js รุ่นนี้เปลี่ยน API
- ไม่ commit/push/reset/revert/ลบ untracked โดยไม่ได้รับคำสั่ง อย่าแตะ `.env.local` secrets ใน output
- อย่าเริ่ม full rental, restore tooling, payment, release fleet rollout หรือ refactor ใหม่ก่อน Sunshine/VNC/power ที่ผู้ใช้ต้องการ
- งานขั้นต่อไปถือว่าสำเร็จเมื่อพิสูจน์ credential สองฝั่งและ authenticated HTTPS/GUI จริงได้ ไม่ใช่แค่ build ผ่านหรือ service Running

### Quick Handoff Summary

กำลังทำ Windows Agent สำหรับ Sunshine/Moonlight, VNC และเปิด/ปิดเครื่อง มี install/enrollment, services, gateway, WOL sender และ probe แล้ว แต่ Sunshine API/GUI ยังไม่ผ่านจริง งานล่าสุดค้างที่ตั้ง credential: helper มีแค่ csproj และ friend assembly, wrapper ยังไม่มี ยังไม่ได้ตั้งรหัสจริง มี setup-window และ maintenance/readiness WIP ที่ต้องรักษาไว้

อ่าน `docs/superpowers/plans/2026-10-29-agent.md` Task 6, stream gate plan, onboarding/operations และรายงานนี้ก่อน เริ่มด้วยตรวจ diff และสาเหตุ Sunshine ไม่เปิด listener แล้วทำ credential reset/HTTPS/GUI ให้จบในขอบเขตเล็กที่สุด ก่อน Moonlight, browser VNC และ power test จริง ไม่เริ่มระบบเช่าเพิ่ม

### Final Status

**DEVELOPMENT PAUSED — READY FOR HANDOFF**

หยุดพัฒนาแล้ว ไม่มีการติดตั้ง เปลี่ยน credential/service หรือ commit ในการจัดทำรายงาน รอคำสั่งใหม่
