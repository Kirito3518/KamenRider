import type { Metadata } from "next";
import { MyMachines } from "@/components/my-machines/my-machines";
import { Suspense } from "react";
import { requireSession } from "@/lib/session";
import { CardsGridSkeleton, SitePageSkeleton } from "@/components/loading";

export const metadata: Metadata = {
  title: "เครื่องของฉัน | KamenRaiderPlay",
  description: "ดูเครื่องที่เช่าและข้อมูลสำหรับเชื่อมต่อเครื่องของคุณ",
};

async function ProtectedMachines() {
  await requireSession();
  return <MyMachines />;
}

export default function MyMachinesPage() {
  return (
    <Suspense
      fallback={
        <SitePageSkeleton label="กำลังโหลดเครื่องของฉัน">
          <CardsGridSkeleton count={1} rows={1} columns="grid-cols-1" cardClassName="h-[360px]" />
        </SitePageSkeleton>
      }
    >
      <ProtectedMachines />
    </Suspense>
  );
}
