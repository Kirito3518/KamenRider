"use client";

import { useEffect, useState } from "react";
import Image from "next/image";
import { IconBrandDiscordFilled } from "@tabler/icons-react";
import toast from "react-hot-toast";
import { authClient } from "@/lib/auth-client";
import { authError } from "@/lib/auth-errors";
import { Button } from "@/components/ui/button";

export function ProviderButtons() {
  const [enabled, setEnabled] = useState({ google: false, discord: false });
  const [pending, setPending] = useState(false);
  useEffect(() => {
    let active = true;
    fetch("/api/auth-providers").then(res => { if (!res.ok) throw new Error(); return res.json(); })
      .then(value => { if (active) setEnabled(value); }).catch(() => { if (active) toast.error("โหลดช่องทางเข้าสู่ระบบไม่สำเร็จ"); });
    return () => { active = false; };
  }, []);
  async function login(provider: "google" | "discord") {
    setPending(true);
    try {
      const result = await authClient.signIn.social({ provider, callbackURL: "/account?authResult=login", errorCallbackURL: "/login" });
      if (result.error) toast.error(authError(result.error));
    } catch { toast.error("เชื่อมต่อไม่ได้ กรุณาลองใหม่"); }
    finally { setPending(false); }
  }
  return <div className="grid grid-cols-2 gap-3">
    <Button variant="outline" type="button" className="h-11 rounded-xl" disabled={pending || !enabled.google} onClick={() => login("google")} title={!enabled.google ? "ยังไม่ได้ตั้งค่า Google" : undefined}>
      <Image src="/auth/google.svg" alt="" width={18} height={18} />Google{!enabled.google && " (ยังไม่พร้อม)"}
    </Button>
    <Button variant="discord" type="button" className="h-11 rounded-xl" disabled={pending || !enabled.discord} onClick={() => login("discord")} title={!enabled.discord ? "ยังไม่ได้ตั้งค่า Discord" : undefined}>
      <IconBrandDiscordFilled data-icon="inline-start" />Discord{!enabled.discord && " (ยังไม่พร้อม)"}
    </Button>
  </div>;
}
