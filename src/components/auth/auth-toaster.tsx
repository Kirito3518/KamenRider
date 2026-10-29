"use client";

import { useEffect } from "react";
import toast, { Toaster } from "react-hot-toast";

export function AuthToaster() {
  useEffect(() => {
    const url = new URL(window.location.href);
    const result = url.searchParams.get("authResult");
    const error = url.searchParams.get("error");
    if (error) {
      toast.error(error === "account_not_linked"
        ? "กรุณาล็อกอินด้วยบัญชีเดิม แล้วเชื่อม provider ในหน้าบัญชี"
        : "เชื่อมต่อผู้ให้บริการไม่สำเร็จ กรุณาลองอีกครั้ง");
    } else if (result) {
      toast.success(result === "linked" ? "เชื่อมบัญชีสำเร็จ" : "เข้าสู่ระบบสำเร็จ");
    }
    if (result || error) {
      url.searchParams.delete("authResult");
      url.searchParams.delete("error");
      url.searchParams.delete("error_description");
      window.history.replaceState(null, "", url.pathname + url.search + url.hash);
    }
  }, []);
  return <Toaster position="top-center" toastOptions={{ duration: 4000, style: { background: "var(--card)", color: "var(--card-foreground)", border: "1px solid var(--border)", fontFamily: "inherit" } }} />;
}
