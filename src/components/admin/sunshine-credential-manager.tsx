"use client";

import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type ChangeEvent,
  type FormEvent,
} from "react";
import { Info, KeyRound, RefreshCw } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import type { SunshineCredentialState } from "@/lib/machine-contracts";
import { formatLocalDateTime } from "@/lib/machine-specs";

const MIN_PASSWORD_LENGTH = 8;
const MAX_PASSWORD_LENGTH = 256;
const MAX_USERNAME_LENGTH = 128;

type FieldErrors = {
  userName?: string;
  password?: string;
  confirmPassword?: string;
  form?: string;
};

function validateForm(values: {
  userName: string;
  password: string;
  confirmPassword: string;
}): FieldErrors {
  const errors: FieldErrors = {};
  if (!values.userName) errors.userName = "กรอกชื่อผู้ใช้ Sunshine";
  else if (values.userName.length > MAX_USERNAME_LENGTH)
    errors.userName = `ชื่อผู้ใช้ต้องไม่เกิน ${MAX_USERNAME_LENGTH} ตัวอักษร`;
  else if (/[\u0000-\u001f\u007f]/.test(values.userName))
    errors.userName = "ชื่อผู้ใช้ต้องไม่มีอักขระควบคุม";
  if (!values.password) errors.password = "กรอกรหัสผ่าน Sunshine";
  else if (values.password.length < MIN_PASSWORD_LENGTH)
    errors.password = `รหัสผ่านต้องมีอย่างน้อย ${MIN_PASSWORD_LENGTH} ตัวอักษร`;
  else if (values.password.length > MAX_PASSWORD_LENGTH)
    errors.password = `รหัสผ่านต้องไม่เกิน ${MAX_PASSWORD_LENGTH} ตัวอักษร`;
  if (!values.confirmPassword) errors.confirmPassword = "ยืนยันรหัสผ่านอีกครั้ง";
  else if (values.confirmPassword !== values.password)
    errors.confirmPassword = "รหัสผ่านยืนยันไม่ตรงกับรหัสผ่านด้านบน";
  return errors;
}

function errorMessage(status: number) {
  if (status === 401) return "กรุณาเข้าสู่ระบบใหม่";
  if (status === 403)
    return "คุณไม่มีสิทธิ์ตั้งค่า Sunshine หรือคำขอมาจากเว็บไซต์อื่น";
  if (status === 400)
    return "ข้อมูลไม่ถูกต้อง กรุณาตรวจสอบชื่อผู้ใช้และรหัสผ่าน";
  if (status === 409)
    return "ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรชสถานะแล้วลองใหม่";
  if (status === 503) return "ติดต่อ Controller ไม่ได้ กรุณาลองใหม่";
  return `ดำเนินการไม่สำเร็จ (${status})`;
}

async function requestCredentialState(
  init: RequestInit,
): Promise<SunshineCredentialState> {
  const response = await fetch("/api/admin/sunshine-credential", {
    cache: "no-store",
    ...init,
  });
  const payload: unknown = await response.json().catch(() => ({}));
  if (!response.ok)
    throw new Error(errorMessage(response.status));
  return payload as SunshineCredentialState;
}

