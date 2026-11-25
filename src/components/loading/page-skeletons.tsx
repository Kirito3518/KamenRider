"use client";

import type { ReactNode } from "react";
import { Navbar } from "@/components/ui/layout/Navbar";
import { Footer } from "@/components/ui/layout/Footer";
import { cn } from "@/lib/utils";
import {
  CardsGridSkeleton,
  FormSkeleton,
  PageHeaderSkeleton,
  PageSkeleton,
  StatGridSkeleton,
  TableSkeleton,
} from "@/components/loading/skeleton-layouts";
import {
  InlineSkeleton,
  SkeletonBlock,
} from "@/components/loading/skeleton-primitives";

/**
 * Full customer-facing page chrome (Navbar + centered main + Footer) used by
 * route `loading.tsx` files for pages that render their own Navbar/Footer.
 */
export function SitePageSkeleton({
  label = "กำลังโหลดข้อมูล",
  maxWidth = "max-w-6xl",
  className,
  children,
}: {
  label?: string;
  maxWidth?: string;
  className?: string;
  children: ReactNode;
}) {
  return (
    <div className="flex min-h-screen flex-col">
      <Navbar />
      <main
        className={cn(
          "mx-auto w-full flex-1 px-5 pt-28 pb-16 sm:pt-32",
          maxWidth,
          className,
        )}
      >
        <PageSkeleton label={label}>{children}</PageSkeleton>
      </main>
      <Footer />
    </div>
  );
}

/**
 * Shell-only fallback for the Admin layout while the admin session is being
 * resolved. Mirrors the real sidebar + sticky header + padded main column.
 */
export function AdminShellSkeleton({
  label = "กำลังตรวจสอบสิทธิ์ผู้ดูแล",
}: {
  label?: string;
}) {  return (
    <div className="flex min-h-screen w-full">
      <aside
        aria-hidden="true"
        className="hidden w-64 shrink-0 flex-col gap-3 border-r bg-card p-3 md:flex"
      >
        <div className="flex h-12 items-center gap-2 px-2">
          <SkeletonBlock className="size-6" radius="rounded-lg" />
          <SkeletonBlock className="h-4 w-28" radius="rounded-full" />
        </div>
        <div className="flex flex-col gap-2">
          {Array.from({ length: 8 }).map((_, index) => (
            <SkeletonBlock
              key={index}
              className="h-9 w-full"
              radius="rounded-xl"
            />
          ))}
        </div>
      </aside>
      <div className="flex min-w-0 flex-1 flex-col">
        <header
          aria-hidden="true"
          className="flex h-16 shrink-0 items-center justify-between gap-3 border-b px-4 sm:px-6"
        >
          <div className="flex items-center gap-3">
            <SkeletonBlock className="size-8" radius="rounded-lg" />
            <SkeletonBlock className="h-4 w-28" radius="rounded-full" />
          </div>
          <div className="flex items-center gap-2">
            <SkeletonBlock className="size-9" radius="rounded-full" />
            <SkeletonBlock className="h-9 w-28" radius="rounded-full" />
          </div>
        </header>
        <main className="flex w-full flex-1 flex-col gap-6 p-4 sm:p-6">
          <PageSkeleton label={label} className="gap-4">
            <PageHeaderSkeleton withActions actionCount={1} />
            <StatGridSkeleton count={4} columns="sm:grid-cols-2 xl:grid-cols-4" />
            <SkeletonBlock className="h-72 w-full" />
          </PageSkeleton>
        </main>
      </div>
    </div>
  );
}

/** Loading preset for `/history` (header, tabs, history table). */
export function HistoryPageSkeleton() {
  return (
    <SitePageSkeleton label="กำลังตรวจสอบบัญชี" maxWidth="max-w-6xl">
      <PageHeaderSkeleton withActions actionCount={2} titleWidth="w-52" />
      <div className="flex flex-col gap-4">
        <SkeletonBlock className="h-9 w-full max-w-xs" radius="rounded-xl" />
        <TableSkeleton rows={5} columns={7} footer />
      </div>
    </SitePageSkeleton>
  );
}

