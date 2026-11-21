"use client";

import type { ReactNode } from "react";
import { Loader2 } from "lucide-react";
import { motion, useReducedMotion, type Variants } from "motion/react";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

/**
 * Shared skeleton + motion primitives.
 *
 * - Every visual skeleton respects `prefers-reduced-motion`: the fade-in stagger
 *   and the shimmer sweep are skipped, and the base `animate-pulse` is disabled
 *   with `motion-reduce:animate-none`.
 * - `LoadingRegion` is the only accessibility container: `role="status"`,
 *   `aria-busy="true"` and an sr-only Thai announcement. Composites themselves
 *   are illustrative only (`aria-hidden`).
 */

const staggerItemVariants: Variants = {
  hidden: { opacity: 0.35 },
  show: {
    opacity: 1,
    transition: { duration: 0.32, ease: "easeOut" },
  },
};

const staggerContainerVariants = (delay: number): Variants => ({
  hidden: {},
  show: {
    transition: { staggerChildren: 0.04, delayChildren: delay },
  },
});

/** Visual-only stagger wrapper (no live region) for skeletons inside an existing `LoadingRegion`. */
export function SkeletonGroup({
  children,
  className,
  delay = 0,
}: {
  children: ReactNode;
  className?: string;
  delay?: number;
}) {
  const reduceMotion = useReducedMotion();
  if (reduceMotion) {
    return (
      <div aria-hidden="true" className={className}>
        {children}
      </div>
    );
  }
  return (
    <motion.div
      aria-hidden="true"
      className={className}
      initial="hidden"
      animate="show"
      variants={staggerContainerVariants(delay)}
    >
      {children}
    </motion.div>
  );
}

/**
 * Accessible loading container. Use once per loading area; nested skeletons stay
 * purely visual so screen readers get a single polite announcement.
 */
export function LoadingRegion({
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
  const reduceMotion = useReducedMotion();
  if (reduceMotion) {
    return (
      <div role="status" aria-busy="true" aria-live="polite" className={className}>
        <span className="sr-only">{label}</span>
        {children}
      </div>
    );
  }
  return (
    <motion.div
      role="status"
      aria-busy="true"
      aria-live="polite"
      className={className}
      initial="hidden"
      animate="show"
      variants={staggerContainerVariants(delay)}
    >
      <span className="sr-only">{label}</span>
      {children}
    </motion.div>
  );
}

/**
 * A single skeleton block whose dimensions are supplied by `className`.
 * Uses the project `Skeleton` surface plus a motion shimmer sweep.
 */
export function SkeletonBlock({
  className,
  radius = "rounded-2xl",
  shimmer = true,
}: {
  className?: string;
  radius?: string;
  shimmer?: boolean;
}) {
  const reduceMotion = useReducedMotion();
  return (
    <motion.div
      aria-hidden="true"
      variants={staggerItemVariants}
      className={cn("relative overflow-hidden", radius, className)}
    >
      <Skeleton className="absolute inset-0 h-full w-full rounded-[inherit] motion-reduce:animate-none" />
      {shimmer && !reduceMotion ? (
        <motion.span
          aria-hidden="true"
          className="pointer-events-none absolute inset-y-0 -left-1/3 w-1/3"
          style={{
            backgroundImage:
              "linear-gradient(90deg, transparent, color-mix(in oklch, var(--background) 72%, transparent), transparent)",
          }}
          initial={{ x: "-150%" }}
          animate={{ x: "450%" }}
          transition={{ repeat: Infinity, repeatDelay: 0.45, duration: 1.45, ease: "linear" }}
        />
      ) : null}
    </motion.div>
  );
}

/**
 * Inline, span-based skeleton for text lines (safe inside `<p>` where a `<div>`
 * would break the content model). Uses a plain pulsing surface.
 */
export function InlineSkeleton({
  lines = 1,
  widths,
  className,
  lineClassName,
}: {
  lines?: number;
  widths?: string[];
  className?: string;
  lineClassName?: string;
}) {
  return (
    <span aria-hidden="true" className={cn("flex flex-col gap-2", className)}>
      {Array.from({ length: lines }).map((_, index) => (
        <span
          key={index}
          className={cn(
            "block h-3.5 animate-pulse rounded-full bg-muted motion-reduce:animate-none",
            widths?.[index] ?? (lines === 1 ? "w-40" : "w-full"),
            lineClassName,
          )}
        />
      ))}
    </span>
  );
}

/**
 * Meaningful action-progress message (publishing, verifying a slip, loading a
 * dialog on demand). Unlike page skeletons these keep the real status text, but
 * get an animated spinner that is hidden from assistive tech and reduced-motion users.
 */
export function ProgressStatus({
  children,
  className,
  live = true,
}: {
  children: ReactNode;
  className?: string;
  live?: boolean;
}) {
  return (
    <p
      role="status"
      aria-live={live ? "polite" : undefined}
      className={cn("flex items-start gap-2 text-sm text-muted-foreground", className)}
    >
      <Loader2
        aria-hidden="true"
        className="mt-0.5 size-4 shrink-0 animate-spin motion-reduce:animate-none"
      />
      <span>{children}</span>
    </p>
  );
}
