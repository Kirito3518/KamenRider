"use client";

import { useCallback, useEffect, useRef, useState } from "react";

type Ticket = {
  ticket: string;
  expiresAtUtc: string;
  webSocketUrl: string;
};

export function AdminVncViewerButton({ machineId }: { machineId: string }) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [ticket, setTicket] = useState<Ticket | null>(null);
  const [viewerReady, setViewerReady] = useState(false);
  const [status, setStatus] = useState("");
  const frame = useRef<HTMLIFrameElement>(null);
  const connectSent = useRef(false);

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
        setViewerReady(true);
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
    if (!open || !ticket || !viewerReady || connectSent.current) return;
    connectSent.current = true;
    frame.current?.contentWindow?.postMessage(
      {
        type: "kr-vnc-connect",
        ticket: ticket.ticket,
        webSocketUrl: ticket.webSocketUrl,
      },
      window.location.origin,
    );
  }, [open, ticket, viewerReady]);

  useEffect(() => {
    if (!open) return;
    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setOpen(false);
        setTicket(null);
        setViewerReady(false);
        setStatus("");
      }
    }
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [open]);

  async function start() {
    setOpen(true);
    setBusy(true);
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
      if (!response.ok) {
        throw new Error("error" in data ? data.error : undefined);
      }
      setTicket(data as Ticket);
      setStatus("กำลังโหลด viewer…");
    } catch (error) {
      setStatus(`เปิด VNC ไม่สำเร็จ: ${String(error)}`);
    } finally {
      setBusy(false);
    }
  }

  const close = useCallback(() => {
    setOpen(false);
    setTicket(null);
    setViewerReady(false);
    connectSent.current = false;
    setStatus("");
  }, []);

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
          className="fixed inset-0 z-50 flex flex-col bg-background p-3 sm:p-5"
        >
          <header className="flex flex-wrap items-center justify-between gap-3 pb-3">
            <div>
              <h2 id="admin-vnc-title" className="text-lg font-semibold">
                VNC · {machineId} · ดูอย่างเดียว
              </h2>
              <p role="status" aria-live="polite" className="text-sm">
                {status || "กำลังเตรียม viewer…"}
              </p>
            </div>
            <button type="button" className="rounded border p-2" onClick={close}>
              ปิดการเชื่อมต่อ
            </button>
          </header>
          {ticket ? (
            <iframe
              ref={frame}
              title={`หน้าจอ VNC ของ ${machineId}`}
              src="/novnc/viewer.html"
              referrerPolicy="no-referrer"
              className="min-h-0 w-full flex-1 rounded border bg-[#111827]"
              onLoad={() => setViewerReady(true)}
            />
          ) : null}
        </section>
      ) : null}
    </>
  );
}
