import { CardsGridSkeleton, SitePageSkeleton } from "@/components/loading";

export default function Loading() {
  return (
    <SitePageSkeleton label="กำลังโหลดบัญชี" maxWidth="max-w-xl">
      <CardsGridSkeleton
        count={1}
        columns="grid-cols-1"
        rows={7}
        cardClassName="min-h-[430px]"
      />
    </SitePageSkeleton>
  );
}
