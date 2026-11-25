"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { Monitor, RotateCcw, X } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { Spinner } from "@/components/ui/spinner";
import { vncTargetIssue } from "@/components/admin/pc-control-card";
import type { PcControlMachine } from "@/lib/pc-control";

type Ticket = { ticket: string; expiresAtUtc: string; webSocketUrl: string };

/** Turn any failure into Thai text the Admin can act on, without exposing fetch internals. */
function failureMessage(cause: unknown) {
  if (cause instanceof TypeError) return "เชื่อมต่อเซิร์ฟเวอร์ไม่สำเร็จ กรุณาลองใหม่";
  if (cause instanceof Error && cause.message.trim()) return cause.message;
  return "เปิดจอ VNC ไม่สำเร็จ";
}

export function PcControlVnc({ machineId, onClose }: { machineId: string; onClose: () => void }) {
  const [ticket, setTicket] = useState<Ticket | null>(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState("");
  const [status, setStatus] = useState("กำลังขอสิทธิ์เชื่อมต่อจอ…");
  const [attempt, setAttempt] = useState(0);
  const frame = useRef<HTMLIFrameElement>(null);
  const connectSent = useRef(false);
  const requestGeneration = useRef(0);
  const ticketRef = useRef<Ticket | null>(null);
  const refreshInFlight = useRef(false);

  const issueTicket = useCallback(async () => {
    const response = await fetch("/api/admin/vnc-ticket", {
      method: "POST",
      cache: "no-store",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ machineId }),
    });
    const payload = (await response.json().catch(() => ({}))) as
      | Ticket
      | { error?: unknown; status?: number };
    if (!response.ok) {
      const message =
        "error" in payload && typeof payload.error === "string" ? payload.error : undefined;
      throw new Error(
        message && message.trim()
          ? message
          : `เปิดจอ VNC ไม่สำเร็จ (${response.status})`,
      );
    }
    const issued = payload as Ticket;
    const expires = Date.parse(issued.expiresAtUtc);
    if (!Number.isFinite(expires) || expires <= Date.now())
      throw new Error("ตั๋ว VNC หมดอายุแล้ว กรุณาลองใหม่");
    return issued;
  }, [machineId]);

  const start = useCallback(async () => {
    const generation = requestGeneration.current + 1;
    requestGeneration.current = generation;
    ticketRef.current = null;
    setBusy(true);
    setError("");
    setTicket(null);
    connectSent.current = false;
    setStatus("กำลังขอสิทธิ์เชื่อมต่อจอ…");
    try {
      const issued = await issueTicket();
      if (requestGeneration.current !== generation) return;
      ticketRef.current = issued;
      setTicket(issued);
      setStatus("กำลังโหลดหน้าจอ…");
      setAttempt(generation);
    } catch (cause) {
      if (requestGeneration.current === generation) setError(failureMessage(cause));
    } finally {
      if (requestGeneration.current === generation) setBusy(false);
    }
  }, [issueTicket]);

  /**
   * A VNC ticket is single-use even when authentication fails, so a wrong-password retry needs
   * fresh ticket material. The viewer keeps the typed password in its own memory and only gets
   * the replacement ticket through this same-origin handshake; the password never leaves the
   * iframe and is never stored by this component.
   */
  const refreshTicket = useCallback(async () => {
    if (refreshInFlight.current) return;
    refreshInFlight.current = true;
    const generation = requestGeneration.current;
    try {
      setStatus("กำลังขอตั๋วใหม่เพื่อลองเชื่อมต่ออีกครั้ง…");
      const issued = await issueTicket();
      if (requestGeneration.current !== generation) return;
      const target = frame.current?.contentWindow;
      if (!target) return;
      ticketRef.current = issued;
      setTicket(issued);
      setStatus("กำลังเชื่อมต่อใหม่…");
      target.postMessage(
        {
          type: "kr-vnc-reconnect",
          ticket: issued.ticket,
          webSocketUrl: issued.webSocketUrl,
        },
        window.location.origin,
      );
    } catch (cause) {
      if (requestGeneration.current !== generation) return;
      const message = failureMessage(cause);
      setStatus(`ขอตั๋วใหม่ไม่สำเร็จ: ${message}`);
      frame.current?.contentWindow?.postMessage(
        { type: "kr-vnc-ticket-error", message },
        window.location.origin,
      );
    } finally {
      refreshInFlight.current = false;
    }
  }, [issueTicket]);

  useEffect(() => {
    const timer = window.setTimeout(() => void start(), 0);
    return () => {
      window.clearTimeout(timer);
      requestGeneration.current += 1;
    };
  }, [start]);

  useEffect(() => {
    function receive(event: MessageEvent) {
      if (event.origin !== window.location.origin || event.source !== frame.current?.contentWindow)
        return;
      if (event.data?.type === "kr-vnc-ready") setStatus("เชื่อมต่อจอแล้ว (อ่านอย่างเดียว)");
      else if (event.data?.type === "kr-vnc-status" && typeof event.data.message === "string")
        setStatus(event.data.message.slice(0, 200));
      else if (event.data?.type === "kr-vnc-need-ticket") void refreshTicket();
    }
    window.addEventListener("message", receive);
    return () => window.removeEventListener("message", receive);
  }, [refreshTicket]);

  /**
   * A ticket only authorises a NEW WebSocket handshake and expires after ~60 s. The Controller
   * keeps an established session alive for up to 1 hour, so expiry must never tear the iframe
   * down once the connect message has been sent; it only blocks a handshake that has not
   * started yet.
   */
  useEffect(() => {
    if (!ticket) return;
    const deadline = Date.parse(ticket.expiresAtUtc);
    const timer = window.setTimeout(() => {
      if (connectSent.current) return;
      ticketRef.current = null;
      setTicket(null);
      setBusy(false);
      const message = "ตั๋ว VNC หมดอายุแล้ว กรุณาขอตั๋วใหม่";
      setStatus(message);
      setError(message);
    }, Math.max(0, deadline - Date.now()));
    return () => window.clearTimeout(timer);
  }, [ticket]);

  return (
    <div className="flex flex-col gap-2 rounded-xl border border-border bg-muted p-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground" role="status" aria-live="polite">
          {status}
        </p>
        <Button variant="ghost" size="sm" onClick={onClose}>
          <X data-icon="inline-start" />
          ปิดจอ
        </Button>
      </div>
      {error ? (
        <Alert variant="destructive">
          <AlertTitle>เปิดจอไม่สำเร็จ</AlertTitle>
          <AlertDescription className="flex flex-wrap items-center gap-2">
            {error}
            <Button variant="outline" size="sm" onClick={() => void start()}>
              <RotateCcw data-icon="inline-start" />
              ลองใหม่
            </Button>
          </AlertDescription>
        </Alert>
      ) : null}
      {busy && !ticket ? <Skeleton className="aspect-video w-full rounded-xl" /> : null}
      {ticket ? (
        <iframe
          key={attempt}
          ref={frame}
          title={`หน้าจอ VNC ของ ${machineId}`}
          src="/novnc/viewer.html"
          referrerPolicy="no-referrer"
          className="aspect-video w-full rounded-xl border border-border bg-background"
          onLoad={() => {
            if (connectSent.current) return;
            const current = ticketRef.current;
            if (!current) return;
            const deadline = Date.parse(current.expiresAtUtc);
            if (!Number.isFinite(deadline) || deadline <= Date.now()) {
              ticketRef.current = null;
              setTicket(null);
              setBusy(false);
              const message = "ตั๋ว VNC หมดอายุแล้ว กรุณาขอตั๋วใหม่";
              setStatus(message);
              setError(message);
              return;
            }
            const target = frame.current?.contentWindow;
            if (!target) return;
            connectSent.current = true;
            target.postMessage(
              {
                type: "kr-vnc-connect",
                ticket: current.ticket,
                webSocketUrl: current.webSocketUrl,
              },
              window.location.origin,
            );
          }}
        />
      ) : null}
      {!busy && !ticket && !error ? (
        <Button variant="default" size="sm" onClick={() => void start()}>
          <Monitor data-icon="inline-start" />
          เปิดจออีกครั้ง
        </Button>
      ) : null}
      {busy && ticket ? (
        <p className="flex items-center gap-2 text-xs text-muted-foreground">
          <Spinner /> กำลังเชื่อมต่อ
        </p>
      ) : null}
    </div>
  );
}

