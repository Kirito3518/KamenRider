"use client";
import { useCallback, useEffect, useRef, useState, type ChangeEventHandler } from "react";
import { AdminVncViewerButton } from "@/components/admin/admin-vnc-viewer";
import { AgentUpdateControl } from "@/components/admin/agent-update-control";
import type {
  Lease,
  Machine,
  ReadinessDiagnosticCheckId,
  ReadinessDiagnosticStatus,
} from "@/lib/machine-contracts";
import { getInstallCommandExpiryState } from "@/lib/agent-install-expiry.mjs";
import {
  getSavedVncTargetForMetadataUpdate,
  getVncTargetForExplicitSave,
  mergeVncTargetDrafts,
  updateVncTargetDraft,
} from "@/lib/admin-vnc-target-drafts.mjs";

const READINESS_CHECK_LABELS: Record<ReadinessDiagnosticCheckId, string> = {
  baseline: "การตรวจพื้นฐาน",
  bootChallenge: "การทดสอบยืนยันการบูต",
  restoration: "การตรวจการคืนค่าสภาพเครื่อง",
  streamFence: "การตรวจป้องกันการสตรีมก่อนพร้อม",
  desktopHealth: "การตรวจสุขภาพเดสก์ท็อป",
  configurationRevision: "การตรวจ revision การตั้งค่า",
  leaseReconciliation: "การตรวจสถานะ lease",
  runtimeHealth: "การตรวจความพร้อมขณะทำงาน",
};

const READINESS_STATUS_LABELS: Record<ReadinessDiagnosticStatus, string> = {
  passed: "ผ่าน",
  blocked: "ถูกบล็อก",
  unavailable: "ไม่มีระบบตรวจสอบ",
  unknown: "ยังไม่ทราบผล",
};

type AgentInstallCommandResult = {
  available: boolean;
  command?: string;
  expiresAtUtc?: string;
  reason?: string;
};

async function fetchInstallCommand(): Promise<AgentInstallCommandResult> {
  const response = await fetch("/api/admin/agent-install-command", {
    method: "POST",
    cache: "no-store",
  });
  if (!response.ok) throw new Error("ตรวจชุดติดตั้งไม่ได้");
  return (await response.json()) as AgentInstallCommandResult;
}

