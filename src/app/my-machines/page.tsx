import type { Metadata } from "next";
import { MyMachines } from "@/components/my-machines/my-machines";

export const metadata: Metadata = {
  title: "เครื่องของฉัน | KamenRaiderPlay",
  description: "ดูเครื่องที่เช่าและข้อมูลสำหรับเชื่อมต่อเครื่องของคุณ",
};

export default function MyMachinesPage() {
  return <MyMachines />;
}
