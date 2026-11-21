"use client";

import type { ReactNode } from "react";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { cn } from "@/lib/utils";
import {
  LoadingRegion,
  SkeletonBlock,
  SkeletonGroup,
} from "@/components/loading/skeleton-primitives";

/**
 * Layout skeletons that mirror the real content dimensions of the app so that
 * swapping skeleton -> content does not shift the page.
 */

const DEFAULT_COLUMN_WIDTHS = [
  "w-24",
  "w-32",
  "w-20",
  "w-24",
  "w-20",
  "w-16",
  "w-20",
  "w-8",
];

function HeaderActionSkeletons({ count = 2 }: { count?: number }) {
  return (
    <div className="flex shrink-0 flex-wrap items-center gap-2">
      {Array.from({ length: count }).map((_, index) => (
        <SkeletonBlock key={index} className="h-8 w-24" radius="rounded-full" />
      ))}
    </div>
  );
}

/** Matches `AdminPageHeader` / a customer page title block. */
export function PageHeaderSkeleton({
  className,
  withIcon = true,
  withDescription = true,
  withActions = false,
  actionCount = 2,
  titleWidth = "w-48",
  descriptionWidth = "w-80",
}: {
  className?: string;
  withIcon?: boolean;
  withDescription?: boolean;
  withActions?: boolean;
  actionCount?: number;
  titleWidth?: string;
  descriptionWidth?: string;
}) {
  return (
    <div
      aria-hidden="true"
      className={cn(
        "flex flex-col justify-between gap-4 xl:flex-row xl:items-center",
        className,
      )}
    >
      <div className="min-w-0">
        <div className="flex min-h-7 items-center gap-2">
          {withIcon ? (
            <SkeletonBlock className="size-5" radius="rounded-md" />
          ) : null}
          <SkeletonBlock className={cn("h-6", titleWidth)} radius="rounded-full" />
        </div>
        {withDescription ? (
          <SkeletonBlock
            className={cn("mt-2 h-4 max-w-full", descriptionWidth)}
            radius="rounded-full"
          />
        ) : null}
      </div>
      {withActions ? <HeaderActionSkeletons count={actionCount} /> : null}
    </div>
  );
}

/** Matches a row of `AdminStatCard`s (label, icon, value, hint). */
export function StatGridSkeleton({
  count = 4,
  columns = "sm:grid-cols-2 xl:grid-cols-4",
  className,
  cardClassName,
  withIcon = true,
}: {
  count?: number;
  columns?: string;
  className?: string;
  cardClassName?: string;
  withIcon?: boolean;
}) {
  return (
    <div aria-hidden="true" className={cn("grid gap-3", columns, className)}>
      {Array.from({ length: count }).map((_, index) => (
        <div
          key={index}
          className={cn(
            "min-w-0 rounded-2xl border bg-card p-4",
            cardClassName,
          )}
        >
          <div className="flex items-start justify-between gap-2">
            <SkeletonBlock className="h-3.5 w-24" radius="rounded-full" />
            {withIcon ? <SkeletonBlock className="size-8" radius="rounded-xl" /> : null}
          </div>
          <SkeletonBlock className="mt-4 h-7 w-20" />
          <SkeletonBlock className="mt-2 h-3 w-32 max-w-full" radius="rounded-full" />
        </div>
      ))}
    </div>
  );
}

/**
 * Table skeleton that renders real table markup so header heights, cell padding
 * and borders match the loaded table exactly.
 */