export function SunshineCredentialManager() {
  const [credential, setCredential] = useState<SunshineCredentialState | null>(
    null,
  );
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [errors, setErrors] = useState<FieldErrors>({});
  const [successMessage, setSuccessMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const formRef = useRef<HTMLFormElement>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true);
    setLoadError("");
    try {
      const state = await requestCredentialState({ signal });
      if (!signal?.aborted) setCredential(state);
    } catch (error) {
      if (!signal?.aborted)
        setLoadError(
          error instanceof Error
            ? error.message
            : "โหลดสถานะ Sunshine ไม่สำเร็จ",
        );
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    // Defer the first load so the effect body itself does not set state.
    const initial = window.setTimeout(() => void load(controller.signal), 0);
    return () => {
      window.clearTimeout(initial);
      controller.abort();
    };
  }, [load]);

  function clearFieldError(event: ChangeEvent<HTMLInputElement>) {
    const name = event.currentTarget.name as keyof Omit<FieldErrors, "form">;
    setErrors((current) => {
      if (!current[name] && !current.form) return current;
      return { ...current, [name]: undefined, form: undefined };
    });
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    const form = event.currentTarget;
    const data = new FormData(form);
    const values = {
      userName: String(data.get("userName") ?? "").trim(),
      password: String(data.get("password") ?? ""),
      confirmPassword: String(data.get("confirmPassword") ?? ""),
    };
    const validation = validateForm(values);
    if (Object.keys(validation).length > 0) {
      setErrors(validation);
      setSuccessMessage("");
      return;
    }
    setBusy(true);
    setErrors({});
    setSuccessMessage("");
    try {
      const state = await requestCredentialState({
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          userName: values.userName,
          password: values.password,
        }),
      });
      setCredential(state);
      // Clear every field, including both password inputs, so the credential is
      // never left rendered in the browser after a successful save.
      form.reset();
      setSuccessMessage(
        "บันทึกข้อมูล Sunshine ของทั้งระบบแล้ว — รหัสผ่านจะไม่ถูกแสดงซ้ำ",
      );
    } catch (error) {
      setErrors({
        form: error instanceof Error ? error.message : "บันทึกไม่สำเร็จ",
      });
    } finally {
      setBusy(false);
    }
  }

  const configured = credential?.configured === true;

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-col gap-1">
          <div className="flex items-center gap-2">
            <KeyRound aria-hidden className="size-5 text-primary" />
            <h1 className="text-2xl font-semibold">ตั้งค่า Sunshine</h1>
            <Badge variant={configured ? "available" : "outline"}>
              {credential
                ? configured
                  ? "ตั้งค่าแล้ว"
                  : "ยังไม่ได้ตั้งค่า"
                : loading
                  ? "กำลังโหลด"
                  : "ไม่ทราบสถานะ"}
            </Badge>
          </div>
          <p className="text-sm text-muted-foreground">
            บัญชีจัดการ Sunshine ใช้ร่วมกันทั้งระบบ ตั้งครั้งเดียวแล้ว Controller
            จะส่งให้เฉพาะ Agent ที่อนุมัติแล้ว
          </p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => void load()}
          disabled={loading || busy}
        >
          {loading ? (
            <Spinner data-icon="inline-start" />
          ) : (
            <RefreshCw data-icon="inline-start" />
          )}
          รีเฟรชสถานะ
        </Button>
      </header>

      <Card>
        <CardHeader>
          <CardTitle>สถานะปัจจุบัน</CardTitle>
          <CardDescription>
            หน้านี้ไม่แสดงรหัสผ่าน Sunshine และ Controller จะไม่ส่งรหัสผ่านกลับมาให้เบราว์เซอร์
          </CardDescription>
        </CardHeader>
        <CardContent>
          {loading && !credential ? (
            <p role="status" className="text-sm text-muted-foreground">
              กำลังโหลดสถานะ Sunshine…
            </p>
          ) : null}
          {loadError ? (
            <p role="alert" className="text-sm text-destructive">
              {loadError}
            </p>
          ) : null}
          {credential ? (
            <dl className="grid gap-4 text-sm sm:grid-cols-2">
              <div className="flex flex-col gap-1">
                <dt className="text-muted-foreground">ชื่อผู้ใช้ Sunshine</dt>
                <dd className="font-mono break-all">
                  {credential.userName || "—"}
                </dd>
              </div>
              <div className="flex flex-col gap-1">
                <dt className="text-muted-foreground">Revision</dt>
                <dd className="font-mono tabular-nums">
                  {credential.revision}
                </dd>
              </div>
              <div className="flex flex-col gap-1">
                <dt className="text-muted-foreground">อัปเดตล่าสุด</dt>
                <dd>{formatLocalDateTime(credential.updatedAtUtc)}</dd>
              </div>
              <div className="flex flex-col gap-1">
                <dt className="text-muted-foreground">การส่งไปยัง Agent</dt>
                <dd>
                  ส่งเฉพาะ Agent ที่อนุมัติแล้ว และ Agent
                  จะใช้เมื่อซิงก์รอบถัดไป
                </dd>
              </div>
            </dl>
          ) : null}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>ตั้งค่าบัญชี Sunshine ของทั้งระบบ</CardTitle>
          <CardDescription>
            เปลี่ยนแล้วมีผลกับเครื่องทุกเครื่องในระบบ ไม่ใช่เฉพาะเครื่องเดียว
          </CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-5">
          <Alert>
            <Info aria-hidden />
            <AlertTitle>คำเตือนก่อนบันทึก</AlertTitle>
            <AlertDescription>
              <ul className="list-disc space-y-1 pl-4">
                <li>บัญชีนี้ใช้ร่วมกันทั้งระบบ (fleet-wide) ไม่ใช่รายเครื่อง</li>
                <li>หน้านี้จะไม่แสดงรหัสผ่านซ้ำหลังบันทึก ควรเก็บรหัสผ่านไว้ในที่ปลอดภัย</li>
                <li>Controller จะส่งข้อมูลให้เฉพาะ Agent ที่อนุมัติแล้วเท่านั้น</li>
                <li>Agent จะใช้บัญชีใหม่เมื่อซิงก์รอบถัดไป ไม่ได้มีผลทันทีทุกเครื่อง</li>
              </ul>
            </AlertDescription>
          </Alert>

          <form
            ref={formRef}
            onSubmit={(event) => void handleSubmit(event)}
            noValidate
          >
            <FieldGroup>
              <Field data-invalid={Boolean(errors.userName)}>
                <FieldLabel htmlFor="sunshine-username">
                  ชื่อผู้ใช้ Sunshine
                </FieldLabel>
                <Input
                  id="sunshine-username"
                  name="userName"
                  autoComplete="username"
                  maxLength={MAX_USERNAME_LENGTH}
                  required
                  aria-invalid={Boolean(errors.userName)}
                  aria-describedby={
                    errors.userName ? "sunshine-username-error" : undefined
                  }
                  onChange={clearFieldError}
                />
                {errors.userName ? (
                  <FieldError id="sunshine-username-error">
                    {errors.userName}
                  </FieldError>
                ) : null}
              </Field>

              <Field data-invalid={Boolean(errors.password)}>
                <FieldLabel htmlFor="sunshine-password">รหัสผ่าน Sunshine</FieldLabel>
                <Input
                  id="sunshine-password"
                  name="password"
                  type="password"
                  autoComplete="new-password"
                  maxLength={MAX_PASSWORD_LENGTH}
                  required
                  aria-invalid={Boolean(errors.password)}
                  aria-describedby={
                    errors.password ? "sunshine-password-error" : undefined
                  }
                  onChange={clearFieldError}
                />
                {errors.password ? (
                  <FieldError id="sunshine-password-error">
                    {errors.password}
                  </FieldError>
                ) : (
                  <p className="text-sm text-muted-foreground">
                    อย่างน้อย {MIN_PASSWORD_LENGTH} ตัวอักษร
                  </p>
                )}
              </Field>

              <Field data-invalid={Boolean(errors.confirmPassword)}>
                <FieldLabel htmlFor="sunshine-password-confirm">
                  ยืนยันรหัสผ่าน Sunshine
                </FieldLabel>
                <Input
                  id="sunshine-password-confirm"
                  name="confirmPassword"
                  type="password"
                  autoComplete="new-password"
                  maxLength={MAX_PASSWORD_LENGTH}
                  required
                  aria-invalid={Boolean(errors.confirmPassword)}
                  aria-describedby={
                    errors.confirmPassword
                      ? "sunshine-password-confirm-error"
                      : undefined
                  }
                  onChange={clearFieldError}
                />
                {errors.confirmPassword ? (
                  <FieldError id="sunshine-password-confirm-error">
                    {errors.confirmPassword}
                  </FieldError>
                ) : null}
              </Field>

              {errors.form ? (
                <FieldError id="sunshine-form-error">{errors.form}</FieldError>
              ) : null}

              <div className="flex flex-wrap items-center gap-3">
                <Button type="submit" disabled={busy}>
                  {busy ? (
                    <Spinner data-icon="inline-start" />
                  ) : (
                    <KeyRound data-icon="inline-start" />
                  )}
                  {busy ? "กำลังบันทึก…" : "บันทึกบัญชี Sunshine"}
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  disabled={busy}
                  onClick={() => {
                    formRef.current?.reset();
                    setErrors({});
                    setSuccessMessage("");
                  }}
                >
                  ล้างฟอร์ม
                </Button>
              </div>
            </FieldGroup>
          </form>
          {successMessage ? (
            <Alert role="status">
              <AlertDescription>{successMessage}</AlertDescription>
            </Alert>
          ) : null}
        </CardContent>
      </Card>
    </div>
  );
}
