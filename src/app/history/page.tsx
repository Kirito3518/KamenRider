import type { Metadata } from "next";
import { Suspense } from "react";
import { CustomerHistory } from "@/components/history/customer-history";
import { HistoryPageSkeleton } from "@/components/loading";
import { requireSession } from "@/lib/session";

export const metadata: Metadata = {
  title: "ประวัติการใช้งาน | KamenRaiderPlay",
  description: "ดูประวัติการเติมเงินและประวัติการเช่าเครื่องของคุณ",
};

async function ProtectedHistory() {
  const session = await requireSession();
  return <CustomerHistory initialBalance={session.user.balanceSatang} />;
}

export default function HistoryPage() {
  return (
    <Suspense fallback={<HistoryPageSkeleton />}>
      <ProtectedHistory />
    </Suspense>
  );
}
