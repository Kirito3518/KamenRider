"use client";

import { useCallback, useEffect, useState, useSyncExternalStore } from "react";
import { Bot, Loader2, ShieldAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { FormSkeleton, LoadingRegion } from "@/components/loading";
import { isAgentOnline, updateStatusLabels, type AgentRelease } from "@/lib/agent-releases";
import type { Machine } from "@/lib/machine-contracts";

type CommandResult = { available: boolean; command?: string; expiresAtUtc?: string; operationId?: string; targetVersion?: string; reason?: string };
const terminal = new Set(["completed", "rolledBack", "expired"]);
const recoverable = new Set(["installing", "awaitingHeartbeat", "rollingBack", "failed"]);

type RecoveryContext = {
  machineId: string;
  chainId: string;
  currentHeadOperationId: string;
  currentGeneration: number;
  currentStatus: string;
  currentTargetVersion: string;
  held: true;
  allocationHeld: boolean;
};

type RecoveryIntent = {
  intentId: string;
  machineId: string;
  chainId: string;
  previousHeadOperationId: string;
  expectedGeneration: number;
  generation: number;
  targetReleaseVersion: string;
  targetVersion: string;
  targetRuntime: string;
  reason: string;
  requestId: string;
  status: string;
  held: true;
  createdAtUtc: string;
  expiresAtUtc: string;
};

type RecoveryIntentConfirmation = Pick<RecoveryIntent, "intentId" | "machineId" | "requestId" | "status" | "held" | "expiresAtUtc">;

type RecoveryIntentRequest = {
  expectedChainId: string;
  expectedHeadId: string;
  expectedGeneration: number;
  targetReleaseVersion: string;
  reason: string;
  requestId: string;
};

function recoveryAttemptStorageKey(actorId: string, machineId: string) {
  return `agent-recovery-attempt:v1:${encodeURIComponent(actorId)}:${encodeURIComponent(machineId)}`;
}

function recoveryConfirmationStorageKey(actorId: string, machineId: string) {
  return `agent-recovery-confirmation:v1:${encodeURIComponent(actorId)}:${encodeURIComponent(machineId)}`;
}

const recoveryAttemptServerSnapshot = "\u0000server-snapshot";
const recoveryAttemptStorageUnavailable = "\u0000storage-unavailable";
const recoveryAttemptListeners = new Map<string, Set<() => void>>();

function recoveryAttemptSnapshot(storageKey: string) {
  try {
    return window.sessionStorage.getItem(storageKey);
  } catch {
    return recoveryAttemptStorageUnavailable;
  }
}

function subscribeRecoveryAttempt(storageKey: string, listener: () => void) {
  const listeners = recoveryAttemptListeners.get(storageKey) ?? new Set<() => void>();
  listeners.add(listener);
  recoveryAttemptListeners.set(storageKey, listeners);
  return () => {
    listeners.delete(listener);
    if (!listeners.size) recoveryAttemptListeners.delete(storageKey);
  };
}

function notifyRecoveryAttempt(storageKey: string) {
  recoveryAttemptListeners.get(storageKey)?.forEach((listener) => listener());
}

function recoveryAttemptFromStorage(value: unknown): RecoveryIntentRequest | null {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return null;
  const item = value as Record<string, unknown>;
  const fields = ["expectedChainId", "expectedHeadId", "expectedGeneration", "targetReleaseVersion", "reason", "requestId"];
  if (Object.keys(item).length !== fields.length || fields.some((field) => !Object.hasOwn(item, field))) return null;
  const identifiers = [item.expectedChainId, item.expectedHeadId, item.targetReleaseVersion, item.requestId];
  if (identifiers.some((entry) => typeof entry !== "string" || !entry.trim() || entry.length > 128 || /[\u0000-\u001f\u007f]/.test(entry))) return null;
  if (!Number.isSafeInteger(item.expectedGeneration) || Number(item.expectedGeneration) <= 0) return null;
  if (typeof item.reason !== "string" || !item.reason.trim() || item.reason.length > 512 || /[\u0000-\u001f\u007f]/.test(item.reason)) return null;
  return {
    expectedChainId: item.expectedChainId as string,
    expectedHeadId: item.expectedHeadId as string,
    expectedGeneration: Number(item.expectedGeneration),
    targetReleaseVersion: item.targetReleaseVersion as string,
    reason: item.reason,
    requestId: item.requestId as string,
  };
}

function recoveryAttemptFromSnapshot(snapshot: string | null): RecoveryIntentRequest | null {
  if (!snapshot || snapshot === recoveryAttemptServerSnapshot || snapshot === recoveryAttemptStorageUnavailable) return null;
  try {
    return recoveryAttemptFromStorage(JSON.parse(snapshot) as unknown);
  } catch {
    return null;
  }
}

function writeRecoveryAttempt(storageKey: string, attempt: RecoveryIntentRequest) {
  try {
    window.sessionStorage.setItem(storageKey, JSON.stringify(attempt));
    notifyRecoveryAttempt(storageKey);
    return true;
  } catch {
    return false;
  }
}

function clearRecoveryAttempt(storageKey: string) {
  try {
    window.sessionStorage.removeItem(storageKey);
    notifyRecoveryAttempt(storageKey);
    return true;
  } catch {
    return false;
  }
}

function recoveryConfirmationFromStorage(value: unknown, machineId: string): RecoveryIntentConfirmation | null {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return null;
  const item = value as Record<string, unknown>;
  const fields = ["intentId", "machineId", "requestId", "status", "held", "expiresAtUtc"];
  if (Object.keys(item).length !== fields.length || fields.some((field) => !Object.hasOwn(item, field))) return null;
  const identifiers = [item.intentId, item.machineId, item.requestId, item.status];
  if (identifiers.some((entry) => typeof entry !== "string" || !entry.trim() || entry.length > 128 || /[\u0000-\u001f\u007f]/.test(entry))) return null;
  if (item.machineId !== machineId || item.held !== true || typeof item.expiresAtUtc !== "string" || !Number.isFinite(Date.parse(item.expiresAtUtc))) return null;
  return {
    intentId: item.intentId as string,
    machineId,
    requestId: item.requestId as string,
    status: item.status as string,
    held: true,
    expiresAtUtc: item.expiresAtUtc,
  };
}

function recoveryConfirmationFromSnapshot(snapshot: string | null, machineId: string): RecoveryIntentConfirmation | null {
  if (!snapshot || snapshot === recoveryAttemptServerSnapshot || snapshot === recoveryAttemptStorageUnavailable) return null;
  try {
    return recoveryConfirmationFromStorage(JSON.parse(snapshot) as unknown, machineId);
  } catch {
    return null;
  }
}

function writeRecoveryConfirmation(storageKey: string, intent: RecoveryIntentConfirmation) {
  try {
    const confirmation: RecoveryIntentConfirmation = {
      intentId: intent.intentId,
      machineId: intent.machineId,
      requestId: intent.requestId,
      status: intent.status,
      held: true,
      expiresAtUtc: intent.expiresAtUtc,
    };
    window.sessionStorage.setItem(storageKey, JSON.stringify(confirmation));
    notifyRecoveryAttempt(storageKey);
    return true;
  } catch {
    return false;
  }
}

function clearRecoveryConfirmation(storageKey: string) {
  try {
    window.sessionStorage.removeItem(storageKey);
    notifyRecoveryAttempt(storageKey);
    return true;
  } catch {
    return false;
  }
}

function recoveryContextFromResponse(value: unknown, machineId: string): RecoveryContext | null {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return null;
  const envelope = value as Record<string, unknown>;
  if (envelope.available !== true || typeof envelope.context !== "object" || envelope.context === null || Array.isArray(envelope.context)) return null;
  const context = envelope.context as Record<string, unknown>;
  if (
    context.machineId !== machineId || context.held !== true || typeof context.allocationHeld !== "boolean" ||
    typeof context.chainId !== "string" || !context.chainId.trim() ||
    typeof context.currentHeadOperationId !== "string" || !context.currentHeadOperationId.trim() ||
    !Number.isSafeInteger(context.currentGeneration) || Number(context.currentGeneration) <= 0 ||
    typeof context.currentStatus !== "string" || !context.currentStatus.trim() ||
    typeof context.currentTargetVersion !== "string" || !context.currentTargetVersion.trim()
  ) return null;
  return {
    machineId,
    chainId: context.chainId,
    currentHeadOperationId: context.currentHeadOperationId,
    currentGeneration: Number(context.currentGeneration),
    currentStatus: context.currentStatus,
    currentTargetVersion: context.currentTargetVersion,
    held: true,
    allocationHeld: context.allocationHeld,
  };
}

function recoveryIntentFromResponse(value: unknown, request: RecoveryIntentRequest, machineId: string): RecoveryIntent | null {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return null;
  const envelope = value as Record<string, unknown>;
  if (envelope.available !== true || typeof envelope.intent !== "object" || envelope.intent === null || Array.isArray(envelope.intent)) return null;
  const item = envelope.intent as Record<string, unknown>;
  const stringFields = ["intentId", "machineId", "chainId", "previousHeadOperationId", "targetReleaseVersion", "targetVersion", "targetRuntime", "reason", "requestId", "status", "createdAtUtc", "expiresAtUtc"] as const;
  if (
    stringFields.some((field) => typeof item[field] !== "string" || !String(item[field]).trim()) ||
    item.machineId !== machineId || item.chainId !== request.expectedChainId ||
    item.previousHeadOperationId !== request.expectedHeadId || item.expectedGeneration !== request.expectedGeneration ||
    item.targetReleaseVersion !== request.targetReleaseVersion || item.reason !== request.reason ||
    item.requestId !== request.requestId || item.generation !== request.expectedGeneration + 1 || item.held !== true ||
    !Number.isFinite(Date.parse(String(item.createdAtUtc))) || !Number.isFinite(Date.parse(String(item.expiresAtUtc)))
  ) return null;
  return {
    intentId: item.intentId as string,
    machineId: item.machineId as string,
    chainId: item.chainId as string,
    previousHeadOperationId: item.previousHeadOperationId as string,
    expectedGeneration: item.expectedGeneration as number,
    generation: item.generation as number,
    targetReleaseVersion: item.targetReleaseVersion as string,
    targetVersion: item.targetVersion as string,
    targetRuntime: item.targetRuntime as string,
    reason: item.reason as string,
    requestId: item.requestId as string,
    status: item.status as string,
    held: true,
    createdAtUtc: item.createdAtUtc as string,
    expiresAtUtc: item.expiresAtUtc as string,
  };
}

function errorMessage(value: unknown, fallback: string) {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return fallback;
  const error = (value as Record<string, unknown>).error;
  return typeof error === "string" && error.trim() && error.length <= 256 ? error : fallback;
}

function contextMatchesMachine(context: RecoveryContext, machine: Machine) {
  return context.currentHeadOperationId === machine.agentUpdateOperationId &&
    context.currentStatus === machine.agentUpdateStatus &&
    !(machine.allocationHeld === true && context.allocationHeld === false);
}

function recoveryIntentUrl(machineId: string, requestId: string) {
  return `/api/admin/machines/${encodeURIComponent(machineId)}/agent-update-recovery-intents?${new URLSearchParams({ requestId })}`;
}

function AgentRecoveryIntentControl({ machine, actorId, onUpdated }: { machine: Machine; actorId: string; onUpdated?: () => boolean | Promise<boolean> }) {
  const [open, setOpen] = useState(false);
  const [context, setContext] = useState<RecoveryContext | null>(null);
  const [releases, setReleases] = useState<AgentRelease[] | null>(null);
  const [targetReleaseVersion, setTargetReleaseVersion] = useState("");
  const [reason, setReason] = useState("");
  const [intent, setIntent] = useState<RecoveryIntent | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [now, setNow] = useState(0);
  const [attemptDismissed, setAttemptDismissed] = useState(false);
  const attemptStorageKey = recoveryAttemptStorageKey(actorId, machine.machineId);
  const confirmationStorageKey = recoveryConfirmationStorageKey(actorId, machine.machineId);
  const subscribeToAttempt = useCallback((listener: () => void) => subscribeRecoveryAttempt(attemptStorageKey, listener), [attemptStorageKey]);
  const getAttemptSnapshot = useCallback(() => recoveryAttemptSnapshot(attemptStorageKey), [attemptStorageKey]);
  const attemptSnapshot = useSyncExternalStore(subscribeToAttempt, getAttemptSnapshot, () => recoveryAttemptServerSnapshot);
  const subscribeToConfirmation = useCallback((listener: () => void) => subscribeRecoveryAttempt(confirmationStorageKey, listener), [confirmationStorageKey]);
  const getConfirmationSnapshot = useCallback(() => recoveryAttemptSnapshot(confirmationStorageKey), [confirmationStorageKey]);
  const confirmationSnapshot = useSyncExternalStore(subscribeToConfirmation, getConfirmationSnapshot, () => recoveryAttemptServerSnapshot);
  const storageReady = attemptSnapshot !== recoveryAttemptServerSnapshot && confirmationSnapshot !== recoveryAttemptServerSnapshot;
  const storageAvailable = attemptSnapshot !== recoveryAttemptStorageUnavailable && confirmationSnapshot !== recoveryAttemptStorageUnavailable;
  const attempt = storageAvailable ? recoveryAttemptFromSnapshot(attemptSnapshot) : null;
  const storedConfirmation = storageAvailable ? recoveryConfirmationFromSnapshot(confirmationSnapshot, machine.machineId) : null;
  const invalidStoredAttempt = storageReady && storageAvailable && attemptSnapshot !== null && attempt === null;
  const invalidStoredConfirmation = storageReady && storageAvailable && confirmationSnapshot !== null && storedConfirmation === null;
  const canTrackAttempt = storageReady && storageAvailable && !invalidStoredAttempt && !invalidStoredConfirmation;
  const recoveryPanelOpen = open || Boolean(attempt && !attemptDismissed);
  const canRequest = Boolean(machine.agentUpdateOperationId && recoverable.has(machine.agentUpdateStatus || "") && machine.allocationHeld === false);
  const contextStale = Boolean(context && !contextMatchesMachine(context, machine));
  const confirmedIntent = intent ?? storedConfirmation;
  const confirmedIntentExpiry = confirmedIntent?.expiresAtUtc;
  const expired = Boolean(confirmedIntent && Date.parse(confirmedIntent.expiresAtUtc) <= now);
  const intentAwaitingInspection = confirmedIntent?.held === true && confirmedIntent.status === "awaitingInspection";
  const confirmedIntentActive = Boolean(intentAwaitingInspection && !expired);

  useEffect(() => {
    if (!confirmedIntentExpiry) return;
    const update = () => setNow(Date.now());
    update();
    const timer = window.setInterval(update, 5_000);
    window.addEventListener("focus", update);
    document.addEventListener("visibilitychange", update);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener("focus", update);
      document.removeEventListener("visibilitychange", update);
    };
  }, [confirmedIntentExpiry]);

  async function loadCurrentContext() {
    const path = `/api/admin/machines/${encodeURIComponent(machine.machineId)}/agent-update-recovery-context`;
    const [contextResponse, releaseResponse] = await Promise.all([
      fetch(path, { cache: "no-store" }),
      fetch("/api/admin/agent-releases", { cache: "no-store" }),
    ]);
    const [contextBody, releaseBody] = await Promise.all([
      contextResponse.json().catch(() => null) as Promise<unknown>,
      releaseResponse.json().catch(() => null) as Promise<unknown>,
    ]);
    if (!contextResponse.ok) throw new Error(errorMessage(contextBody, "Recovery context is unavailable."));
    if (!releaseResponse.ok || !Array.isArray(releaseBody)) throw new Error("Could not load target releases for recovery.");
    const nextContext = recoveryContextFromResponse(contextBody, machine.machineId);
    if (!nextContext) throw new Error("Controller did not return a valid held recovery context.");
    if (!contextMatchesMachine(nextContext, machine)) {
      setContext(null);
      setIntent(null);
      clearRecoveryConfirmation(confirmationStorageKey);
      setMessage("หัว operation เปลี่ยนแล้ว โปรดรีเฟรชข้อมูลเครื่องก่อนเปิดคำขอ recovery ใหม่");
      return;
    }
    setContext(nextContext);
    setReleases(releaseBody as AgentRelease[]);
    setOpen(true);
    setTargetReleaseVersion("");
    setIntent(null);
    clearRecoveryConfirmation(confirmationStorageKey);
    setNow(Date.now());
    setMessage(nextContext.allocationHeld ? "เครื่องยังมี allocation hold จึงยังขอ recovery intent ไม่ได้" : "");
  }

  function showIntent(value: unknown, request: RecoveryIntentRequest) {
    const received = recoveryIntentFromResponse(value, request, machine.machineId);
    if (!received) {
      setMessage("Controller response did not match this held chain and request. No successor or update is ready.");
      return false;
    }
    setContext(null);
    setOpen(false);
    setIntent(received);
    if (writeRecoveryConfirmation(confirmationStorageKey, received)) clearRecoveryAttempt(attemptStorageKey);
    setNow(Date.now());
    const expired = Date.parse(received.expiresAtUtc) <= Date.now();
    setMessage(expired
      ? "Recovery intent expired. The maintenance chain remains held; no successor or update is ready. Refresh the held context before requesting another intent."
      : "Recovery intent recorded and still held. It does not authorize a successor operation or make an update ready.");
    return true;
  }

  async function reconcile(request: RecoveryIntentRequest) {
    try {
      const response = await fetch(recoveryIntentUrl(machine.machineId, request.requestId), { cache: "no-store" });
      const body = await response.json().catch(() => null) as unknown;
      if (response.ok && showIntent(body, request)) return;
      if (response.status === 404) {
        setMessage("No intent was found for this request ID. The held chain is unchanged; retry the exact same request or check again.");
        return;
      }
      setMessage(errorMessage(body, "Could not reconcile this request. The chain remains held; check the same request ID again."));
    } catch {
      setMessage("Could not reconcile this request. The chain remains held; check the same request ID again.");
    }
  }

  async function refreshMachine(): Promise<boolean> {
    if (!onUpdated) return false;
    try { return await onUpdated(); }
    catch { return false; }
  }

  async function submit(request: RecoveryIntentRequest) {
    const storedAttempt = canTrackAttempt ? recoveryAttemptFromSnapshot(recoveryAttemptSnapshot(attemptStorageKey)) : null;
    if (!storedAttempt || JSON.stringify(storedAttempt) !== JSON.stringify(request)) {
      setMessage("The exact unresolved request could not be verified in this tab. No new request was sent.");
      return;
    }
    setBusy(true);
    setMessage("");
    setIntent(null);
    try {
      const response = await fetch(`/api/admin/machines/${encodeURIComponent(machine.machineId)}/agent-update-recovery-intents`, {
        method: "POST",
        cache: "no-store",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
      });
      const body = await response.json().catch(() => null) as unknown;
      if (response.ok) {
        showIntent(body, request);
        return;
      }
      const errorBody = typeof body === "object" && body !== null && !Array.isArray(body) ? body as Record<string, unknown> : {};
      if (response.status === 409) {
        const head = typeof errorBody.currentHead === "object" && errorBody.currentHead !== null
          ? errorBody.currentHead as Record<string, unknown>
          : null;
        setContext(null);
        clearRecoveryAttempt(attemptStorageKey);
        setIntent(null);
        clearRecoveryConfirmation(confirmationStorageKey);
        setOpen(false);
        const refreshed = await refreshMachine();
        const latest = refreshed
          ? "Latest machine data was refreshed. Read the held context again before creating another request."
          : "Refresh the machine data and read the held context before creating another request.";
        setMessage(head
          ? `Stale recovery request rejected (chain ${String(head.currentChainId || "unknown")}, head ${String(head.currentHeadOperationId || "unknown")}, generation ${String(head.currentGeneration || "unknown")}). No intent was accepted. ${latest}`
          : `Recovery request rejected with HTTP 409. No intent was accepted and it was not retried automatically. ${latest}`);
        return;
      }
      if (response.status >= 500) {
        await reconcile(request);
        return;
      }
      setMessage(errorMessage(body, "Recovery intent request was rejected. The chain remains held and no update is ready."));
    } catch {
      await reconcile(request);
    } finally {
      setBusy(false);
    }
  }

  function createIntent() {
    if (!canTrackAttempt || attempt || confirmedIntentActive || !context || !releases || !targetReleaseVersion || !reason.trim() || reason.length > 512 || /[\u0000-\u001f\u007f]/.test(reason) || busy || context.allocationHeld || machine.allocationHeld !== false) return;
    const request: RecoveryIntentRequest = {
      expectedChainId: context.chainId,
      expectedHeadId: context.currentHeadOperationId,
      expectedGeneration: context.currentGeneration,
      targetReleaseVersion,
      reason,
      requestId: crypto.randomUUID(),
    };
    if (!writeRecoveryAttempt(attemptStorageKey, request)) {
      setMessage("This tab could not save the exact recovery request. No intent was submitted; enable session storage and reload before trying again.");
      return;
    }
    void submit(request);
  }

  const choices = releases ?? [];

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-start gap-2">
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={busy || !canTrackAttempt || confirmedIntentActive || (!attempt && !canRequest)}
          onClick={() => {
            if (attempt) { setAttemptDismissed(false); setOpen(true); return; }
            setMessage(""); setContext(null); setOpen(false); setBusy(true);
            void loadCurrentContext().catch((error: unknown) => {
              setOpen(false);
              setMessage(error instanceof Error ? error.message : "Recovery context is unavailable.");
            }).finally(() => setBusy(false));
          }}
        >
          {busy && !open ? (
            <Loader2
              aria-hidden="true"
              data-icon="inline-start"
              className="animate-spin motion-reduce:animate-none"
            />
          ) : null}
          {busy && !open ? "กำลังตรวจ held chain…" : confirmedIntentActive ? "Recovery intent ยัง active" : attempt ? "ตรวจสอบ recovery request เดิม" : confirmedIntent ? "ตรวจสอบ held context ใหม่" : "ขอ recovery intent"}
        </Button>
        <p className="max-w-xl text-sm text-muted-foreground">Recovery intent จะคง hold ไว้ ไม่ออกคำสั่ง successor และยังไม่ทำให้ update พร้อม</p>
      </div>
      {contextStale && open ? <p role="status" className="text-sm text-status-busy">หัว operation เปลี่ยนแล้ว ฟอร์ม recovery เดิมถูกปิด กรุณาอ่านสถานะปัจจุบันก่อนขอใหม่</p> : null}
      {open && context && !contextStale && !attempt && !confirmedIntentActive ? (
        <div className="flex flex-col gap-3 rounded-2xl bg-muted/50 p-4">
          <p className="text-sm">Controller ยืนยัน chain {context.chainId} · head {context.currentHeadOperationId} · generation {context.currentGeneration} · {context.currentStatus} · เป้าหมายเดิม {context.currentTargetVersion}</p>
          <p className="text-sm">Recovery intent จะคง hold ไว้ ไม่ออกคำสั่ง successor และยังไม่ทำให้ update พร้อม</p>
          {context.allocationHeld ? <p role="status" className="text-sm">มี allocation hold จึงสร้าง recovery intent ไม่ได้</p> : null}
          {!context.allocationHeld && !intent ? (
            <>
              <label className="flex flex-col gap-1 text-sm">
                เลือก target release สำหรับ {machine.machineId}
                <select aria-label={`เลือก target release สำหรับ ${machine.machineId}`} className="h-9 rounded-xl border border-transparent bg-background px-3 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/30" value={targetReleaseVersion} onChange={(event) => setTargetReleaseVersion(event.target.value)}>
                  <option value="">เลือก release</option>
                  {choices.map((release) => <option key={release.version} value={release.version}>{release.version} · {release.channel}</option>)}
                </select>
              </label>
              <label className="flex flex-col gap-1 text-sm">
                เหตุผล recovery
                <textarea aria-label="เหตุผล recovery" className="min-h-20 rounded-xl border border-transparent bg-background px-3 py-2 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/30" maxLength={512} required rows={3} value={reason} onChange={(event) => setReason(event.target.value)} />
              </label>
              <p className="text-sm text-muted-foreground">Controller ตรวจสอบความเข้ากันได้ของ release กับ held chain ก่อนรับ intent</p>
              <Button type="button" size="sm" className="self-start" disabled={!targetReleaseVersion || !reason.trim() || reason.length > 512 || /[\u0000-\u001f\u007f]/.test(reason) || busy} onClick={createIntent}>{busy ? <Loader2 aria-hidden="true" data-icon="inline-start" className="animate-spin motion-reduce:animate-none" /> : null}{busy ? "กำลังบันทึก intent…" : "สร้าง recovery intent"}</Button>
            </>
          ) : null}
        </div>
      ) : null}
      {confirmedIntent ? (
        <div className="flex flex-col gap-1 rounded-2xl bg-muted/50 p-4 text-sm" role="status">
          <p>Intent {confirmedIntent.intentId} · request {confirmedIntent.requestId} · {expired || confirmedIntent.status === "expired" ? "หมดอายุ" : confirmedIntent.status}</p>
          <p>หมดอายุ (UTC): {confirmedIntent.expiresAtUtc}</p>
          <p>{confirmedIntent.status === "superseded"
            ? "Intent นี้ถูกแทนที่แล้ว โปรดโหลด held context ปัจจุบันก่อนสร้างคำขอใหม่; chain ยัง held และยังไม่มี successor/update พร้อม"
            : expired || confirmedIntent.status === "expired"
              ? "Intent หมดอายุแล้ว แต่ chain ยัง held และยังไม่มี successor/update พร้อม"
              : confirmedIntentActive
                ? "Recovery intent กำลังรอตรวจสอบ; คำขอใหม่ถูกล็อกจนกว่าจะหมดอายุ. Chain ยังคง held; successor operation และ update readiness ยังไม่เสร็จ"
                : "Intent ไม่ได้รอตรวจสอบแล้ว โปรดโหลด held context ปัจจุบันก่อนสร้างคำขอใหม่; chain ยัง held และยังไม่มี successor/update พร้อม"}</p>
        </div>
      ) : null}
      {recoveryPanelOpen && attempt && !confirmedIntentActive ? (
        <div className="flex flex-col gap-2 rounded-2xl bg-muted/50 p-4">
          <p className="text-sm">Request ID: {attempt.requestId}</p>
          <p className="text-sm">Target release: {attempt.targetReleaseVersion} · chain {attempt.expectedChainId} · head {attempt.expectedHeadId} · generation {attempt.expectedGeneration}</p>
          <p className="text-sm">This exact request remains unresolved. A missing GET result or temporary Controller error does not unlock a new request.</p>
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => {
              setBusy(true);
              void reconcile(attempt).finally(() => setBusy(false));
            }}>ตรวจสอบ request ID</Button>
            <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => void submit(attempt)}>ลอง request เดิมซ้ำ</Button>
            <Button type="button" variant="ghost" size="sm" disabled={busy} onClick={() => { setOpen(false); setAttemptDismissed(true); setContext(null); setIntent(null); }}>ปิด</Button>
          </div>
        </div>
      ) : null}
      {!storageAvailable ? <p role="status" className="text-sm">This tab cannot preserve a recovery request safely. Enable session storage before requesting an intent; no request was sent.</p> : null}
      {invalidStoredAttempt ? <p role="status" className="text-sm">A saved recovery request could not be read. No new request will be sent from this tab.</p> : null}
      {invalidStoredConfirmation ? <p role="status" className="text-sm">A saved recovery confirmation could not be read. No new request will be sent from this tab.</p> : null}
      {storageReady && storageAvailable && !invalidStoredAttempt ? <p role="status" aria-live="polite" className="text-sm">{message}</p> : null}
    </div>
  );
}

