"use client";

import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
  type RefObject,
} from "react";
import { MailCheck, RefreshCw, ShieldCheck, WifiOff } from "lucide-react";
import toast from "react-hot-toast";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogMedia,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field, FieldDescription, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Spinner } from "@/components/ui/spinner";
import { ParsecMark } from "@/components/brands/streaming-logos";
import {
  leaseApi,
  leaseErrorMessage,
  leaseErrorStatus,
} from "@/components/my-machines/lease-api";
import { sealParsecCredentials } from "@/lib/parsec-crypto";
import {
  PARSEC_STATE_LABELS,
  parsecAttemptIsRunning,
  parsecErrorLabel,
  type ParsecAttempt,
  type ParsecLeaseState,
} from "@/lib/parsec-contracts";
import type { Lease } from "@/lib/machine-contracts";

const attemptPollIntervalMs = 2_000;
const clockTickMs = 5_000;
/**
 * A 409 can mean a login is already running, the machine is rate-limited, or Parsec is not
 * enabled/advertised — not only a stale e-mail challenge. Keep the copy neutral and actionable.
 */
const conflictMessage =
  "เริ่มเชื่อมต่อ Parsec ไม่ได้ในขณะนี้ — เครื่องอาจกำลังเชื่อมต่ออยู่ ถูกจำกัดจำนวนครั้ง หรือยังไม่ได้เปิดใช้งาน Parsec กรุณารอสักครู่แล้วลองใหม่";

function connectionHost(address: string | null | undefined) {
  if (!address) return null;
  const value = address.trim();
  if (!value) return null;
  if (value.startsWith("[")) {
    const end = value.indexOf("]");
    return end > 0 ? value.slice(1, end) : value;
  }
  return value.split(":")[0] || null;
}

function attemptFailureText(attempt: ParsecAttempt) {
  const label = parsecErrorLabel(attempt.errorCode);
  return attempt.message ? `${label} — ${attempt.message}` : label;
}

/**
 * Renter-side Parsec login. The browser seals the renter's own Parsec account with the
 * Agent's boot key; only ciphertext is relayed. The flow has two POSTs:
 *
 * 1. Start the login, which makes Parsec e-mail a new-IP confirmation.
 * 2. After the customer confirms the e-mail, POST again with `retryAttemptId` so the
 *    Controller supersedes the waiting attempt and queues a clean one that no longer
 *    asks for e-mail confirmation.
 *
 * The password stays in React state only between those two calls and is cleared when
 * the dialog closes or the login succeeds.
 */
