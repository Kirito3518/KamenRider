"use client";

import Link from "next/link";
import { useState } from "react";
import { History, MonitorPlay, Wallet } from "lucide-react";
import { motion, useReducedMotion } from "motion/react";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Footer } from "@/components/ui/layout/Footer";
import { Navbar } from "@/components/ui/layout/Navbar";
import { formatBalance } from "@/lib/money";
import { TopupHistoryPanel } from "@/components/history/topup-history-panel";
import { RentalHistoryPanel } from "@/components/history/rental-history-panel";

export function CustomerHistory({
  initialBalance,
}: {
  initialBalance: number;
}) {
  const reduceMotion = useReducedMotion();
  const [balanceSatang, setBalanceSatang] = useState(initialBalance);

  return (
    <div className="flex min-h-screen flex-col">
      <Navbar />
      <main className="mx-auto w-full max-w-6xl flex-1 px-5 pt-28 pb-16 sm:pt-32">
        <motion.header
          initial={reduceMotion ? false : { opacity: 0, y: 10 }}
          animate={{ opacity: 1, y: 0 }}
          transition={reduceMotion ? { duration: 0 } : { duration: 0.28 }}
          className="flex flex-col justify-between gap-4 sm:flex-row sm:items-end"
        >
          <div>
            <h1 className="flex items-center gap-2 text-3xl font-bold">
              <History aria-hidden className="size-7 text-primary" />
              ประวัติการใช้งาน
            </h1>
            <p className="mt-1 text-sm text-muted-foreground">
              ตรวจสอบประวัติการเติมเงินและประวัติการเช่าของคุณย้อนหลัง
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <div className="flex items-center gap-2 rounded-xl border bg-card px-3 py-2 text-sm">
              <Wallet aria-hidden className="size-4 text-muted-foreground" />
              <span className="text-muted-foreground">ยอดเงินคงเหลือ</span>
              <span className="font-semibold tabular-nums" aria-live="polite">
                {formatBalance(balanceSatang)}
              </span>
            </div>
            <Button size="sm" nativeButton={false} render={<Link href="/topup" />}>
              <Wallet data-icon="inline-start" />
              เติมเงิน
            </Button>
          </div>
        </motion.header>

        <Tabs defaultValue="topups" className="mt-6 gap-4">
          <TabsList aria-label="เลือกประวัติ" className="h-auto w-full flex-wrap sm:w-fit">
            <TabsTrigger value="topups">
              <Wallet aria-hidden />
              ประวัติการเติมเงิน
            </TabsTrigger>
            <TabsTrigger value="rentals">
              <MonitorPlay aria-hidden />
              ประวัติการเช่า
            </TabsTrigger>
          </TabsList>
          <TabsContent value="topups">
            <TopupHistoryPanel onBalance={setBalanceSatang} />
          </TabsContent>
          <TabsContent value="rentals">
            <RentalHistoryPanel onBalance={setBalanceSatang} />
          </TabsContent>
        </Tabs>
      </main>
      <Footer />
    </div>
  );
}
