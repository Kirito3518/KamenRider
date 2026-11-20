"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { sealParsecCredentials } from "@/lib/parsec-crypto";
import {
  PARSEC_STATE_LABELS,
  parsecAttemptIsRunning,
  parsecErrorLabel,
  type ParsecAttempt,
  type ParsecLeaseState,
} from "@/lib/parsec-contracts";

async function api(path: string, body?: unknown) {
  const response = await fetch(path, {
    method: body ? "POST" : "GET",
    cache: "no-store",
    headers: { "Content-Type": "application/json" },
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await response.json();
  if (!response.ok)
    throw new Error(
      response.status === 401
        ? "กรุณาเข้าสู่ระบบ"
        : data.error || `${response.status}: คำขอไม่สำเร็จ`,
    );
  return data;
}

/**
 * Renter-side Parsec login. The customer enters their own Parsec account; the browser
 * seals the credentials with the Agent's boot key and only ciphertext is relayed.
 */
export function ParsecConnect({
  leaseId,
  initial,
}: {
  leaseId: string;
  initial: ParsecLeaseState | null;
}) {
  const base = `/api/my-leases/${encodeURIComponent(leaseId)}/parsec`;
  const [state, setState] = useState<ParsecLeaseState | null>(initial);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const refresh = useCallback(async () => {
    try {
      const next = (await api(base)) as ParsecLeaseState;
      if (mounted.current) setState(next);
      return next;
    } catch (error) {
      if (mounted.current) setMessage(String(error));
      return null;
    }
  }, [base]);

  const attempt = state?.attempt ?? null;
  const running = parsecAttemptIsRunning(attempt);

  useEffect(() => {
    if (!running) return;
    const timer = setInterval(() => void refresh(), 2_000);
    return () => clearInterval(timer);
  }, [running, refresh]);

  if (!state?.available) return null;

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setMessage("");
    try {
      const current = await refresh();
      if (!current?.publicKey) throw new Error("เครื่องนี้ยังไม่พร้อมสำหรับ Parsec");
      const secret = await sealParsecCredentials(current.publicKey, {
        email,
        password,
      });
      const created = (await api(base, {
        idempotencyKey: crypto.randomUUID(),
        secret,
      })) as ParsecAttempt;
      setState({ ...current, attempt: created });
      setPassword("");
      setMessage("ส่งคำขอเข้าสู่ระบบ Parsec ไปยังเครื่องแล้ว");
    } catch (error) {
      setMessage(String(error));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="space-y-3 rounded-lg border p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="font-medium">เชื่อมต่อด้วย Parsec</h3>
        <span className="text-sm text-muted-foreground">
          ใช้บัญชี Parsec ของคุณเอง
        </span>
      </div>

      {attempt ? (
        <p role="status" className="text-sm">
          {attempt.state === "failed"
            ? `${PARSEC_STATE_LABELS.failed}: ${parsecErrorLabel(attempt.errorCode)}${attempt.message ? ` — ${attempt.message}` : ""}`
            : PARSEC_STATE_LABELS[attempt.state]}
        </p>
      ) : null}

      {attempt?.state === "ready" ? (
        <p className="text-sm">
          เครื่องนี้เข้าสู่ระบบ Parsec ของคุณแล้ว — เปิดแอป Parsec ของคุณแล้วเชื่อมต่อเครื่องนี้ได้เลย
        </p>
      ) : running ? (
        <p className="text-sm text-muted-foreground">
          กำลังทำงานบนเครื่อง กรุณารอสักครู่ ระบบจะอัปเดตสถานะให้อัตโนมัติ
        </p>
      ) : (
        <form className="space-y-3" onSubmit={(event) => void submit(event)}>
          <label className="block">
            อีเมล Parsec
            <input
              className="ml-2 rounded border p-2"
              type="email"
              autoComplete="username"
              value={email}
              onChange={(event) => setEmail(event.currentTarget.value)}
              required
              disabled={busy}
            />
          </label>
          <label className="block">
            รหัสผ่าน Parsec
            <input
              className="ml-2 rounded border p-2"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(event) => setPassword(event.currentTarget.value)}
              required
              disabled={busy}
            />
          </label>
          <button className="rounded border p-2 disabled:opacity-50" disabled={busy}>
            {busy ? "กำลังส่ง…" : "เชื่อมต่อ Parsec"}
          </button>
          <p className="text-sm text-muted-foreground">
            รหัสผ่านถูกเข้ารหัสในเบราว์เซอร์ด้วยกุญแจของ Agent และถอดรหัสบนเครื่องเท่านั้น
            — ระบบยังไม่รองรับ Parsec 2FA และ Parsec อาจขอให้ยืนยันอีเมล/อุปกรณ์ก่อน
          </p>
        </form>
      )}

      {message ? <p role="status">{message}</p> : null}
    </section>
  );
}