export function ParsecDialog({
  lease,
  open,
  onClose,
  returnFocusRef,
}: {
  lease: Lease;
  open: boolean;
  onClose: () => void;
  returnFocusRef: RefObject<HTMLButtonElement | null>;
}) {
  const base = `/api/my-leases/${encodeURIComponent(lease.id)}/parsec`;

  const [state, setState] = useState<ParsecLeaseState | null>(
    lease.parsec ?? null,
  );
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [formBusy, setFormBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [verifyOpen, setVerifyOpen] = useState(false);
  const [verifySending, setVerifySending] = useState(false);
  const [verifyError, setVerifyError] = useState("");
  const [now, setNow] = useState(() => Date.now());
  // Set when the customer presses "ลองใหม่" on a locally timed-out attempt. The form appears
  // immediately even if the next refresh still reports the stale running state (offline Agent
  // or clock skew); the submit itself re-reads the authoritative state.
  const [retryOverride, setRetryOverride] = useState(false);
  const mounted = useRef(true);
  const emailRef = useRef<HTMLInputElement>(null);
  const firstRequestKey = useRef<string | null>(null);
  const retryRequestKey = useRef<string | null>(null);
  const notifiedAttempt = useRef<string | null>(null);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const refresh = useCallback(async () => {
    try {
      const next = await leaseApi<ParsecLeaseState>(base);
      if (mounted.current) {
        setState(next);
        setNow(Date.now());
        setMessage("");
        // The authoritative state supersedes the local retry view unless it is still the same
        // timed-out running attempt (the Controller read path normally resolves it to failed).
        const nextAttempt = next.attempt;
        const stillTimedOut =
          parsecAttemptIsRunning(nextAttempt) &&
          nextAttempt !== null &&
          Date.parse(nextAttempt.expiresAtUtc) <= Date.now();
        if (!stillTimedOut) setRetryOverride(false);
      }
      return next;
    } catch (error) {
      if (mounted.current) setMessage(leaseErrorMessage(error));
      return null;
    }
  }, [base]);

  // Load the authoritative state whenever the dialog opens. The form is cleared in
  // onOpenChangeComplete after the dialog closes, so a reopened dialog starts clean.
  useEffect(() => {
    if (!open) return;
    const timer = window.setTimeout(() => void refresh(), 0);
    return () => window.clearTimeout(timer);
  }, [open, refresh]);

  useEffect(() => {
    if (!open) return;
    const timer = window.setInterval(() => setNow(Date.now()), clockTickMs);
    return () => window.clearInterval(timer);
  }, [open]);

  const attempt = state?.attempt ?? null;
  const running = parsecAttemptIsRunning(attempt);
  const available = state?.available === true && Boolean(state.publicKey);
  const attemptExpired = attempt
    ? Date.parse(attempt.expiresAtUtc) <= now
    : false;
  // An Agent that never reports back would otherwise leave the dialog spinning forever: past
  // its expiresAtUtc a running attempt is terminal (the Controller read path fails it TIMEOUT).
  const attemptTimedOut = running && attemptExpired && !retryOverride;

  useEffect(() => {
    if (!open || !running) return;
    const timer = window.setInterval(
      () => void refresh(),
      attemptPollIntervalMs,
    );
    return () => window.clearInterval(timer);
  }, [open, running, refresh]);

  useEffect(() => {
    if (!attempt) return;
    const key = `${attempt.attemptId}:${attempt.state}`;
    if (notifiedAttempt.current === key) return;
    if (attempt.state === "ready") {
      notifiedAttempt.current = key;
      toast.success("เชื่อมต่อ Parsec สำเร็จ");
    } else if (attempt.state === "failed") {
      notifiedAttempt.current = key;
      if (!verifyOpen)
        toast.error(`เชื่อมต่อ Parsec ไม่สำเร็จ: ${attemptFailureText(attempt)}`);
    }
  }, [attempt, verifyOpen]);

  const showForm =
    available &&
    !attemptTimedOut &&
    (retryOverride ||
      attempt === null ||
      attempt.state === "failed" ||
      attempt.state === "waiting_email");
  const showRunning =
    available &&
    !attemptTimedOut &&
    attempt !== null &&
    (attempt.state === "queued" || attempt.state === "starting");

  const verifyActionDisabled =
    !attempt ||
    verifySending ||
    (!attemptExpired &&
      (attempt.state === "queued" || attempt.state === "starting"));
  const verifyActionLabel = verifySending
    ? "กำลังเชื่อมต่ออีกครั้ง…"
    : !attempt
      ? "กำลังรอ…"
      : attemptExpired &&
          (attempt.state === "queued" ||
            attempt.state === "starting" ||
            attempt.state === "waiting_email")
        ? "ปิด"
        : attempt.state === "waiting_email"
          ? "ยืนยันอีเมลแล้ว"
          : attempt.state === "ready"
            ? "เสร็จสิ้น"
            : attempt.state === "failed"
              ? "ปิด"
              : "กำลังรอ Parsec ส่งอีเมล…";

  async function submitAttempt(source: "form" | "alert") {
    const inAlert = source === "alert";
    if (inAlert ? verifySending : formBusy) return;
    if (inAlert) setVerifyError("");
    else setMessage("");
    if (!email.trim() || !password) {
      const text = "กรอกอีเมลและรหัสผ่าน Parsec";
      if (inAlert) setVerifyError(text);
      else {
        setMessage(text);
        toast.error(text);
      }
      return;
    }
    if (inAlert) setVerifySending(true);
    else setFormBusy(true);
    try {
      const current = await refresh();
      if (!current?.available || !current.publicKey)
        throw new Error("เครื่องนี้ยังไม่พร้อมสำหรับ Parsec");
      if (current.attempt?.state === "ready") {
        setVerifyOpen(false);
        toast.success("เชื่อมต่อ Parsec สำเร็จ");
        return;
      }
      const currentExpired = current.attempt
        ? Date.parse(current.attempt.expiresAtUtc) <= Date.now()
        : false;
      // Only queued/starting block a new POST. A current waiting_email attempt is expected:
      // round two deliberately supersedes it with retryAttemptId.
      const currentBusy =
        !currentExpired &&
        (current.attempt?.state === "queued" ||
          current.attempt?.state === "starting");
      if (currentBusy) throw new Error("Parsec กำลังทำงานอยู่ กรุณารอสักครู่");
      // Round two is only valid while the challenged attempt is still waiting_email/current.
      const retryAttemptId =
        current.attempt?.state === "waiting_email" && !currentExpired
          ? current.attempt.attemptId
          : undefined;
      const keyRef = retryAttemptId ? retryRequestKey : firstRequestKey;
      const idempotencyKey =
        keyRef.current ?? (keyRef.current = crypto.randomUUID());
      const secret = await sealParsecCredentials(current.publicKey, {
        email,
        password,
      });
      const payload: Record<string, unknown> = { idempotencyKey, secret };
      if (retryAttemptId) payload.retryAttemptId = retryAttemptId;
      const created = await leaseApi<ParsecAttempt>(base, payload);
      keyRef.current = null;
      // A successful normal attempt supersedes any stale round-two key from an
      // earlier challenge, otherwise a later round two could reuse it accidentally.
      if (!retryAttemptId) retryRequestKey.current = null;
      setRetryOverride(false);
      setState({ ...current, attempt: created });
      if (retryAttemptId) {
        setVerifyError("");
        toast.success("ส่งคำขอเชื่อมต่ออีกครั้งหลังยืนยันอีเมลแล้ว");
      } else {
        setVerifyOpen(true);
      }
    } catch (error) {
      const text =
        leaseErrorStatus(error) === 409
          ? conflictMessage
          : leaseErrorMessage(error);
      if (inAlert) setVerifyError(text);
      else {
        setMessage(text);
        toast.error(text);
      }
    } finally {
      if (inAlert) setVerifySending(false);
      else setFormBusy(false);
    }
  }

  function handleVerifyAction() {
    if (!attempt) return;
    if (
      attemptExpired &&
      (attempt.state === "queued" ||
        attempt.state === "starting" ||
        attempt.state === "waiting_email")
    ) {
      setVerifyOpen(false);
      return;
    }
    if (attempt.state === "waiting_email") {
      void submitAttempt("alert");
      return;
    }
    if (attempt.state === "ready" || attempt.state === "failed")
      setVerifyOpen(false);
  }

  /** Reveals the form again after a locally timed-out attempt so the customer can retry. */
  function handleExpiredRetry() {
    setMessage("");
    setVerifyError("");
    setVerifyOpen(false);
    setRetryOverride(true);
  }

  const ip = connectionHost(lease.connection?.address);
  const targetEmail = email.trim() || "อีเมลของบัญชี Parsec";

  return (
    <>
      <Dialog
        open={open}
        onOpenChange={(next) => {
          if (!next) onClose();
        }}
        onOpenChangeComplete={(next) => {
          if (next) return;
          setEmail("");
          setPassword("");
          setFormBusy(false);
          setMessage("");
          setVerifyOpen(false);
          setVerifySending(false);
          setVerifyError("");
          setRetryOverride(false);
          setState(null);
          firstRequestKey.current = null;
          retryRequestKey.current = null;
        }}
      >
        <DialogContent
          data-lenis-prevent
          initialFocus={emailRef}
          finalFocus={returnFocusRef}
          className="max-h-[calc(100dvh-2rem)] overflow-y-auto"
        >
          <DialogHeader className="pr-8">
            <DialogTitle className="flex items-center gap-2">
              <ParsecMark className="h-6 w-auto" />
              เชื่อมต่อด้วย Parsec
            </DialogTitle>
            <DialogDescription>
              ใช้บัญชี Parsec ของคุณเอง — รหัสผ่านถูกเข้ารหัสในเบราว์เซอร์ด้วยกุญแจของ Agent
            </DialogDescription>
          </DialogHeader>

          {!state ? (
            message ? (
              <Alert variant="destructive">
                <AlertTitle>โหลดสถานะ Parsec ไม่สำเร็จ</AlertTitle>
                <AlertDescription>{message}</AlertDescription>
              </Alert>
            ) : (
              <Skeleton className="h-40 w-full rounded-2xl" />
            )
          ) : !available ? (
            <Alert variant="destructive">
              <WifiOff aria-hidden />
              <AlertTitle>เครื่องนี้ยังไม่พร้อมสำหรับ Parsec</AlertTitle>
              <AlertDescription>
                {attempt
                  ? attemptFailureText(attempt)
                  : "Agent ยังไม่พร้อมหรือยังไม่พบการติดตั้ง Parsec บนเครื่องนี้ กรุณาลองใหม่ภายหลัง"}
              </AlertDescription>
            </Alert>
          ) : (
            <>
              {attempt?.state === "ready" ? (
                <Alert>
                  <ShieldCheck aria-hidden />
                  <AlertTitle>เชื่อมต่อ Parsec สำเร็จแล้ว</AlertTitle>
                  <AlertDescription>
                    เปิดแอป Parsec ของคุณแล้วเชื่อมต่อเครื่องนี้ได้เลย
                  </AlertDescription>
                </Alert>
              ) : null}

              {showRunning ? (
                <p
                  role="status"
                  className="flex items-center gap-2 rounded-2xl border p-3 text-sm text-muted-foreground"
                >
                  <Spinner />
                  {PARSEC_STATE_LABELS[attempt.state]}
                </p>
              ) : null}

              {attempt?.state === "failed" ? (
                <Alert variant="destructive">
                  <AlertTitle>{PARSEC_STATE_LABELS.failed}</AlertTitle>
                  <AlertDescription>
                    {attemptFailureText(attempt)}
                  </AlertDescription>
                </Alert>
              ) : null}

              {attemptTimedOut ? (
                <div className="flex flex-col gap-3">
                  <Alert variant="destructive">
                    <AlertTitle>{PARSEC_STATE_LABELS.failed}</AlertTitle>
                    <AlertDescription>
                      {parsecErrorLabel("TIMEOUT")} — เครื่องไม่ตอบกลับภายในเวลาที่กำหนด
                      กด “ลองใหม่” เพื่อเริ่มเชื่อมต่ออีกครั้ง
                    </AlertDescription>
                  </Alert>
                  <Button
                    type="button"
                    variant="outline"
                    className="w-full"
                    onClick={handleExpiredRetry}
                  >
                    <RefreshCw data-icon="inline-start" />
                    ลองใหม่
                  </Button>
                </div>
              ) : null}

              {attempt?.state === "waiting_email" &&
              !verifyOpen &&
              !attemptTimedOut ? (
                <Alert>
                  <MailCheck aria-hidden />
                  <AlertTitle>Parsec รอยืนยันอีเมล</AlertTitle>
                  <AlertDescription>
                    โปรดยืนยันอีเมล/IP แล้วกรอกอีเมลและรหัสผ่านอีกครั้งเพื่อเชื่อมต่อ
                    (attempt ก่อนหน้าจะถูกแทนที่)
                  </AlertDescription>
                </Alert>
              ) : null}

              {showForm ? (
                <form
                  className="flex flex-col gap-4"
                  onSubmit={(event: FormEvent<HTMLFormElement>) => {
                    event.preventDefault();
                    void submitAttempt("form");
                  }}
                >
                  <FieldGroup className="gap-4">
                    <Field>
                      <FieldLabel htmlFor="parsec-email">อีเมล Parsec</FieldLabel>
                      <Input
                        ref={emailRef}
                        id="parsec-email"
                        type="email"
                        autoComplete="username"
                        value={email}
                        onChange={(event) => setEmail(event.target.value)}
                        disabled={formBusy}
                        required
                      />
                    </Field>
                    <Field>
                      <FieldLabel htmlFor="parsec-password">
                        รหัสผ่าน Parsec
                      </FieldLabel>
                      <Input
                        id="parsec-password"
                        type="password"
                        autoComplete="current-password"
                        value={password}
                        onChange={(event) => setPassword(event.target.value)}
                        disabled={formBusy}
                        required
                      />
                      <FieldDescription>
                        รหัสผ่านถูกเข้ารหัสในเบราว์เซอร์และถอดรหัสบนเครื่องเท่านั้น
                        — ระบบยังไม่รองรับ Parsec 2FA
                      </FieldDescription>
                    </Field>
                  </FieldGroup>
                  <Button
                    type="submit"
                    className="w-full"
                    disabled={formBusy || !email.trim() || !password}
                  >
                    {formBusy ? <Spinner data-icon="inline-start" /> : null}
                    {formBusy
                      ? "กำลังส่ง…"
                      : attempt?.state === "waiting_email"
                        ? "ยืนยันอีเมลแล้ว เชื่อมต่ออีกครั้ง"
                        : "เชื่อมต่อ Parsec"}
                  </Button>
                  {message ? (
                    <p role="status" className="text-sm text-destructive">
                      {message}
                    </p>
                  ) : null}
                </form>
              ) : null}
            </>
          )}

          <DialogFooter>
            <Button variant="outline" onClick={onClose}>
              ปิด
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <AlertDialog
        open={verifyOpen}
        onOpenChange={(next) => setVerifyOpen(next)}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogMedia>
              <MailCheck aria-hidden />
            </AlertDialogMedia>
            <AlertDialogTitle>ยืนยันอีเมล Parsec</AlertDialogTitle>
            <AlertDialogDescription>
              Parsec ส่งอีเมลยืนยันการเข้าสู่ระบบจากที่อยู่ IP{" "}
              <span className="font-medium text-foreground">
                {ip ?? "ของเครื่องที่คุณเช่า"}
              </span>{" "}
              ไปที่{" "}
              <span className="font-medium text-foreground">{targetEmail}</span>
              <br />
              เปิดอีเมลแล้วกดยืนยัน จากนั้นกลับมากด “ยืนยันอีเมลแล้ว”
              ระบบจะส่งคำขอเชื่อมต่ออีกครั้งโดยไม่ติดการยืนยันอีเมล
            </AlertDialogDescription>
          </AlertDialogHeader>

          {verifyError ? (
            <Alert variant="destructive">
              <AlertTitle>เชื่อมต่ออีกครั้งไม่สำเร็จ</AlertTitle>
              <AlertDescription>{verifyError}</AlertDescription>
            </Alert>
          ) : null}
          {verifySending ? (
            <p
              role="status"
              className="flex items-center gap-2 text-sm text-muted-foreground"
            >
              <Spinner />
              กำลังส่งคำขอเชื่อมต่ออีกครั้งหลังยืนยันอีเมล…
            </p>
          ) : null}
          {attempt ? (
            <p role="status" className="text-sm text-muted-foreground">
              สถานะ:{" "}
              {attempt.state === "failed"
                ? attemptFailureText(attempt)
                : PARSEC_STATE_LABELS[attempt.state]}
            </p>
          ) : null}
          {attemptExpired &&
          attempt &&
          (attempt.state === "queued" ||
            attempt.state === "starting" ||
            attempt.state === "waiting_email") ? (
            <Alert>
              <AlertTitle>หมดเวลารอผลจากเครื่อง</AlertTitle>
              <AlertDescription>
                กด “ปิด” แล้วกด “ลองใหม่” ในหน้าต่างเชื่อมต่อ Parsec
                เพื่อเริ่มเชื่อมต่ออีกครั้ง
              </AlertDescription>
            </Alert>
          ) : null}

          <AlertDialogFooter>
            <AlertDialogCancel>ปิด</AlertDialogCancel>
            <AlertDialogAction
              onClick={handleVerifyAction}
              disabled={verifyActionDisabled}
            >
              {verifySending ? <Spinner data-icon="inline-start" /> : null}
              {verifyActionLabel}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}
