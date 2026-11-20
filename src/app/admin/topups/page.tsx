import type { Metadata } from "next";
import { TopupsManager } from "@/components/admin/topups/topups-manager";

export const instant = false;

export const metadata: Metadata = {
  title: "ประวัติการเติมเงิน | KamenRaiderPlay",
  description: "รายการเติมเงินทั้งหมดของผู้ใช้ พร้อมผลตรวจสลิปและประวัติการเรียกผู้ให้บริการ",
  robots: { index: false, follow: false },
};

export default function Page() {
  return <TopupsManager />;
}
