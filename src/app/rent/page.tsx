import type { Metadata } from "next";
import { RentalBrowser } from "@/components/rent/rental-browser";

export const metadata: Metadata = {
  title: "เช่าคอมพิวเตอร์ | KamenRaiderPlay",
  description: "เลือกเครื่องคอมพิวเตอร์ตามสเปก ราคา และสถานะที่คุณต้องการ",
};

export default function RentPage() {
  return <RentalBrowser />;
}
