import type { LucideIcon } from "lucide-react";
import {
  Card,
  CardAction,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { cn } from "@/lib/utils";

/**
 * Shared Admin proportions, matching the `/admin/users` dashboard:
 * full-width column with 16px gaps, a compact page header, 4-up stat cards and
 * flat `rounded-2xl` card surfaces. Keep these components server-safe so both
 * Server Components (overview/balances) and Client Components can use them.
 */

export function AdminPage({ className, ...props }: React.ComponentProps<"div">) {
  return <div className={cn("flex min-w-0 flex-col gap-4", className)} {...props} />;
}

export function AdminPageHeader({
  icon: Icon,
  title,
  titleSuffix,
  description,
  actions,
  className,
}: {
  icon: LucideIcon;
  title: string;
  titleSuffix?: React.ReactNode;
  description?: React.ReactNode;
  actions?: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("flex flex-col justify-between gap-4 xl:flex-row xl:items-center", className)}>
      <div className="min-w-0">
        <h1 className="flex flex-wrap items-center gap-2 text-xl font-semibold">
          <Icon aria-hidden className="size-5 text-primary" />
          {title}
          {titleSuffix}
        </h1>
        {description ? <p className="mt-1 text-sm text-muted-foreground">{description}</p> : null}
      </div>
      {actions ? <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div> : null}
    </div>
  );
}

export function AdminStatGrid({ className, ...props }: React.ComponentProps<"div">) {
  return <div className={cn("grid gap-3 sm:grid-cols-2 xl:grid-cols-4", className)} {...props} />;
}

const statToneClassName = {
  primary: "bg-primary/10 text-primary",
  available: "bg-status-available/10 text-status-available",
  busy: "bg-status-busy/10 text-status-busy",
  maintenance: "bg-status-maintenance/10 text-status-maintenance",
  muted: "bg-muted text-foreground",
} as const;

export function AdminStatCard({
  label,
  value,
  hint,
  icon: Icon,
  tone = "primary",
}: {
  label: string;
  value: React.ReactNode;
  hint?: React.ReactNode;
  icon: LucideIcon;
  tone?: keyof typeof statToneClassName;
}) {
  return (
    <section className="h-full min-w-0">
      <AdminCard className="h-full">
        <CardHeader>
          <CardTitle className="text-sm font-normal text-muted-foreground">{label}</CardTitle>
          <CardAction>
            <span
              className={cn(
                "flex size-8 items-center justify-center rounded-xl",
                statToneClassName[tone],
              )}
            >
              <Icon aria-hidden className="size-4" />
            </span>
          </CardAction>
        </CardHeader>
        <CardContent>
          <div className="text-2xl font-semibold tracking-tight break-words tabular-nums">{value}</div>
          {hint ? <div className="mt-1 text-xs text-muted-foreground">{hint}</div> : null}
        </CardContent>
      </AdminCard>
    </section>
  );
}

/** Flat card surface matching the users page: 16px padding, 2xl radius, border instead of ring. */
export function AdminCard({ className, ...props }: React.ComponentProps<typeof Card>) {
  return (
    <Card
      size="sm"
      className={cn("rounded-2xl border shadow-none ring-0", className)}
      {...props}
    />
  );
}
