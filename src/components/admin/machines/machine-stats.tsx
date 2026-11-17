"use client";

import { motion, useReducedMotion } from "motion/react";
import { CircleCheck, Clock3, Server, ShieldCheck } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { AdminStatCard, AdminStatGrid } from "@/components/admin/admin-page";
import { Skeleton } from "@/components/ui/skeleton";
import { isAgentOnline } from "@/lib/agent-releases";
import { isApprovedMachine, isPendingMachine } from "@/lib/machine-display";
import type { Machine } from "@/lib/machine-contracts";

type StatsTone = "primary" | "available" | "busy" | "maintenance" | "muted";

type StatsCard = {
  key: string;
  label: string;
  hint: React.ReactNode;
  value: React.ReactNode;
  icon: LucideIcon;
  tone: StatsTone;
};

export function MachineStats({ machines, now }: { machines: Machine[] | null; now: number }) {
  const reduceMotion = useReducedMotion();
  const loading = machines === null;
  const total = machines?.length ?? 0;
  const pending = machines?.filter(isPendingMachine).length ?? 0;
  const approved = machines?.filter(isApprovedMachine) ?? [];
  const online = approved.filter((machine) => isAgentOnline(machine, now)).length;
  const offline = approved.length - online;
  const rentable = approved.filter((machine) => machine.canRent).length;
  const cards: StatsCard[] = [
    {
      key: "total",
      label: "เครื่องทั้งหมด",
      hint: "เครื่องที่ลงทะเบียนในระบบ",
      value: loading ? <Skeleton className="h-8 w-16" /> : total.toLocaleString("th-TH"),
      icon: Server,
      tone: "muted",
    },
    {
      key: "pending",
      label: "รออนุมัติ",
      hint: "ต้องตรวจ fingerprint ก่อนอนุมัติ",
      value: loading ? <Skeleton className="h-8 w-16" /> : pending.toLocaleString("th-TH"),
      icon: Clock3,
      tone: "busy",
    },
    {
      key: "approved",
      label: "อนุมัติแล้ว",
      hint: loading ? (
        "กำลังอ่านสถานะ Agent"
      ) : (
        <>
          ออนไลน์ {online} ·{" "}
          <span className={offline ? "text-status-maintenance" : undefined}>ออฟไลน์ {offline}</span>
        </>
      ),
      value: loading ? <Skeleton className="h-8 w-16" /> : approved.length.toLocaleString("th-TH"),
      icon: ShieldCheck,
      tone: "available",
    },
    {
      key: "rentable",
      label: "พร้อมให้เช่า",
      hint: loading ? "กำลังอ่านสถานะการเช่า" : `ยังไม่พร้อม ${approved.length - rentable} เครื่อง`,
      value: loading ? <Skeleton className="h-8 w-16" /> : rentable.toLocaleString("th-TH"),
      icon: CircleCheck,
      tone: "primary",
    },
  ];
  return (
    <AdminStatGrid>
      {cards.map((card, index) => (
        <motion.div
          key={card.key}
          className="min-w-0"
          initial={reduceMotion ? false : { opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          transition={reduceMotion ? { duration: 0 } : { duration: 0.25, delay: index * 0.05 }}
        >
          <AdminStatCard
            label={card.label}
            value={card.value}
            hint={card.hint}
            icon={card.icon}
            tone={card.tone}
          />
        </motion.div>
      ))}
    </AdminStatGrid>
  );
}
