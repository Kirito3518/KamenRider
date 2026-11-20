import type { Metadata } from "next";
import { Suspense } from "react";
import { requireSession } from "@/lib/session";
import { TopupPageWithSearch } from "@/components/topup/topup-page";

export const metadata: Metadata = {
  title: "เติมเงิน | KamenRaiderPlay",
  description: "เติมเงินผ่าน TrueMoney Angpao, PromptPay QR และบัญชีธนาคาร",
};

async function ProtectedTopup() {
  const session = await requireSession();
  return <TopupPageWithSearch initialBalance={session.user.balanceSatang} />;
}

export default function TopupRoute() {
  return (
    <Suspense fallback={<p role="status" className="p-6">กำลังตรวจสอบบัญชี…</p>}>
      <ProtectedTopup />
    </Suspense>
  );
}
