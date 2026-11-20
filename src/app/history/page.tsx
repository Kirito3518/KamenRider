import type { Metadata } from "next";
import { Suspense } from "react";
import { RentalHistory } from "@/components/my-rentals/rental-history";
import { requireSession } from "@/lib/session";

export const metadata: Metadata = {
  title: "ประวัติการเช่า | KamenRaiderPlay",
  description: "ดูประวัติการเช่าเครื่อง ยอดชำระ และเงินคืนย้อนหลังของคุณ",
};

async function ProtectedHistory() {
  await requireSession();
  return <RentalHistory />;
}

export default function HistoryPage() {
  return (
    <Suspense fallback={<p role="status" className="p-6">กำลังตรวจสอบบัญชี…</p>}>
      <ProtectedHistory />
    </Suspense>
  );
}
