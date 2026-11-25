import { Suspense } from "react";
import { connection } from "next/server";
import { LayoutDashboard } from "lucide-react";
import { AdminPage, AdminPageHeader } from "@/components/admin/admin-page";
import { OverviewControls } from "@/components/admin/overview/overview-controls";
import {
  OverviewMachinesSection,
  OverviewMembersSection,
  OverviewRentalsSection,
  OverviewStatusSection,
  OverviewTopupsSection,
  OverviewTotalsSection,
  type OverviewSectionSearchParams,
} from "@/components/admin/overview/overview-sections";
import {
  CardsGridSkeleton,
  SkeletonBlock,
  StatGridSkeleton,
  TableSkeleton,
} from "@/components/loading";
import { flattenOverviewSearchParams, resolveOverviewRange } from "@/lib/admin-overview";

/**
 * `/admin/overview` — read-only system dashboard.
 *
 * The page shell (title + controls) is static; each section is an independent
 * Suspense boundary with its own server loader. The Controller-backed machine
 * section therefore cannot delay the member/top-up/rental sections, and the
 * whole page still renders without a configured Controller.
 */

async function OverviewHeaderControls({
  searchParams,
}: {
  searchParams: OverviewSectionSearchParams;
}) {
  const filters = flattenOverviewSearchParams(await searchParams);
  // The period picker resolves the default range with Date.now(); make that
  // request-time before the Cache Components shell prerender evaluates it.
  await connection();
  const range = resolveOverviewRange(filters);
  return <OverviewControls period={range.period} from={range.from} to={range.to} />;
}

function TableCardSkeleton({ columns }: { columns: number }) {
  return (
    <div className="rounded-2xl border bg-card p-4">
      <SkeletonBlock className="h-5 w-40" radius="rounded-full" />
      <SkeletonBlock className="mt-2 h-3 w-56 max-w-full" radius="rounded-full" />
      <div className="mt-4">
        <TableSkeleton rows={5} columns={columns} frame={false} />
      </div>
    </div>
  );
}

export default function AdminOverviewPage({ searchParams }: PageProps<"/admin/overview">) {
  return (
    <AdminPage>
      <AdminPageHeader
        icon={LayoutDashboard}
        title="ภาพรวมระบบ"
        description="สรุปสมาชิก การเติมเงิน การเช่า และสถานะเครื่อง · เวลาไทย"
        actions={
          <Suspense
            fallback={<SkeletonBlock className="h-24 w-full max-w-sm xl:w-80" radius="rounded-xl" />}
          >
            <OverviewHeaderControls searchParams={searchParams} />
          </Suspense>
        }
      />
      <Suspense
        fallback={<SkeletonBlock className="h-4 w-80 max-w-full" radius="rounded-full" />}
      >
        <OverviewStatusSection searchParams={searchParams} />
      </Suspense>
      <Suspense fallback={<StatGridSkeleton count={4} />}>
        <OverviewTotalsSection searchParams={searchParams} />
      </Suspense>
      <Suspense
        fallback={
          <div className="flex flex-col gap-3">
            <SkeletonBlock className="h-64 w-full" />
            <TableCardSkeleton columns={3} />
          </div>
        }
      >
        <OverviewMembersSection searchParams={searchParams} />
      </Suspense>
      <Suspense
        fallback={
          <div className="flex flex-col gap-3">
            <StatGridSkeleton count={5} columns="sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-5" />
            <CardsGridSkeleton count={2} columns="xl:grid-cols-2" rows={0} cardClassName="h-80" />
            <TableCardSkeleton columns={6} />
          </div>
        }
      >
        <OverviewTopupsSection searchParams={searchParams} />
      </Suspense>
      <Suspense
        fallback={
          <div className="flex flex-col gap-3">
            <StatGridSkeleton count={3} columns="sm:grid-cols-2 xl:grid-cols-3" />
            <CardsGridSkeleton count={2} columns="xl:grid-cols-2" rows={0} cardClassName="h-80" />
            <TableCardSkeleton columns={6} />
          </div>
        }
      >
        <OverviewRentalsSection searchParams={searchParams} />
      </Suspense>
      <Suspense
        fallback={
          <div className="flex flex-col gap-3">
            <StatGridSkeleton count={4} />
            <CardsGridSkeleton count={2} columns="xl:grid-cols-2" rows={0} cardClassName="h-80" />
          </div>
        }
      >
        <OverviewMachinesSection />
      </Suspense>
    </AdminPage>
  );
}
