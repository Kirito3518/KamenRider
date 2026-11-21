"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Monitor, RotateCcw, X } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { Spinner } from "@/components/ui/spinner";
import type { PcControlMachine } from "@/lib/pc-control";

type Ticket = { ticket: string; expiresAtUtc: string; webSocketUrl: string };

export function PcControlVnc({ machineId, onClose }: { machineId: string; onClose: () => void }) {
  const [ticket, setTicket] = useState<Ticket | null>(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState("");
  const [status, setStatus] = useState("กำลังขอสิทธิ์เชื่อมต่อจอ…");
  const [attempt, setAttempt] = useState(0);
  const frame = useRef<HTMLIFrameElement>(null);
  const connectSent = useRef(false);
  const requestGeneration = useRef(0);

  const start = useCallback(async () => {
    const generation = requestGeneration.current + 1;
    requestGeneration.current = generation;
    setBusy(true);
    setError("");
    setTicket(null);
    connectSent.current = false;
    setStatus("กำลังขอสิทธิ์เชื่อมต่อจอ…");
    try {
      const response = await fetch("/api/admin/vnc-ticket", {
        method: "POST",
        cache: "no-store",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ machineId }),
      });
      const payload = (await response.json()) as Ticket | { error?: string };
      if (requestGeneration.current !== generation) return;
      if (!response.ok) throw new Error("error" in payload ? payload.error : undefined);
      const issued = payload as Ticket;
      const expires = Date.parse(issued.expiresAtUtc);
      if (!Number.isFinite(expires) || expires <= Date.now()) throw new Error("ตั๋ว VNC หมดอายุแล้ว กรุณาลองใหม่");
      setTicket(issued);
      setStatus("กำลังโหลดหน้าจอ…");
      setAttempt(generation);
    } catch (cause) {
      if (requestGeneration.current === generation)
        setError(cause instanceof Error ? cause.message : "เปิดจอ VNC ไม่สำเร็จ");
    } finally {
      if (requestGeneration.current === generation) setBusy(false);
    }
  }, [machineId]);

  useEffect(() => {
    const timer = window.setTimeout(() => void start(), 0);
    return () => {
      window.clearTimeout(timer);
      requestGeneration.current += 1;
    };
  }, [start]);

  useEffect(() => {
    function receive(event: MessageEvent) {
      if (event.origin !== window.location.origin || event.source !== frame.current?.contentWindow) return;
      if (event.data?.type === "kr-vnc-ready") setStatus("เชื่อมต่อจอแล้ว (อ่านอย่างเดียว)");
      else if (event.data?.type === "kr-vnc-status" && typeof event.data.message === "string")
        setStatus(event.data.message.slice(0, 200));
    }
    window.addEventListener("message", receive);
    return () => window.removeEventListener("message", receive);
  }, []);

  useEffect(() => {
    if (!ticket) return;
    const deadline = Date.parse(ticket.expiresAtUtc);
    const timer = window.setTimeout(() => {
      setTicket(null);
      setStatus("ตั๋ว VNC หมดอายุแล้ว กรุณาเปิดจอใหม่");
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
            const deadline = Date.parse(ticket.expiresAtUtc);
            if (!Number.isFinite(deadline) || deadline <= Date.now()) {
              setTicket(null);
              setStatus("ตั๋ว VNC หมดอายุแล้ว กรุณาเปิดจอใหม่");
              return;
            }
            const target = frame.current?.contentWindow;
            if (!target) return;
            connectSent.current = true;
            target.postMessage(
              { type: "kr-vnc-connect", ticket: ticket.ticket, webSocketUrl: ticket.webSocketUrl },
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
  onClose,
}: {
  machine: PcControlMachine | null;
  onClose: () => void;
}) {
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
        {machine ? <PcControlVnc machineId={machine.machineId} onClose={onClose} /> : null}
      </DialogContent>
    </Dialog>
  );
}