/** Loading preset for `/topup` (header, channel card with three channel rows). */
export function TopupPageSkeleton() {
  return (
    <SitePageSkeleton label="กำลังตรวจสอบบัญชี" maxWidth="max-w-3xl">
      <PageHeaderSkeleton withActions actionCount={2} titleWidth="w-56" />
      <div className="rounded-3xl border bg-card p-4">
        <SkeletonBlock className="h-4 w-40" radius="rounded-full" />
        <div className="mt-4 flex flex-col gap-3">
          <SkeletonBlock className="h-16 w-full" />
          <SkeletonBlock className="h-16 w-full" />
          <SkeletonBlock className="h-16 w-full" />
        </div>
      </div>
    </SitePageSkeleton>
  );
}

/** Loading preset for `/account` (single account card). */
export function AccountPageSkeleton() {
  return (
    <PageSkeleton label="กำลังโหลดบัญชี" className="gap-6">
      <CardsGridSkeleton
        count={1}
        columns="grid-cols-1"
        rows={7}
        cardClassName="min-h-[430px]"
      />
    </PageSkeleton>
  );
}

/** Loading preset for admin pages made of a header, stat grid and one table card. */
export function AdminStatsTablePageSkeleton({
  label,
}: {
  label: string;
}) {
  return (
    <PageSkeleton label={label}>
      <PageHeaderSkeleton withActions actionCount={1} />
      <StatGridSkeleton count={3} columns="sm:grid-cols-3" />
      <div className="rounded-2xl border bg-card p-4">
        <SkeletonBlock className="h-5 w-36" radius="rounded-full" />
        <SkeletonBlock className="mt-2 h-3 w-56 max-w-full" radius="rounded-full" />
        <div className="mt-4">
          <TableSkeleton rows={5} columns={6} frame={false} />
        </div>
      </div>
    </PageSkeleton>
  );
}

/** Loading preset for `/admin/users` (header, filters, KPIs, chart, member table). */
export function AdminUsersPageSkeleton() {
  return (
    <PageSkeleton label="กำลังโหลดสมาชิก">
      <PageHeaderSkeleton />
      <div className="rounded-2xl border bg-card p-4">
        <SkeletonBlock className="h-10 w-full max-w-sm" radius="rounded-xl" />
      </div>
      <StatGridSkeleton count={4} />
      <CardsGridSkeleton count={1} columns="grid-cols-1" rows={0} cardClassName="h-64" />
      <div className="rounded-2xl border bg-card p-4">
        <SkeletonBlock className="h-10 w-full" radius="rounded-xl" />
        <div className="mt-4">
          <TableSkeleton rows={5} columns={6} frame={false} />
        </div>
      </div>
    </PageSkeleton>
  );
}

/** Loading preset for `/admin/rentals` (filters, KPI row, charts, rental table). */
export function AdminRentalsPageSkeleton() {
  return (
    <PageSkeleton label="กำลังโหลดประวัติการเช่า">
      <PageHeaderSkeleton withActions actionCount={1} />
      <div className="rounded-2xl border bg-card p-4">
        <SkeletonBlock className="h-10 w-full max-w-xl" radius="rounded-xl" />
      </div>
      <StatGridSkeleton count={5} columns="sm:grid-cols-2 xl:grid-cols-5" />
      <CardsGridSkeleton count={2} columns="xl:grid-cols-2" rows={0} cardClassName="h-72" />
      <div className="rounded-2xl border bg-card p-4">
        <SkeletonBlock className="h-5 w-40" radius="rounded-full" />
        <div className="mt-4">
          <TableSkeleton rows={5} columns={9} frame={false} />
        </div>
      </div>
    </PageSkeleton>
  );
}

/** Loading preset for `/admin/topups` (KPI row, charts, top-up table). */
export function AdminTopupsPageSkeleton() {
  return (
    <PageSkeleton label="กำลังโหลดประวัติการเติมเงิน">
      <PageHeaderSkeleton withActions actionCount={2} />
      <StatGridSkeleton
        count={6}
        columns="sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-6"
      />
      <CardsGridSkeleton count={2} columns="xl:grid-cols-2" rows={0} cardClassName="h-80" />
      <div className="rounded-2xl border bg-card p-4">
        <SkeletonBlock className="h-10 w-full" radius="rounded-xl" />
        <div className="mt-4">
          <TableSkeleton rows={5} columns={8} frame={false} />
        </div>
      </div>
    </PageSkeleton>
  );
}

