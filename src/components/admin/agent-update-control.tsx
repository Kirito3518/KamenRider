"use client";

import { useEffect, useState } from "react";
import { isAgentOnline, updateStatusLabels, type AgentRelease } from "@/lib/agent-releases";
import type { Machine } from "@/lib/machine-contracts";

type CommandResult = { available: boolean; command?: string; expiresAtUtc?: string; operationId?: string; targetVersion?: string; reason?: string };
const terminal = new Set(["completed", "rolledBack", "expired"]);
const recoverable = new Set(["installing", "awaitingHeartbeat", "rollingBack", "failed"]);

export function AgentUpdateControl({ machine, onUpdated }: { machine: Machine; onUpdated?: () => void | Promise<void> }) {
  const [open, setOpen] = useState(false);
  const [releases, setReleases] = useState<AgentRelease[] | null>(null);
  const [selected, setSelected] = useState("");
  const [command, setCommand] = useState<CommandResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [clock, setClock] = useState(0);
  const awaitingSnapshot = Boolean(command?.operationId && (command.operationId !== machine.agentUpdateOperationId || !machine.agentUpdateStatus));
  const status = awaitingSnapshot ? "awaitingInstaller" : machine.agentUpdateStatus;
  const targetVersion = awaitingSnapshot ? command?.targetVersion : machine.agentUpdateTargetVersion;
  const locked = Boolean(status && !terminal.has(status));
  const leaseClear = machine.allocationHeld === false;
  const canStart = leaseClear && !locked && machine.approvalStatus === "approved" && Boolean(machine.agentVersion) && isAgentOnline(machine, clock);
  const canRecover = leaseClear && Boolean(status && recoverable.has(status) && machine.agentUpdateOperationId);
  const expires = command?.expiresAtUtc ? Date.parse(command.expiresAtUtc) : NaN;
  const expired = !Number.isFinite(expires) || clock >= expires;

  useEffect(() => {
    const update = () => setClock(Date.now());
    update();
    const timer = window.setInterval(update, 5_000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    if (!command?.expiresAtUtc) return;
    const deadline = Date.parse(command.expiresAtUtc);
    const update = () => setClock(Date.now());
    const timer = window.setTimeout(update, Math.max(0, deadline - Date.now()));
    window.addEventListener("focus", update);
    document.addEventListener("visibilitychange", update);
    return () => { window.clearTimeout(timer); window.removeEventListener("focus", update); document.removeEventListener("visibilitychange", update); };
  }, [command?.expiresAtUtc]);

  async function chooseRelease() {
    if (!canStart || busy) return;
    setOpen(true); setBusy(true); setMessage(""); setCommand(null); setSelected("");
    try {
      const response = await fetch("/api/admin/agent-releases", { cache: "no-store" });
      if (!response.ok) throw new Error();
      setReleases(await response.json() as AgentRelease[]);
    } catch { setMessage("โหลดรายการ release ไม่ได้ กรุณาลองใหม่"); }
    finally { setBusy(false); }
  }

  async function refreshMachine() {
    try { await onUpdated?.(); }
    catch { setMessage("โหลดสถานะเครื่องล่าสุดไม่ได้ กรุณารีเฟรชอีกครั้ง"); }
  }

  async function requestCommand(body: { releaseVersion: string } | { operationId: string }) {
    setBusy(true); setMessage(""); setCommand(null);
    try {
      const response = await fetch(`/api/admin/machines/${encodeURIComponent(machine.machineId)}/agent-update-command`, {
        method: "POST", cache: "no-store", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body),
      });
      if (!response.ok) throw new Error();
      const result = await response.json() as CommandResult;
      if (!result.available || !result.command || !result.operationId || !result.expiresAtUtc || !Number.isFinite(Date.parse(result.expiresAtUtc))) {
        setMessage(result.reason || "ยังไม่สามารถสร้างคำสั่งอัปเดตได้"); return;
      }
      setClock(Date.now()); setCommand(result); setOpen(false); await refreshMachine();
    } catch { setMessage("สร้างคำสั่งไม่สำเร็จ สถานะเครื่องหรือ lease อาจเปลี่ยนแล้ว กรุณารีเฟรชและตรวจสอบอีกครั้ง"); await refreshMachine(); }
    finally { setBusy(false); }
  }

  async function copy() {
    if (!command?.command || !Number.isFinite(expires) || Date.now() >= expires) { setClock(Date.now()); setMessage("คำสั่งหมดอายุแล้ว"); return; }
    try { await navigator.clipboard.writeText(command.command); setMessage("คัดลอกคำสั่งแล้ว"); }
    catch { setMessage("คัดลอกอัตโนมัติไม่ได้ เลือกข้อความในช่องแล้วกด Ctrl+C"); }
  }

  const choices = releases?.filter((release) => release.version !== machine.agentVersion) ?? [];
  return <section className="space-y-2 rounded border p-3" aria-label={`อัปเดต Agent ${machine.machineId}`}>
    <h3 className="font-semibold">Update Agent</h3>
    <p className="text-sm">สถานะอัปเดต: {status ? updateStatusLabels[status] || status : "ยังไม่มีการอัปเดต"}{targetVersion ? ` · เป้าหมาย ${targetVersion}` : ""}</p>
    <p className="text-sm text-muted-foreground">ผลอัปเดตแยกจากการอนุมัติและความพร้อมให้เช่า</p>
    {!leaseClear ? <p className="text-sm">{machine.allocationHeld ? "เครื่องมี lease อยู่ จึงอัปเดตไม่ได้" : "ไม่ทราบสถานะ lease กรุณารีเฟรชข้อมูลเครื่อง"}</p> : null}
    {leaseClear && !locked && machine.approvalStatus !== "approved" ? <p className="text-sm">ต้องอนุมัติเครื่องก่อนอัปเดต</p> : null}
    {leaseClear && !locked && machine.approvalStatus === "approved" && (!machine.agentVersion || !isAgentOnline(machine, clock)) ? <p className="text-sm">ต้องมี heartbeat ล่าสุดพร้อมเวอร์ชัน Agent ภายใน 20 วินาที</p> : null}
    <div className="flex flex-wrap gap-2">
      <button type="button" className="rounded border px-3 py-2 text-sm disabled:opacity-50" disabled={!canStart || busy || Boolean(command && !expired && !terminal.has(status || ""))} onClick={() => void chooseRelease()}>Update Agent</button>
      {status && recoverable.has(status) ? <button type="button" className="rounded border px-3 py-2 text-sm disabled:opacity-50" disabled={!canRecover || busy} onClick={() => void requestCommand({ operationId: machine.agentUpdateOperationId! })}>กู้คืนการอัปเดต</button> : null}
    </div>
    {open ? <div className="space-y-2">
      {busy && !releases ? <p role="status">กำลังโหลด release…</p> : null}
      {releases ? choices.length ? <><label className="block text-sm">เลือก release สำหรับ {machine.machineId}<select className="ml-2 rounded border bg-background p-2" value={selected} onChange={(event) => setSelected(event.target.value)}><option value="">เลือก release</option>{choices.map((release) => <option key={release.version} value={release.version}>{release.version} · {release.channel}</option>)}</select></label><button type="button" className="rounded bg-primary px-3 py-2 text-sm text-primary-foreground disabled:opacity-50" disabled={!selected || busy || !canStart} onClick={() => void requestCommand({ releaseVersion: selected })}>สร้างคำสั่งอัปเดต</button></> : <p>ไม่มี release ที่ต่างจากเวอร์ชันปัจจุบัน</p> : null}
      <button type="button" className="rounded border px-3 py-2 text-sm" onClick={() => setOpen(false)}>ปิด</button>
    </div> : null}
    {command?.command ? <div className="space-y-2"><p className="text-sm">คำสั่งสำหรับ {machine.machineId} · เป้าหมาย {command.targetVersion} · หมดอายุเมื่อ (UTC): {command.expiresAtUtc}</p><p className="text-sm">เรียกใช้ PowerShell แบบผู้ดูแลบนเครื่องนี้ คำสั่งมีตั๋วใช้ครั้งเดียว</p><textarea aria-label={`คำสั่งอัปเดต Agent สำหรับ ${machine.machineId}`} className="w-full rounded border bg-background p-2 font-mono text-xs" rows={5} readOnly spellCheck={false} value={command.command} /><button type="button" className="rounded border px-3 py-2 text-sm disabled:opacity-50" disabled={expired} onClick={() => void copy()}>คัดลอกคำสั่งอัปเดต</button>{expired ? <p className="text-sm">คำสั่งหมดอายุแล้ว</p> : null}</div> : null}
    <p role="status" aria-live="polite" className="text-sm">{message}</p>
  </section>;
}