async function request(path: string, body?: unknown, method = "POST") {
  const r = await fetch(path, {
    method,
    cache: "no-store",
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const data = await r.json();
  if (!r.ok) throw new Error(`${r.status}: ${data.error}`);
  return data;
}

export function MachinesManager() {
  const [machines, setMachines] = useState<Machine[]>([]);
  const [vncTargetDrafts, setVncTargetDrafts] = useState<Record<string, string>>({});
  const [vncTargetCheckStatuses, setVncTargetCheckStatuses] = useState<
    Record<string, { targetAddress: string; checkedAt: number | null; state: "checking" | "supported" | "unsupported" | "error" }>
  >({});
  const vncTargetCheckRuns = useRef(new Map<string, number>());
  const [installCommand, setInstallCommand] = useState<string | null>(null);
  const [installExpiresAtUtc, setInstallExpiresAtUtc] = useState<string | null>(null);
  const [installClockNow, setInstallClockNow] = useState(0);
  const [isRefreshingInstallCommand, setIsRefreshingInstallCommand] = useState(false);
  const [installStatus, setInstallStatus] = useState("กดปุ่มเพื่อขอคำสั่งติดตั้ง");
  const [copyStatus, setCopyStatus] = useState("");
  const [trialLease, setTrialLease] = useState<Lease | null>(null);
  const [loadStatus, setLoadStatus] = useState("กำลังโหลด…");
  const [message, setMessage] = useState("");
  async function load() {
    try {
      const nextMachines: Machine[] = await request(
        "/api/admin/machines",
        undefined,
        "GET",
      );
      setMachines(nextMachines);
      setVncTargetDrafts((current) =>
        mergeVncTargetDrafts(current, nextMachines),
      );
      setLoadStatus("");
    } catch (e) {
      setLoadStatus(String(e));
    }
  }
  useEffect(() => {
    let active = true;
    let busy = false;
    async function refresh() {
      if (busy) return;
      busy = true;
      try {
        const r = await fetch("/api/admin/machines", { cache: "no-store" });
        if (!r.ok) throw new Error("โหลดเครื่องไม่ได้");
        const data: Machine[] = await r.json();
        if (active) {
          setMachines(data);
          setVncTargetDrafts((current) => mergeVncTargetDrafts(current, data));
          setLoadStatus("");
        }
      } catch (e) {
        if (active) setLoadStatus(String(e));
      } finally {
        busy = false;
      }
    }
    void refresh();
    const timer = window.setInterval(() => {
      if (document.visibilityState === "visible") void refresh();
    }, 5000);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, []);
  const refreshInstallCommand = useCallback(async () => {
    setIsRefreshingInstallCommand(true);
    setInstallStatus("กำลังขอคำสั่งติดตั้ง…");
    setCopyStatus("");
    try {
      const result = await fetchInstallCommand();
      const available = result.available && Boolean(result.command) && Boolean(result.expiresAtUtc);
      setInstallCommand(available ? result.command! : null);
      setInstallExpiresAtUtc(available ? result.expiresAtUtc! : null);
      setInstallClockNow(Date.now());
      setInstallStatus(available ? "ชุดติดตั้งพร้อมใช้งาน" : result.reason || "ชุดติดตั้งยังไม่พร้อม");
    } catch (error) {
      setInstallStatus(`ตรวจชุดติดตั้งไม่ได้: ${String(error)}`);
    } finally {
      setIsRefreshingInstallCommand(false);
    }
  }, []);
  useEffect(() => {
    if (!installExpiresAtUtc) return;
    const expiresAtMs = Date.parse(installExpiresAtUtc);
    const updateClock = () => setInstallClockNow(Date.now());
    if (!Number.isFinite(expiresAtMs)) {
      updateClock();
      return;
    }
    const timer = window.setTimeout(updateClock, Math.max(0, expiresAtMs - Date.now()));
    window.addEventListener("focus", updateClock);
    document.addEventListener("visibilitychange", updateClock);
    return () => {
      window.clearTimeout(timer);
      window.removeEventListener("focus", updateClock);
      document.removeEventListener("visibilitychange", updateClock);
    };
  }, [installExpiresAtUtc]);
  const installExpiry = installExpiresAtUtc
    ? getInstallCommandExpiryState(installExpiresAtUtc, installClockNow)
    : null;
  async function copyInstallCommand() {
    if (!installCommand || !installExpiresAtUtc) return;
    const expiry = getInstallCommandExpiryState(installExpiresAtUtc, Date.now());
    setInstallClockNow(Date.now());
    if (expiry.expired) {
      setCopyStatus("คำสั่งหมดอายุแล้ว กรุณาขอคำสั่งใหม่");
      return;
    }
    try {
      await navigator.clipboard.writeText(installCommand);
      setCopyStatus("คัดลอกคำสั่งแล้ว");
    } catch {
      setCopyStatus("คัดลอกอัตโนมัติไม่ได้ เลือกข้อความในช่องแล้วกด Ctrl+C");
    }
  }
  async function act(path: string, body: unknown, method = "POST") {
    try {
      setMessage("กำลังส่ง…");
      const result = await request(path, body, method);
      if (path === "/api/admin/trial-leases" || path.includes("/extend")) setTrialLease(result);
      await load();
      setMessage("บันทึกคำขอแล้ว ตรวจสถานะ Agent ก่อนใช้งาน");
      return true;
    } catch (e) {
      setMessage(String(e));
      return false;
    }
  }
  function invalidateVncTargetCheck(machineId: string) {
    vncTargetCheckRuns.current.set(
      machineId,
      (vncTargetCheckRuns.current.get(machineId) ?? 0) + 1,
    );
    setVncTargetCheckStatuses((current) => {
      if (!(machineId in current)) return current;
      const next = { ...current };
      delete next[machineId];
      return next;
    });
  }
  async function checkVncTarget(machineId: string) {
    const targetAddress = getVncTargetForExplicitSave(vncTargetDrafts[machineId]);
    if (!targetAddress) {
      setMessage("กรอก IPv4 LAN ส่วนตัวหรือ IPv6 ULA ที่ยืนยันกับเครื่องจริงก่อนตรวจสอบ");
      return;
    }

    const requestId = (vncTargetCheckRuns.current.get(machineId) ?? 0) + 1;
    vncTargetCheckRuns.current.set(machineId, requestId);
    setVncTargetCheckStatuses((current) => ({
      ...current,
      [machineId]: { targetAddress, checkedAt: null, state: "checking" },
    }));
    try {
      const result = (await request("/api/admin/vnc-target-check", {
        machineId,
        targetAddress,
      })) as { supported: boolean };
      if (vncTargetCheckRuns.current.get(machineId) !== requestId) return;
      setVncTargetCheckStatuses((current) => ({
        ...current,
        [machineId]: {
          targetAddress,
          checkedAt: Date.now(),
          state: result.supported ? "supported" : "unsupported",
        },
      }));
    } catch {
      if (vncTargetCheckRuns.current.get(machineId) !== requestId) return;
      setVncTargetCheckStatuses((current) => ({
        ...current,
        [machineId]: { targetAddress, checkedAt: null, state: "error" },
      }));
    }
  }
  return (
    <main className="mx-auto max-w-5xl space-y-5 p-5">
      <h1 className="text-3xl font-bold">เครื่องและ Agent</h1>
      <section className="space-y-2 rounded-xl border p-4">
        <h2 className="text-xl font-semibold">ติดตั้งเครื่องลูก</h2>
        <p role="status" aria-live="polite">
          {installCommand && installExpiry?.expired ? "คำสั่งติดตั้งหมดอายุแล้ว กรุณาขอคำสั่งใหม่" : installStatus}
        </p>
        <button
          type="button"
          className="rounded border p-2 disabled:opacity-50"
          disabled={isRefreshingInstallCommand}
          onClick={() => void refreshInstallCommand()}
        >
          {isRefreshingInstallCommand
            ? "กำลังขอคำสั่ง…"
            : installCommand
              ? "ขอคำสั่งติดตั้งใหม่"
              : "ขอคำสั่งติดตั้ง"}
        </button>
        {installCommand ? <>
          <ol className="list-inside list-decimal space-y-1 text-sm">
            <li>เปิด PowerShell แบบ Administrator บนเครื่องลูก</li>
            <li>คัดลอกคำสั่งด้านล่างไปรันบนเครื่องนั้น</li>
            <li>หลังลงทะเบียนแล้ว หน้าแอดมินจะเปิดขึ้นและเครื่องจะแสดงเป็น pending</li>
          </ol>
          <p>หมดอายุเมื่อ (UTC): {installExpiry?.displayUtc || "ไม่ทราบ"}</p>
          <textarea id="agent-install-command" className="w-full rounded border p-2 font-mono text-xs" rows={5} readOnly spellCheck={false} value={installCommand} aria-label="คำสั่งติดตั้ง Agent" />
          <button
            className="rounded border p-2 disabled:opacity-50"
            disabled={isRefreshingInstallCommand || !installExpiresAtUtc || installExpiry?.expired !== false}
            onClick={copyInstallCommand}
          >
            คัดลอกคำสั่ง PowerShell
          </button>
          <p role="status" aria-live="polite">{copyStatus}</p>
        </> : null}
      </section>
      <p>
        ระบบทดลอง ไม่มีการหักยอดเงิน ความพร้อมจริงยังต้องผ่านการตรวจสอบ Agent
      </p>
      <button className="rounded border p-2" onClick={load}>
        รีเฟรช
      </button>
      <p role="status">{message || loadStatus}</p>
      {!machines.length && !loadStatus ? <p>ยังไม่มีเครื่องลงทะเบียน</p> : null}
      {machines.some((m) => m.approvalStatus === "pending") ? (
        <p>เครื่องรอตั้งค่าและอนุมัติ: {machines.filter((m) => m.approvalStatus === "pending").length}</p>
      ) : null}
      {[...machines].sort((a, b) => Number(b.approvalStatus === "pending") - Number(a.approvalStatus === "pending")).map((m) => (
        <section
          key={`${m.machineId}-${m.configRevision}`}
          className="space-y-3 rounded-xl border p-4"
        >
          <h2 className="font-bold">
            {m.machineId} · {m.approvalStatus}
          </h2>
          <p>
            Agent: {m.agentVersion || "ยังไม่รายงาน"} · Readiness: {m.readiness} · Config revision: {m.configRevision} ·
            ACK: {m.appliedConfigRevision ?? "ยังไม่ยืนยัน"}{" "}
            {m.appliedConfigRevision === m.configRevision
              ? "(ตรงกับ config)"
              : "(รอ Agent ACK)"}
          </p>
          <p role="status">
            เซสชันผู้ดูแลที่กำลังดู VNC: {m.activeAdminVncSessions ?? 0}
          </p>
          <AgentUpdateControl machine={m} onUpdated={() => void load()} />
          <p className="break-all font-mono text-sm">
            Fingerprint: {m.publicKeyFingerprint || "ยังไม่มีข้อมูลยืนยัน"}
          </p>
          <p className="break-all font-mono text-sm">
            IP ที่ Agent รายงาน: {m.localIp}
          </p>
          <section
            className="space-y-2 rounded border p-3"
            aria-label="ผลตรวจวินิจฉัยจาก Agent"
          >
            <h3 className="font-semibold">ผลตรวจวินิจฉัยจาก Agent</h3>
            <p className="text-sm">
              ผลตรวจวินิจฉัยจาก Agent เท่านั้น ไม่ใช่การอนุมัติให้เช่า
            </p>
            {m.readinessDiagnostics?.length ? (
              <ul className="space-y-1 text-sm">
                {m.readinessDiagnostics.map((diagnostic) => (
                  <li
                    key={diagnostic.checkId}
                    className="flex flex-wrap justify-between gap-2"
                  >
                    <span>{READINESS_CHECK_LABELS[diagnostic.checkId]}</span>
                    <span>{READINESS_STATUS_LABELS[diagnostic.status]}</span>
                  </li>
                ))}
              </ul>
            ) : (
              <p role="status">
                {m.readinessDiagnostics
                  ? "รายงานนี้ไม่มีรายการตรวจวินิจฉัย"
                  : "Agent ยังไม่รายงานผลตรวจวินิจฉัย"}
              </p>
            )}
          </section>
          <form
            className="grid gap-3 sm:grid-cols-2"
            onSubmit={(e) => {
              e.preventDefault();
              const d = new FormData(e.currentTarget);
              const savedVncTarget = getSavedVncTargetForMetadataUpdate(m);
              void act(
                `/api/admin/machines/${encodeURIComponent(m.machineId)}`,
                {
                  displayName: d.get("name"),
                  hourlyPriceMinorUnits: Number(d.get("price")),
                  moonlightPort: Number(d.get("moon")),
                  sunshinePort: Number(d.get("sun")),
                  vncLocalIp: savedVncTarget,
                  expectedRevision: m.configRevision,
                },
                "PATCH",
              );
            }}
          >
            <label>
              ชื่อ
              <Input name="name" value={m.displayName} />
            </label>
            <label>
              ราคา (สตางค์/ชั่วโมง)
              <Input
                name="price"
                value={m.hourlyPriceMinorUnits}
                type="number"
              />
            </label>
            <label>
              Moonlight port
              <Input name="moon" value={m.moonlightPort} type="number" />
            </label>
            <label>
              Management port
              <Input name="sun" value={m.sunshinePort} type="number" />
            </label>
            <label>
              IP เป้าหมาย VNC (ตรวจสอบกับเครื่องจริงก่อนบันทึก)
              <Input
                name="vncIp"
                value={vncTargetDrafts[m.machineId] ?? ""}
                required={false}
                onChange={(event) => {
                  const value = event.currentTarget.value;
                  invalidateVncTargetCheck(m.machineId);
                  setVncTargetDrafts((current) =>
                    updateVncTargetDraft(current, m.machineId, value),
                  );
                }}
              />
            </label>
            <button className="rounded border p-2">บันทึก metadata</button>
            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                className="rounded border p-2 disabled:opacity-50"
                disabled={vncTargetCheckStatuses[m.machineId]?.state === "checking"}
                onClick={() => void checkVncTarget(m.machineId)}
              >
                {vncTargetCheckStatuses[m.machineId]?.state === "checking"
                  ? "กำลังตรวจสอบ VNC…"
                  : "ตรวจสอบ VNC"}
              </button>
              <button
                type="button"
                className="rounded border p-2"
                onClick={async () => {
                  const target = getVncTargetForExplicitSave(
                    vncTargetDrafts[m.machineId],
                  );
                  if (!target) {
                    setMessage("กรอก IPv4 LAN ส่วนตัวหรือ IPv6 ULA ที่ยืนยันกับเครื่องจริงก่อนบันทึก");
                    return;
                  }
                  const saved = await act(
                    `/api/admin/machines/${encodeURIComponent(m.machineId)}`,
                    {
                      displayName: m.displayName,
                      hourlyPriceMinorUnits: m.hourlyPriceMinorUnits,
                      moonlightPort: m.moonlightPort,
                      sunshinePort: m.sunshinePort,
                      vncLocalIp: target,
                      expectedRevision: m.configRevision,
                    },
                    "PATCH",
                  );
                  if (!saved) return;
                  setVncTargetDrafts((current) =>
                    updateVncTargetDraft(current, m.machineId, target),
                  );
                }}
              >
                บันทึก IP เป้าหมาย VNC ที่ยืนยันแล้ว
              </button>
            </div>
            {vncTargetCheckStatuses[m.machineId] ? (
              <p role="status" aria-live="polite" className="text-sm">
                {vncTargetCheckStatuses[m.machineId].state === "checking"
                  ? `กำลังตรวจ ${vncTargetCheckStatuses[m.machineId].targetAddress}:5900…`
                  : vncTargetCheckStatuses[m.machineId].state === "supported"
                    ? `พบ VNC ที่รองรับบน ${vncTargetCheckStatuses[m.machineId].targetAddress}:5900 · ตรวจเมื่อ ${new Date(vncTargetCheckStatuses[m.machineId].checkedAt!).toLocaleString()} ตามเวลาท้องถิ่น (ยังไม่ได้ยืนยันรหัสผ่าน)`
                    : vncTargetCheckStatuses[m.machineId].state === "unsupported"
                      ? `ไม่พบ VNC ที่รองรับบน ${vncTargetCheckStatuses[m.machineId].targetAddress}:5900`
                      : `ตรวจสอบ ${vncTargetCheckStatuses[m.machineId].targetAddress}:5900 ไม่สำเร็จ`}
              </p>
            ) : null}
          </form>
          {m.approvalStatus === "pending" ? (
            <form
              className="space-y-2"
              onSubmit={(e) => {
                e.preventDefault();
                const fingerprint = String(
                  new FormData(e.currentTarget).get("fingerprint"),
                );
                void act(
                  `/api/admin/machines/${encodeURIComponent(m.machineId)}/approve`,
                  { fingerprint },
                );
              }}
            >
              <label>
                พิมพ์ fingerprint ที่ตรวจสอบจากเครื่องจริง
                <Input name="fingerprint" value="" />
              </label>
              <button
                disabled={!m.publicKeyFingerprint}
                className="rounded border p-2 disabled:opacity-50"
              >
                อนุมัติ
              </button>
              <button
                type="button"
                className="ml-3 rounded border p-2"
                onClick={() =>
                  act(
                    `/api/admin/machines/${encodeURIComponent(m.machineId)}/reject`,
                    { fingerprint: m.publicKeyFingerprint },
                  )
                }
              >
                ปฏิเสธ pending
              </button>
            </form>
          ) : (
            <div className="flex flex-wrap gap-2">
              <button
                className="rounded border p-2"
                onClick={() =>
                  act(
                    `/api/admin/machines/${encodeURIComponent(m.machineId)}/wake`,
                    {},
                  )
                }
              >
                Wake-on-LAN
              </button>
              <AdminVncViewerButton machineId={m.machineId} />
            </div>
          )}
        </section>
      ))}
      <form
        className="grid gap-3 rounded border p-4"
        onSubmit={(e) => {
          e.preventDefault();
          const d = new FormData(e.currentTarget);
          void act("/api/admin/trial-leases", {
            userId: d.get("user"),
            machineId: d.get("machine"),
            durationSeconds: Number(d.get("duration")),
            idempotencyKey: crypto.randomUUID(),
          });
        }}
      >
        <h2>ให้สิทธิ์ทดลอง (ไม่หักเงิน)</h2>
        <label>
          User ID
          <Input name="user" value="" />
        </label>
        <label>
          Machine ID
          <Input name="machine" value="" />
        </label>
        <label>
          ระยะเวลา (วินาที 1–86400)
          <Input name="duration" type="number" value={600} />
        </label>
        <button className="rounded border p-2">สร้างสิทธิ์ทดลอง</button>
      </form>
      {trialLease && <p role="status">Rental ID: {trialLease.id} · Revision: {trialLease.revision}{trialLease.pendingRevision !== null ? ` · รอ ACK ${trialLease.pendingRevision}` : ""}</p>}
      <form
        className="grid gap-3 rounded border p-4"
        onSubmit={(e) => {
          e.preventDefault();
          const d = new FormData(e.currentTarget);
          void act(
            `/api/admin/leases/${encodeURIComponent(String(d.get("rental")))}/extend`,
            {
              additionalSeconds: Number(d.get("duration")),
              expectedRevision: Number(d.get("revision")),
              idempotencyKey: crypto.randomUUID(),
            },
          );
        }}
      >
        <h2>ต่อเวลาทดลอง</h2>
        <label>
          Rental ID
          <Input name="rental" value="" />
        </label>
        <label>
          Revision
          <Input name="revision" type="number" value={1} />
        </label>
        <label>
          เพิ่ม (วินาที)
          <Input name="duration" type="number" value={600} />
        </label>
        <button className="rounded border p-2">ขอต่อเวลาทดลอง</button>
      </form>
    </main>
  );
}
function Input({
  name,
  value,
  type = "text",
  onChange,
  required = true,
}: {
  name: string;
  value: string | number;
  type?: string;
  onChange?: ChangeEventHandler<HTMLInputElement>;
  required?: boolean;
}) {
  return (
    <input
      className="mt-1 block w-full rounded border p-2"
      name={name}
      {...(onChange ? { value, onChange } : { defaultValue: value })}
      type={type}
      required={required}
    />
  );
}
