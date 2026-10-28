"use client";

import Image from "next/image";
import { Eye, EyeOff, Info, X } from "lucide-react";
import { IconBrandDiscordFilled } from "@tabler/icons-react";
import { useState, type FormEvent, type RefObject } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Separator } from "@/components/ui/separator";
import { Input } from "@/components/ui/input";
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput } from "@/components/ui/input-group";
import { Alert, AlertDescription } from "@/components/ui/alert";

type View = "login" | "register" | "reset";

const copy = {
  login: { title: "เข้าสู่ระบบ", description: "ยินดีต้อนรับกลับมา !", submit: "เข้าสู่ระบบ" },
  register: { title: "สมัครสมาชิก", description: "เริ่มต้นประสบการณ์เล่นเกมในแบบของคุณ", submit: "สมัครสมาชิก" },
  reset: { title: "ลืมรหัสผ่าน?", description: "กรอกอีเมลที่ใช้สมัครสมาชิกเพื่อรีเซ็ตรหัสผ่าน", submit: "ส่งลิงก์รีเซ็ตรหัสผ่าน" },
};

export function AuthDialog({ open, onOpenChange, returnFocusRef }: { open: boolean; onOpenChange: (open: boolean) => void; returnFocusRef: RefObject<HTMLButtonElement | null> }) {
  const [view, setView] = useState<View>("login");
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmation, setShowConfirmation] = useState(false);
  const [passwordError, setPasswordError] = useState("");
  const [notice, setNotice] = useState("");
  const content = copy[view];

  function changeView(next: View) {
    setView(next);
    setShowPassword(false);
    setShowConfirmation(false);
    setPasswordError("");
    setNotice("");
  }

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setNotice("");
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
    setNotice(view === "login"
      ? "นี่เป็นหน้าตาเข้าสู่ระบบตัวอย่าง ยังไม่ได้เชื่อมต่อระบบยืนยันตัวตน"
      : view === "register"
        ? "นี่เป็นฟอร์มสมัครสมาชิกตัวอย่าง ยังไม่ได้สร้างบัญชีจริง"
        : "นี่เป็นฟอร์มรีเซ็ตรหัสผ่านตัวอย่าง ยังไม่ได้ส่งอีเมล");
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
          <FieldGroup className="gap-4">
            {view !== "reset" && (
              <Field className="gap-2">
                <FieldLabel htmlFor="auth-username">ชื่อผู้ใช้</FieldLabel>
                <Input id="auth-username" name="username" placeholder="กรอกชื่อผู้ใช้" autoComplete="username" autoFocus required maxLength={100} />
              </Field>
            )}
            {view !== "login" && (
              <Field className="gap-2">
                <FieldLabel htmlFor="auth-email">อีเมล</FieldLabel>
                <Input id="auth-email" name="email" type="email" placeholder="you@example.com" autoComplete="email" autoFocus={view === "reset"} required maxLength={254} />
              </Field>
            )}
            {view !== "reset" && (
              <Field className="gap-2">
                <FieldLabel htmlFor="auth-password">รหัสผ่าน</FieldLabel>
                <InputGroup className="overflow-hidden">
                  <InputGroupInput id="auth-password" name="password" type={showPassword ? "text" : "password"} placeholder="กรอกรหัสผ่าน" autoComplete={view === "register" ? "new-password" : "current-password"} onInput={() => setPasswordError("")} required maxLength={256} />
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
                  <InputGroupInput id="auth-confirm-password" name="confirmPassword" type={showConfirmation ? "text" : "password"} placeholder="กรอกรหัสผ่านอีกครั้ง" autoComplete="new-password" required maxLength={256} aria-invalid={!!passwordError} aria-describedby={passwordError ? "auth-password-error" : undefined} onInput={() => setPasswordError("")} />
                  <InputGroupAddon align="inline-end">
                    <InputGroupButton size="icon-sm" aria-label={showConfirmation ? "ซ่อนรหัสผ่านยืนยัน" : "แสดงรหัสผ่านยืนยัน"} aria-pressed={showConfirmation} onClick={() => setShowConfirmation(!showConfirmation)}>
                      {showConfirmation ? <EyeOff /> : <Eye />}
                    </InputGroupButton>
                  </InputGroupAddon>
                </InputGroup>
                {passwordError && <FieldError id="auth-password-error">{passwordError}</FieldError>}
              </Field>
            )}
            {view === "login" && <div className="-mt-1 flex justify-end"><Button type="button" variant="link" size="sm" className="h-auto p-0" onClick={() => changeView("reset")}>ลืมรหัสผ่าน?</Button></div>}
            <Button type="submit" className="mt-1 h-10 w-full rounded-xl">{content.submit}</Button>
          </FieldGroup>
        </form>

        {view !== "reset" && <>
          <div className="flex items-center gap-3"><Separator className="flex-1" /><span className="text-xs text-muted-foreground">หรือ</span><Separator className="flex-1" /></div>
          <div className="grid grid-cols-2 gap-3">
            <Button variant="outline" type="button" className="h-11 rounded-xl" onClick={() => setNotice("ปุ่ม Google เป็นตัวอย่าง frontend ยังไม่ได้เชื่อมต่อบัญชี")}>
              <Image src="/auth/google.svg" alt="" width={18} height={18} />Google
            </Button>
            <Button variant="discord" type="button" className="h-11 rounded-xl" onClick={() => setNotice("ปุ่ม Discord เป็นตัวอย่าง frontend ยังไม่ได้เชื่อมต่อบัญชี")}>
              <IconBrandDiscordFilled data-icon="inline-start" />Discord
            </Button>
          </div>
        </>}

        {notice && <Alert role="status"><Info /><AlertDescription>{notice}</AlertDescription></Alert>}

        <p className="text-center text-xs text-muted-foreground sm:text-sm">
          {view === "login" ? "ยังไม่มีบัญชี? " : "มีบัญชีอยู่แล้ว? "}
          <Button type="button" variant="link" className="h-auto p-0" onClick={() => changeView(view === "login" ? "register" : "login")}>
            {view === "login" ? "สมัครสมาชิก" : "เข้าสู่ระบบ"}
          </Button>
        </p>
      </DialogContent>
    </Dialog>
  );
}
