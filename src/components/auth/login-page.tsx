"use client";

import { useRef, useState } from "react";
import { AuthDialog } from "@/components/auth/auth-dialog";
import { Button } from "@/components/ui/button";
import { Navbar } from "@/components/ui/layout/Navbar";

export function LoginPage() {
  const [open, setOpen] = useState(true);
  const trigger = useRef<HTMLButtonElement>(null);
  return <><Navbar /><main className="mx-auto flex min-h-screen max-w-xl flex-col items-center justify-center gap-5 px-5"><h1 className="text-2xl font-bold">เข้าสู่ระบบเพื่อใช้งานบัญชีของคุณ</h1><Button ref={trigger} onClick={() => setOpen(true)}>เข้าสู่ระบบ / สมัครสมาชิก</Button></main><AuthDialog open={open} onOpenChange={setOpen} returnFocusRef={trigger} /></>;
}