/** Loading preset for `/admin/overview` (status line, KPI rows, charts and lists). */
export function AdminOverviewPageSkeleton() {
  return (
    <PageSkeleton label="กำลังโหลดภาพรวมระบบ">
      <PageHeaderSkeleton withActions actionCount={2} />
      <SkeletonBlock className="h-4 w-80 max-w-full" radius="rounded-full" />
      <StatGridSkeleton count={4} />
      <CardsGridSkeleton count={1} columns="grid-cols-1" rows={0} cardClassName="h-64" />
      <div className="rounded-2xl border bg-card p-4">
        <SkeletonBlock className="h-5 w-40" radius="rounded-full" />
        <SkeletonBlock className="mt-2 h-3 w-56 max-w-full" radius="rounded-full" />
        <div className="mt-4">
          <TableSkeleton rows={5} columns={3} frame={false} />
        </div>
      </div>
      <StatGridSkeleton count={5} columns="sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-5" />
      <CardsGridSkeleton count={2} columns="xl:grid-cols-2" rows={0} cardClassName="h-80" />
      <div className="rounded-2xl border bg-card p-4">
        <SkeletonBlock className="h-5 w-40" radius="rounded-full" />
        <div className="mt-4">
          <TableSkeleton rows={5} columns={6} frame={false} />
        </div>
      </div>
      <StatGridSkeleton count={3} columns="sm:grid-cols-2 xl:grid-cols-3" />
      <CardsGridSkeleton count={2} columns="xl:grid-cols-2" rows={0} cardClassName="h-80" />
      <StatGridSkeleton count={4} />
      <CardsGridSkeleton count={2} columns="xl:grid-cols-2" rows={0} cardClassName="h-80" />
    </PageSkeleton>
  );
}

/** Loading preset for `/admin/agent-releases` (KPI row and release lists). */
export function AgentReleasesPageSkeleton() {
  return (
    <PageSkeleton label="กำลังโหลดข้อมูล Agent">
      <PageHeaderSkeleton />
      <StatGridSkeleton count={4} />
      <CardsGridSkeleton count={3} columns="md:grid-cols-2 xl:grid-cols-3" rows={3} />
      <div className="rounded-2xl border bg-card p-4">
        <SkeletonBlock className="h-5 w-44" radius="rounded-full" />
        <FormSkeleton fields={4} columns={2} className="mt-4" withActions={false} />
      </div>
    </PageSkeleton>
  );
}

/** Loading preset for `/admin/settings/topup` (tabs and settings cards). */
export function TopupSettingsPageSkeleton() {
  return (
    <PageSkeleton label="กำลังโหลดการตั้งค่าเติมเงิน">
      <PageHeaderSkeleton withActions actionCount={1} />
      <div className="flex flex-wrap items-center gap-2">
        {Array.from({ length: 5 }).map((_, index) => (
          <SkeletonBlock key={index} className="h-8 w-24" radius="rounded-xl" />
        ))}
      </div>
      <div className="grid items-start gap-4 md:grid-cols-2 xl:grid-cols-12">
        <SkeletonBlock className="h-36 md:col-span-2 xl:col-span-7" />
        <SkeletonBlock className="h-36 md:col-span-2 xl:col-span-5" />
        <SkeletonBlock className="h-36 md:col-span-2 xl:col-span-4" />
        <SkeletonBlock className="h-48 md:col-span-2 xl:col-span-8" />
      </div>
    </PageSkeleton>
  );
}

/** Loading preset for `/admin/settings/sunshine` (status card and credential form). */
export function SunshineSettingsPageSkeleton() {
  return (
    <PageSkeleton label="กำลังโหลดการตั้งค่า Sunshine">
      <PageHeaderSkeleton withActions actionCount={1} />
      <div className="rounded-2xl border bg-card p-4">
        <div className="grid gap-4 sm:grid-cols-2">
          {Array.from({ length: 4 }).map((_, index) => (
            <div key={index} className="flex flex-col gap-1">
              <SkeletonBlock className="h-3 w-28" radius="rounded-full" />
              <SkeletonBlock className="h-5 w-40 max-w-full" />
            </div>
          ))}
        </div>
      </div>
      <FormSkeleton fields={4} columns={2} />
      <InlineSkeleton lines={3} widths={["w-full", "w-4/5", "w-2/3"]} />
    </PageSkeleton>
  );
}
