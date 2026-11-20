import type { Metadata } from "next";
import { TopupSettingsManager } from "@/components/admin/settings/topup-settings-manager";
import { requireAdminSession } from "@/lib/session";

export const instant = false;

export const metadata: Metadata = {
  title: "ตั้งค่าเติมเงิน | KamenRaiderPlay",
  robots: { index: false, follow: false },
};

export default async function Page() {
  await requireAdminSession();
  return <TopupSettingsManager />;
}
