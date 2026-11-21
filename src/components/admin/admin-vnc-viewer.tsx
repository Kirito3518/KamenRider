"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Loader2 } from "lucide-react";
import { SkeletonBlock } from "@/components/loading";

type Ticket = {
  ticket: string;
  expiresAtUtc: string;
  webSocketUrl: string;
};

export function AdminVncViewerButton({ machineId }: { machineId: string }) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [ticket, setTicket] = useState<Ticket | null>(null);
  const [status, setStatus] = useState("");
  const [iframeAttempt, setIframeAttempt] = useState(0);
  const frame = useRef<HTMLIFrameElement>(null);
  const connectSent = useRef(false);
  const requestGeneration = useRef(0);

  const close = useCallback(() => {
    requestGeneration.current += 1;
    setOpen(false);
    setBusy(false);
    setTicket(null);
    connectSent.current = false;
    setStatus("");
  }, []);

  useEffect(() => {
    if (!open) return;
    function receive(event: MessageEvent) {
      if (
        event.origin !== window.location.origin ||
        event.source !== frame.current?.contentWindow
      ) {
        return;
      }
      if (event.data?.type === "kr-vnc-ready") {
        setStatus("กำลังเชื่อมต่อกับเครื่องลูก…");
      } else if (
        event.data?.type === "kr-vnc-status" &&
        typeof event.data.message === "string"
      ) {
        setStatus(event.data.message.slice(0, 200));
      }
    }
    window.addEventListener("message", receive);
    return () => window.removeEventListener("message", receive);
  }, [open]);

  useEffect(() => {
    if (!open) return;
    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === "Escape") {
        close();
      }
    }
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [open, close]);

  async function start() {
    const requestId = requestGeneration.current + 1;
    requestGeneration.current = requestId;
    setOpen(true);
    setBusy(true);
    setTicket(null);
    setIframeAttempt(requestId);
    connectSent.current = false;
    setStatus("กำลังขอสิทธิ์ดูหน้าจอ…");
    try {
      const response = await fetch("/api/admin/vnc-ticket", {
        method: "POST",
        cache: "no-store",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ machineId }),
      });
      const data = (await response.json()) as Ticket | { error?: string };
      if (requestGeneration.current !== requestId) return;
      if (!response.ok) {
        throw new Error("error" in data ? data.error : undefined);
      }
      const issuedTicket = data as Ticket;
      const expiresAt = Date.parse(issuedTicket.expiresAtUtc);
      if (!Number.isFinite(expiresAt) || expiresAt <= Date.now()) {
        setStatus("ตั๋ว VNC หมดอายุแล้ว กรุณาขอ ticket ใหม่");
        return;
      }
      setTicket(issuedTicket);
      setStatus("กำลังโหลด viewer…");
    } catch (error) {
      if (requestGeneration.current === requestId) {
        setTicket(null);
        setStatus(`เปิด VNC ไม่สำเร็จ: ${String(error)}`);
      }
    } finally {
      if (requestGeneration.current === requestId) setBusy(false);
    }
  }

  return (
    <>
      <button
        type="button"
        className="rounded border p-2"
        onClick={start}
        disabled={busy}
      >
        ดูหน้าจอผ่าน VNC
      </button>
      {open ? (
        <section
          role="dialog"
          aria-modal="true"
          aria-labelledby="admin-vnc-title"
          aria-busy={busy}
          className="fixed inset-0 z-50 flex flex-col bg-background p-3 sm:p-5"
        >
          <header className="flex flex-wrap items-center justify-between gap-3 pb-3">
            <div>
              <h2 id="admin-vnc-title" className="text-lg font-semibold">
                VNC · {machineId} · ดูอย่างเดียว
              </h2>
              <p
                role="status"
                aria-live="polite"
                className="flex items-center gap-2 text-sm"
              >
                <Loader2
                  aria-hidden="true"
                  className="size-4 animate-spin motion-reduce:animate-none"
                />
                <span>{status || "กำลังเตรียม viewer…"}</span>
              </p>
            </div>
            <button type="button" className="rounded border p-2" onClick={close}>
              ปิดการเชื่อมต่อ
            </button>
          </header>
          {open && !busy && !ticket ? (
            <button
              type="button"
              className="w-fit rounded border px-3 py-2"
              onClick={start}
            >
              ขอ ticket ใหม่
            </button>
          ) : null}
          {ticket ? (
            <iframe
              key={iframeAttempt}
              ref={frame}
              title={`หน้าจอ VNC ของ ${machineId}`}
              src="/novnc/viewer.html"
              referrerPolicy="no-referrer"
              className="min-h-0 w-full flex-1 rounded border bg-[#111827]"
              onLoad={() => {
                if (requestGeneration.current !== iframeAttempt || connectSent.current) return;
                const expiresAt = Date.parse(ticket.expiresAtUtc);
                if (!Number.isFinite(expiresAt) || expiresAt <= Date.now()) {
                  setTicket(null);
                  setStatus("ตั๋ว VNC หมดอายุแล้ว กรุณาขอ ticket ใหม่");
                  return;
                }
                const target = frame.current?.contentWindow;
                if (!target) return;
                connectSent.current = true;
                target.postMessage(
                  {
                    type: "kr-vnc-connect",
                    ticket: ticket.ticket,
                    webSocketUrl: ticket.webSocketUrl,
                  },
                  window.location.origin,
                );
              }}
            />
          ) : (
            <div className="min-h-0 w-full flex-1" aria-hidden="true">
              <SkeletonBlock className="h-full min-h-48 w-full" radius="rounded border" />
            </div>
          )}
        </section>
      ) : null}
    </>
  );
}
