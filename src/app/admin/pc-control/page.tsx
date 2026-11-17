import { PcControl } from "@/components/admin/pc-control";
import { requireAdminSession } from "@/lib/session";

export const instant = false;

export const metadata = {
  title: "PC Control | KamenRaiderPlay",
  robots: { index: false, follow: false },
};

export default async function Page() {
  await requireAdminSession();
  return <PcControl />;
}
