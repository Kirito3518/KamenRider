"use client";

import { Eye, EyeOff, X } from "lucide-react";
import { useState, type FormEvent, type RefObject } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Separator } from "@/components/ui/separator";
import { Input } from "@/components/ui/input";
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput } from "@/components/ui/input-group";
import toast from "react-hot-toast";
import { useRouter } from "next/navigation";
import { authClient } from "@/lib/auth-client";
import { authError } from "@/lib/auth-errors";
import { ProviderButtons } from "@/components/auth/provider-buttons";

type View = "login" | "register";

const copy = {
  login: { title: "เข้าสู่ระบบ", description: "ยินดีต้อนรับกลับมา !", submit: "เข้าสู่ระบบ" },
  register: { title: "สมัครสมาชิก", description: "เริ่มต้นประสบการณ์เล่นเกมในแบบของคุณ", submit: "สมัครสมาชิก" },
};

export function AuthDialog({ open, onOpenChange, returnFocusRef }: { open: boolean; onOpenChange: (open: boolean) => void; returnFocusRef: RefObject<HTMLButtonElement | null> }) {
  const [view, setView] = useState<View>("login");
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmation, setShowConfirmation] = useState(false);
  const [passwordError, setPasswordError] = useState("");
  const [pending, setPending] = useState(false);
  const router = useRouter();
  const content = copy[view];

  function changeView(next: View) {
    setView(next);
    setShowPassword(false);
    setShowConfirmation(false);
    setPasswordError("");

  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (view === "register") {
      const form = event.currentTarget;
      const values = new FormData(form);
      if (values.get("password") !== values.get("confirmPassword")) {
        setPasswordError("รหัสผ่านไม่ตรงกัน กรุณากรอกให้ตรงกัน");
        (form.elements.namedItem("confirmPassword") as HTMLInputElement).focus();
        return;
      }
    }
    setPasswordError("");
    if (pending) return;
    const values = new FormData(event.currentTarget);
    const username = String(values.get("username")).trim();
    const password = String(values.get("password"));
    setPending(true);
    try {
      const result = view === "register"
        ? await authClient.signUp.email({ name: username, username, email: String(values.get("email")).trim(), password })
        : await authClient.signIn.username({ username, password });
      if (result.error) { toast.error(authError(result.error)); return; }
      toast.success(view === "register" ? "สมัครสมาชิกสำเร็จ" : "เข้าสู่ระบบสำเร็จ");
      onOpenChange(false);
      router.replace("/account");
      router.refresh();
    } catch { toast.error("เชื่อมต่อไม่ได้ กรุณาลองใหม่อีกครั้ง"); }
    finally { setPending(false); }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange} onOpenChangeComplete={(isOpen) => { if (!isOpen) changeView("login"); }}>
      <DialogContent showCloseButton={false} finalFocus={returnFocusRef} data-lenis-prevent className="max-h-[calc(100dvh-2rem)] gap-4 overflow-y-auto rounded-2xl sm:max-w-md">
        <DialogHeader className="pr-8">
          <DialogTitle>{content.title}</DialogTitle>
          <DialogDescription>{content.description}</DialogDescription>
        </DialogHeader>
        <DialogClose render={<Button variant="ghost" size="icon-sm" className="absolute top-5 right-5" />} aria-label="ปิดหน้าต่างเข้าสู่ระบบ"><X /></DialogClose>

        <form key={view} onSubmit={handleSubmit}>
          <fieldset disabled={pending} className="contents"><FieldGroup className="gap-4">
            {(
              <Field className="gap-2">
                <FieldLabel htmlFor="auth-username">ชื่อผู้ใช้</FieldLabel>
                <Input id="auth-username" name="username" placeholder="กรอกชื่อผู้ใช้" autoComplete="username" autoFocus required minLength={3} maxLength={30} pattern="[a-zA-Z0-9_]+" title="ตัวอักษรอังกฤษ ตัวเลข หรือขีดล่าง 3–30 ตัว" />
              </Field>
            )}
            {view === "register" && (
              <Field className="gap-2">
                <FieldLabel htmlFor="auth-email">อีเมล</FieldLabel>
                <Input id="auth-email" name="email" type="email" placeholder="you@example.com" autoComplete="email" required maxLength={254} />
              </Field>
            )}
            {(
              <Field className="gap-2">
                <FieldLabel htmlFor="auth-password">รหัสผ่าน</FieldLabel>
                <InputGroup className="overflow-hidden">
                  <InputGroupInput id="auth-password" name="password" type={showPassword ? "text" : "password"} placeholder="กรอกรหัสผ่าน" autoComplete={view === "register" ? "new-password" : "current-password"} onInput={() => setPasswordError("")} required minLength={view === "register" ? 8 : 1} maxLength={128} />
                  <InputGroupAddon align="inline-end">
                    <InputGroupButton size="icon-sm" aria-label={showPassword ? "ซ่อนรหัสผ่าน" : "แสดงรหัสผ่าน"} aria-pressed={showPassword} onClick={() => setShowPassword(!showPassword)}>
                      {showPassword ? <EyeOff /> : <Eye />}
                    </InputGroupButton>
                  </InputGroupAddon>
                </InputGroup>
              </Field>
            )}
            {view === "register" && (
              <Field className="gap-2" data-invalid={!!passwordError}>
                <FieldLabel htmlFor="auth-confirm-password">ยืนยันรหัสผ่าน</FieldLabel>
                <InputGroup className="overflow-hidden">
                  <InputGroupInput id="auth-confirm-password" name="confirmPassword" type={showConfirmation ? "text" : "password"} placeholder="กรอกรหัสผ่านอีกครั้ง" autoComplete="new-password" required minLength={view === "register" ? 8 : 1} maxLength={128} aria-invalid={!!passwordError} aria-describedby={passwordError ? "auth-password-error" : undefined} onInput={() => setPasswordError("")} />
                  <InputGroupAddon align="inline-end">
                    <InputGroupButton size="icon-sm" aria-label={showConfirmation ? "ซ่อนรหัสผ่านยืนยัน" : "แสดงรหัสผ่านยืนยัน"} aria-pressed={showConfirmation} onClick={() => setShowConfirmation(!showConfirmation)}>
                      {showConfirmation ? <EyeOff /> : <Eye />}
                    </InputGroupButton>
                  </InputGroupAddon>
                </InputGroup>
                {passwordError && <FieldError id="auth-password-error">{passwordError}</FieldError>}
              </Field>
            )}
            <Button type="submit" disabled={pending} className="mt-1 h-10 w-full rounded-xl">{pending ? "กำลังดำเนินการ…" : content.submit}</Button>
          </FieldGroup></fieldset>
        </form>

        <div className="flex items-center gap-3"><Separator className="flex-1" /><span className="text-xs text-muted-foreground">หรือ</span><Separator className="flex-1" /></div>
        <ProviderButtons />

        <p className="text-center text-xs text-muted-foreground sm:text-sm">
          {view === "login" ? "ยังไม่มีบัญชี? " : "มีบัญชีอยู่แล้ว? "}
          <Button type="button" variant="link" className="h-auto p-0" disabled={pending} onClick={() => changeView(view === "login" ? "register" : "login")}>
            {view === "login" ? "สมัครสมาชิก" : "เข้าสู่ระบบ"}
          </Button>
        </p>
      </DialogContent>
    </Dialog>
  );
}