export function AgentUpdateControl({ machine, actorId, onUpdated }: { machine: Machine; actorId: string; onUpdated?: () => boolean | Promise<boolean> }) {
  const [open, setOpen] = useState(false);
  const [releases, setReleases] = useState<AgentRelease[] | null>(null);
  const [selected, setSelected] = useState("");
  const [command, setCommand] = useState<CommandResult | null>(null);
  const [pendingOperationId, setPendingOperationId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [clock, setClock] = useState(0);
  // A matching snapshot acknowledges this reservation once; later operations stay authoritative.
  const matchingSnapshot = Boolean(pendingOperationId && pendingOperationId === machine.agentUpdateOperationId && machine.agentUpdateStatus);
  if (matchingSnapshot) setPendingOperationId(null);
  if (!pendingOperationId && command && machine.agentUpdateOperationId && command.operationId !== machine.agentUpdateOperationId) setCommand(null);
  const awaitingSnapshot = Boolean(pendingOperationId && !matchingSnapshot);
  const status = awaitingSnapshot ? "awaitingInstaller" : machine.agentUpdateStatus;
  const targetVersion = awaitingSnapshot ? command?.targetVersion : machine.agentUpdateTargetVersion;
  const locked = Boolean(status && !terminal.has(status));
  const leaseClear = machine.allocationHeld === false;
  const canStart = leaseClear && !locked && Boolean(machine.agentVersion) && isAgentOnline(machine, clock);
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

  async function requestCommand(body: { releaseVersion: string }) {
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
      setClock(Date.now()); setCommand(result); setPendingOperationId(result.operationId); setOpen(false); await refreshMachine();
    } catch { setMessage("สร้างคำสั่งไม่สำเร็จ สถานะเครื่องหรือ lease อาจเปลี่ยนแล้ว กรุณารีเฟรชและตรวจสอบอีกครั้ง"); await refreshMachine(); }
    finally { setBusy(false); }
  }

  async function copy() {
    if (!command?.command || !Number.isFinite(expires) || Date.now() >= expires) { setClock(Date.now()); setMessage("คำสั่งหมดอายุแล้ว"); return; }
    try { await navigator.clipboard.writeText(command.command); setMessage("คัดลอกคำสั่งแล้ว"); }
    catch { setMessage("คัดลอกอัตโนมัติไม่ได้ เลือกข้อความในช่องแล้วกด Ctrl+C"); }
  }

  const choices = releases?.filter((release) => release.version !== machine.agentVersion) ?? [];
  return (
    <section className="flex flex-col gap-3 rounded-2xl border bg-card p-4" aria-label={`อัปเดต Agent ${machine.machineId}`}>
      <h3 className="flex items-center gap-2 font-heading text-base font-medium">
        <span className="flex size-8 items-center justify-center rounded-xl bg-muted text-muted-foreground">
          <Bot aria-hidden className="size-4" />
        </span>
        Update Agent
      </h3>
      <p className="text-sm">สถานะอัปเดต: {status ? updateStatusLabels[status] || status : "ยังไม่มีการอัปเดต"}{targetVersion ? ` · เป้าหมาย ${targetVersion}` : ""}</p>
      <p className="text-sm text-muted-foreground">ผลอัปเดตแยกจากการอนุมัติและความพร้อมให้เช่า</p>
      {!leaseClear ? <p className="text-sm text-status-busy">{machine.allocationHeld ? "เครื่องมี lease อยู่ จึงอัปเดตไม่ได้" : "ไม่ทราบสถานะ lease กรุณารีเฟรชข้อมูลเครื่อง"}</p> : null}
      {leaseClear && !locked && (!machine.agentVersion || !isAgentOnline(machine, clock)) ? <p className="text-sm text-status-busy">ต้องมี heartbeat ล่าสุดพร้อมเวอร์ชัน Agent ภายใน 20 วินาที</p> : null}
      {status === "rollingBack" ? (
        <p className="flex items-start gap-2 rounded-2xl bg-status-busy/10 p-3 text-sm text-status-busy">
          <ShieldAlert aria-hidden className="mt-0.5 size-4 shrink-0" />
          <span>หาก Agent รุ่นเดิมไม่รองรับการยืนยันหลัง rollback เครื่องจะยังถูกล็อก ใช้กู้คืนการอัปเดตเดิมเพื่อติดตั้งเป้าหมายเดิมที่รองรับการยืนยัน หากเป้าหมายเดิมเป็น release เก่าที่ไม่รองรับ ให้หยุดและวางแผนกู้คืนกับผู้ดูแล</span>
        </p>
      ) : null}
      <div className="flex flex-col gap-2">
        <Button
          type="button"
          variant="outline"
          size="sm"
          className="self-start"
          disabled={!canStart || busy || Boolean(command && !expired && !terminal.has(status || ""))}
          onClick={() => void chooseRelease()}
        >
          Update Agent
        </Button>
        <AgentRecoveryIntentControl key={`${actorId}:${machine.machineId}`} machine={machine} actorId={actorId} onUpdated={onUpdated} />
      </div>
          {open ? (
        <div className="flex flex-col gap-3 rounded-2xl bg-muted/50 p-4">
          {busy && !releases ? (
            <LoadingRegion label="กำลังโหลดรายการ release">
              <FormSkeleton
                fields={2}
                withActions={false}
                maxWidth="max-w-full"
                className="gap-3"
              />
            </LoadingRegion>
          ) : null}
          {releases ? (
            choices.length ? (
              <>
                <label className="flex flex-col gap-1 text-sm">
                  เลือก release สำหรับ {machine.machineId}
                  <select className="h-9 rounded-xl border border-transparent bg-background px-3 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/30" value={selected} onChange={(event) => setSelected(event.target.value)}>
                    <option value="">เลือก release</option>
                    {choices.map((release) => <option key={release.version} value={release.version}>{release.version} · {release.channel}</option>)}
                  </select>
                </label>
                <Button type="button" size="sm" className="self-start" disabled={!selected || busy || !canStart} onClick={() => void requestCommand({ releaseVersion: selected })}>สร้างคำสั่งอัปเดต</Button>
              </>
            ) : (
              <p className="text-sm">ไม่มี release ที่ต่างจากเวอร์ชันปัจจุบัน</p>
            )
          ) : null}
          <Button type="button" variant="ghost" size="sm" className="self-start" onClick={() => setOpen(false)}>ปิด</Button>
        </div>
      ) : null}
      {command?.command ? (
        <div className="flex flex-col gap-3 rounded-2xl bg-muted/50 p-4">
          <p className="text-sm">คำสั่งสำหรับ {machine.machineId} · เป้าหมาย {command.targetVersion} · หมดอายุเมื่อ (UTC): {command.expiresAtUtc}</p>
          <p className="text-sm text-muted-foreground">เรียกใช้ PowerShell แบบผู้ดูแลบนเครื่องนี้ คำสั่งมีตั๋วใช้ครั้งเดียว</p>
          <textarea aria-label={`คำสั่งอัปเดต Agent สำหรับ ${machine.machineId}`} className="min-h-24 w-full rounded-xl border border-transparent bg-background px-3 py-2 font-mono text-xs outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/30" rows={5} readOnly spellCheck={false} value={command.command} />
          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" variant="outline" size="sm" disabled={expired} onClick={() => void copy()}>คัดลอกคำสั่งอัปเดต</Button>
            {expired ? <p className="text-sm text-status-maintenance">คำสั่งหมดอายุแล้ว</p> : null}
          </div>
        </div>
      ) : null}
      <p role="status" aria-live="polite" className="text-sm">{message}</p>
    </section>
  );
}