export function TableSkeleton({
  rows = 5,
  columns = 6,
  columnWidths,
  showHeader = true,
  footer = false,
  frame = true,
  className,
}: {
  rows?: number;
  columns?: number;
  columnWidths?: string[];
  showHeader?: boolean;
  footer?: boolean;
  frame?: boolean;
  className?: string;
}) {
  const widths = columnWidths ?? DEFAULT_COLUMN_WIDTHS;
  return (
    <div
      aria-hidden="true"
      className={cn(frame && "overflow-hidden rounded-2xl border bg-card", className)}
    >
      <div className="overflow-x-auto">
        <Table>
          {showHeader ? (
            <TableHeader>
              <TableRow>
                {Array.from({ length: columns }).map((_, index) => (
                  <TableHead key={index}>
                    <SkeletonBlock
                      className={cn("h-3.5", widths[index % widths.length])}
                      radius="rounded-full"
                    />
                  </TableHead>
                ))}
              </TableRow>
            </TableHeader>
          ) : null}
          <TableBody>
            {Array.from({ length: rows }).map((_, rowIndex) => (
              <TableRow key={rowIndex}>
                {Array.from({ length: columns }).map((_, columnIndex) => (
                  <TableCell key={columnIndex}>
                    <SkeletonBlock
                      className={cn(
                        "h-4",
                        columnIndex === 0
                          ? "w-28"
                          : widths[columnIndex % widths.length],
                      )}
                      radius="rounded-full"
                    />
                  </TableCell>
                ))}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
      {footer ? (
        <div className="flex flex-wrap items-center justify-between gap-3 border-t p-3">
          <SkeletonBlock className="h-3 w-40" radius="rounded-full" />
          <div className="flex items-center gap-2">
            <SkeletonBlock className="h-8 w-20" radius="rounded-full" />
            <SkeletonBlock className="h-8 w-20" radius="rounded-full" />
          </div>
        </div>
      ) : null}
    </div>
  );
}

/** Matches a responsive grid of `Card`s (charts, account cards, channel cards). */
export function CardsGridSkeleton({
  count = 4,
  columns = "sm:grid-cols-2 xl:grid-cols-3",
  rows = 3,
  className,
  cardClassName,
}: {
  count?: number;
  columns?: string;
  rows?: number;
  className?: string;
  cardClassName?: string;
}) {
  return (
    <div aria-hidden="true" className={cn("grid gap-4", columns, className)}>
      {Array.from({ length: count }).map((_, index) => (
        <div
          key={index}
          className={cn("min-w-0 rounded-2xl border bg-card p-4", cardClassName)}
        >
          <SkeletonBlock className="h-4 w-40" radius="rounded-full" />
          <SkeletonBlock className="mt-2 h-3 w-56 max-w-full" radius="rounded-full" />
          {rows > 0 ? (
            <div className="mt-4 flex flex-col gap-2">
              {Array.from({ length: rows }).map((_, rowIndex) => (
                <SkeletonBlock
                  key={rowIndex}
                  className={cn("h-3.5", rowIndex % 2 ? "w-4/5" : "w-full")}
                  radius="rounded-full"
                />
              ))}
            </div>
          ) : null}
        </div>
      ))}
    </div>
  );
}

/** Matches label + control form rows (and an optional action row). */
export function FormSkeleton({
  fields = 3,
  columns = 1,
  withActions = true,
  className,
  maxWidth = "max-w-2xl",
}: {
  fields?: number;
  columns?: 1 | 2;
  withActions?: boolean;
  className?: string;
  maxWidth?: string;
}) {
  return (
    <div
      aria-hidden="true"
      className={cn(
        "grid gap-4",
        columns === 2 && "sm:grid-cols-2",
        maxWidth,
        className,
      )}
    >
      {Array.from({ length: fields }).map((_, index) => (
        <div key={index} className="flex flex-col gap-2">
          <SkeletonBlock className="h-3.5 w-28" radius="rounded-full" />
          <SkeletonBlock className="h-9 w-full" radius="rounded-xl" />
        </div>
      ))}
      {withActions ? (
        <div
          className={cn(
            "flex flex-wrap items-center gap-2",
            columns === 1 && "mt-1",
          )}
        >
          <SkeletonBlock className="h-9 w-32" radius="rounded-full" />
          <SkeletonBlock className="h-9 w-24" radius="rounded-full" />
        </div>
      ) : null}
    </div>
  );
}

/** Matches a `Card` with a few content rows, e.g. a settings/warning card. */
export function CardSkeleton({
  rows = 3,
  className,
  cardClassName,
}: {
  rows?: number;
  className?: string;
  cardClassName?: string;
}) {
  return (
    <div
      aria-hidden="true"
      className={cn("rounded-2xl border bg-card p-4", cardClassName, className)}
    >
      <SkeletonBlock className="h-4 w-36" radius="rounded-full" />
      <SkeletonBlock className="mt-2 h-3 w-56 max-w-full" radius="rounded-full" />
      <div className="mt-4 flex flex-col gap-2">
        {Array.from({ length: rows }).map((_, index) => (
          <SkeletonBlock
            key={index}
            className={cn("h-3.5", index % 2 ? "w-3/4" : "w-full")}
            radius="rounded-full"
          />
        ))}
      </div>
    </div>
  );
}

/** Matches the feature/version bullet lists used on the Agent pages. */
export function ListSkeleton({
  count = 4,
  className,
  withDivider = true,
}: {
  count?: number;
  className?: string;
  withDivider?: boolean;
}) {
  return (
    <div
      aria-hidden="true"
      className={cn(
        "flex flex-col",
        withDivider && "divide-y rounded-xl border",
        className,
      )}
    >
      {Array.from({ length: count }).map((_, index) => (
        <div key={index} className="flex flex-col gap-1.5 p-3">
          <div className="flex flex-wrap items-center gap-2">
            <SkeletonBlock className="h-4 w-24" radius="rounded-full" />
            <SkeletonBlock className="h-5 w-16" radius="rounded-full" />
            <SkeletonBlock className="h-3 w-28" radius="rounded-full" />
          </div>
          <SkeletonBlock className="h-3.5 w-full" radius="rounded-full" />
        </div>
      ))}
    </div>
  );
}

/** Dialog body skeleton: title, description and a few card-shaped rows. */
export function DialogSkeleton({
  lines = 3,
  showHeader = true,
  className,
}: {
  lines?: number;
  showHeader?: boolean;
  className?: string;
}) {
  return (
    <div aria-hidden="true" className={cn("flex flex-col gap-4", className)}>
      {showHeader ? (
        <div className="flex flex-col gap-2">
          <SkeletonBlock className="h-5 w-48" radius="rounded-full" />
          <SkeletonBlock className="h-4 w-64 max-w-full" radius="rounded-full" />
        </div>
      ) : null}
      <div className="flex flex-col gap-3">
        <SkeletonBlock className="h-16 w-full" />
        {Array.from({ length: Math.max(1, lines - 1) }).map((_, index) => (
          <SkeletonBlock
            key={index}
            className={cn(index === lines - 2 ? "h-40 w-full" : "h-24 w-full")}
          />
        ))}
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <SkeletonBlock className="h-9 w-32" radius="rounded-full" />
        <SkeletonBlock className="h-9 w-24" radius="rounded-full" />
      </div>
    </div>
  );
}

/**
 * Page-level loading wrapper: a single accessible `role="status"` region around
 * composed layout skeletons. `className` controls page column/gap utilities.
 */
export function PageSkeleton({
  label = "กำลังโหลดข้อมูล",
  children,
  className,
  delay = 0,
}: {
  label?: string;
  children: ReactNode;
  className?: string;
  delay?: number;
}) {
  return (
    <LoadingRegion
      label={label}
      delay={delay}
      className={cn("flex min-w-0 flex-col gap-4", className)}
    >
      {children}
    </LoadingRegion>
  );
}

/** Visual-only stagger group for skeletons embedded in an existing live region. */
export function SkeletonCards({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) {
  return <SkeletonGroup className={className}>{children}</SkeletonGroup>;
}
