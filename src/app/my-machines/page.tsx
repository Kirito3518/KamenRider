import type { Metadata } from "next";
import { MyMachines } from "@/components/my-machines/my-machines";
import { Suspense } from "react";
import { requireSession } from "@/lib/session";

export const metadata: Metadata = {
  title: "เครื่องของฉัน | KamenRaiderPlay",
  description: "ดูเครื่องที่เช่าและข้อมูลสำหรับเชื่อมต่อเครื่องของคุณ",
};

async function ProtectedMachines() {
  await requireSession();
  return <MyMachines />;
}

export default function MyMachinesPage() {
  return <Suspense fallback={<p role="status">กำลังตรวจสอบบัญชี…</p>}><ProtectedMachines /></Suspense>;
}