/**
 * One VNC session at a time: the parent holds a single `machine` value. Rendering the session
 * inside a modal keeps the PC Control cards usable after close and prevents an accidental
 * second stream from being opened in another card.
 */
export function PcControlVncDialog({
  machine,
  now,
  onClose,
}: {
  machine: PcControlMachine | null;
  now: number;
  onClose: () => void;
}) {
  const targetIssue = machine ? vncTargetIssue(machine, now) : "";
  return (
    <Dialog open={machine !== null} onOpenChange={(next) => { if (!next) onClose(); }}>
      <DialogContent className="sm:max-w-6xl" showCloseButton={false}>
        <DialogHeader>
          <DialogTitle className="text-lg">
            หน้าจอ VNC · {machine?.displayName || machine?.machineId}
          </DialogTitle>
          <DialogDescription>
            ดูอย่างเดียว ไม่สามารถควบคุมเมาส์หรือคีย์บอร์ดจากหน้านี้ · เปิดได้ทีละจอ
          </DialogDescription>
        </DialogHeader>
        {machine && targetIssue ? (
          <Alert>
            <AlertTitle>เป้าหมาย VNC ยังไม่พร้อม</AlertTitle>
            <AlertDescription className="flex flex-col gap-1">
              <span>
                {targetIssue} · การออกตั๋ว VNC ใหม่จะถูกปฏิเสธจนกว่าจะแก้ไข
              </span>
              <Link
                className="w-fit underline underline-offset-4 hover:text-foreground"
                href="/admin/machines"
              >
                ไปตั้งค่า VNC target ที่หน้าเครื่องและ Agent
              </Link>
            </AlertDescription>
          </Alert>
        ) : null}
        {machine ? <PcControlVnc machineId={machine.machineId} onClose={onClose} /> : null}
      </DialogContent>
    </Dialog>
  );
}
