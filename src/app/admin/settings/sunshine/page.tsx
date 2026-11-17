import type { Metadata } from "next";
import { SunshineCredentialManager } from "@/components/admin/sunshine-credential-manager";
import { requireAdminSession } from "@/lib/session";

export const instant = false;

export const metadata: Metadata = {
  title: "ตั้งค่า Sunshine | KamenRaiderPlay",
  robots: { index: false, follow: false },
};

export default async function Page() {
  await requireAdminSession();
  return <SunshineCredentialManager />;
}
