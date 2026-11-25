"use client";

import { useEffect, useRef, useState } from "react";
import { MonitorX } from "lucide-react";
import toast from "react-hot-toast";
import { Navbar } from "@/components/ui/layout/Navbar";
import { Footer } from "@/components/ui/layout/Footer";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty";
import { useLandingMotion } from "@/hooks/use-landing-motion";
import { useRentalMachines } from "@/hooks/use-rental-machines";
import {
  filterMachines,
  type MachineFilter,
  type MachineSort,
} from "@/lib/rental-machines";
import { MachineCard } from "@/components/rent/machine-card";
import { RentalDialog } from "@/components/rent/rental-dialog";
import { CardsGridSkeleton, LoadingRegion } from "@/components/loading";

const filters: { value: MachineFilter; label: string }[] = [
  { value: "all", label: "ทั้งหมด" },
  { value: "available", label: "พร้อมเช่า" },
  { value: "unavailable", label: "ไม่ว่าง" },
];
const sortOptions: { value: MachineSort; label: string }[] = [
  { value: "name", label: "ชื่อเครื่อง" },
  { value: "price-asc", label: "ราคาต่ำไปสูง" },
  { value: "price-desc", label: "ราคาสูงไปต่ำ" },
];

export function RentalBrowser() {
  const pageRef = useLandingMotion();
  const [filter, setFilter] = useState<MachineFilter>("all");
  const [sort, setSort] = useState<MachineSort>("name");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const rentTriggerRef = useRef<HTMLButtonElement>(null);
  const { machines: rentalMachines, loading, live, error } = useRentalMachines();
  const selected =
    selectedId === null
      ? null
      : rentalMachines.find((machine) => machine.id === selectedId) ?? null;
  const selectedMissing = selectedId !== null && selected === null;

  // A machine can drop out of the public list (maintenance, disabled, price
  // change, Controller offline). Close the dialog and tell the customer. The
  // state update is deferred one tick so it is a reaction to the machine list
  // changing rather than a synchronous state write inside the effect body.
  useEffect(() => {
    if (!selectedMissing) return;
    const timeout = window.setTimeout(() => {
      setSelectedId(null);
      toast.error("เครื่องที่เลือกไม่พร้อมให้เช่าแล้ว กรุณาเลือกเครื่องอื่น");
    }, 0);
    return () => window.clearTimeout(timeout);
  }, [selectedMissing]);

  const available = rentalMachines.filter(
    (machine) => machine.status === "available",
  ).length;
  const counts = {
    all: rentalMachines.length,
    available,
    unavailable: rentalMachines.length - available,
  };
  const results = filterMachines(rentalMachines, filter, sort);
  const loadMessage = error ?? "";

  function resetFilters() {
    setFilter("all");
    setSort("name");
  }

  return (
    <div ref={pageRef} className="min-h-dvh bg-background text-foreground">
      <Navbar />
      <main className="mx-auto min-h-[calc(100dvh-82px)] max-w-[1440px] px-5 pt-10 pb-20 sm:px-6 sm:pt-12 lg:pt-14">
        <header
          data-hero-reveal
          className="flex flex-col justify-between gap-8 sm:flex-row sm:items-end"
        >
          <div>
            <h1 className="text-4xl leading-[1.2] font-bold tracking-tight sm:text-5xl lg:text-6xl">
              เลือกคอมที่ใช่
              <br />
              แล้วไปเล่น<span className="text-primary">เกมที่ชอบ</span>
            </h1>
            <div className="mt-5 flex flex-wrap items-center gap-2">
              <p className="text-sm text-muted-foreground">
                เลือกสเปกและราคาที่เหมาะกับคุณ แต่ละเครื่องมีราคาของตัวเอง
              </p>
            </div>
          </div>
          <dl className="flex shrink-0 gap-7 sm:pb-1">
            <div>
              <dt className="text-xs text-muted-foreground">พร้อมเช่า</dt>
              <dd className="mt-1 flex items-center gap-2 text-3xl font-semibold text-status-available">
                <span className="size-2 rounded-full bg-status-available" />
                {available}
              </dd>
            </div>
            <div>
              <dt className="text-xs text-muted-foreground">เครื่องทั้งหมด</dt>
              <dd className="mt-1 text-3xl font-semibold">
                {rentalMachines.length}
              </dd>
            </div>
          </dl>
        </header>

        {!loading && rentalMachines.length > 0 ? (
          loadMessage ? (
            <p role="status" className="mt-5 text-sm text-destructive">
              {loadMessage}
            </p>
          ) : (
            <p
              role="status"
              className="mt-5 flex items-center gap-2 text-xs text-muted-foreground"
            >
              <span
                aria-hidden
                className={
                  live
                    ? "size-2 rounded-full bg-status-available"
                    : "size-2 rounded-full bg-muted-foreground"
                }
              />
              {live
                ? "เชื่อมต่อแบบเรียลไทม์ · สถานะอัปเดตทันที"
                : "โหมดสำรอง · อัปเดตทุก 3 วินาที"}
            </p>
          )
        ) : null}
        {!loading && rentalMachines.length === 0 && loadMessage ? (
          <p role="status" className="mt-5 text-sm text-destructive">
            {loadMessage}
          </p>
        ) : null}
        {loading ? (
          <LoadingRegion label="กำลังโหลดรายการเครื่อง" className="mt-9">
            <CardsGridSkeleton
              count={5}
              rows={1}
              columns="grid-cols-1 min-[520px]:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5"
              cardClassName="h-[330px]"
            />
          </LoadingRegion>
        ) : (
        <Tabs
          value={filter}
          onValueChange={(value) => setFilter(value as MachineFilter)}
          className="mt-9 gap-6 sm:mt-12 sm:gap-9"
        >
          <div
            data-hero-reveal
            className="flex flex-wrap items-center justify-between gap-3 sm:flex-nowrap"
          >
            <TabsList
              aria-label="กรองสถานะเครื่อง"
              className="h-13! w-full sm:w-auto"
            >
              {filters.map(({ value, label }) => (
                <TabsTrigger key={value} value={value}>
                  {label}
                  <span className="text-xs opacity-60">{counts[value]}</span>
                </TabsTrigger>
              ))}
            </TabsList>
            <Select
              items={sortOptions}
              value={sort}
              onValueChange={(value) => setSort(value ?? "name")}
            >
              <SelectTrigger
                aria-label="เรียงลำดับเครื่อง"
                className="h-13! w-full rounded-2xl sm:w-40"
              >
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectGroup>
                  {sortOptions.map(({ value, label }) => (
                    <SelectItem key={value} value={value}>
                      {label}
                    </SelectItem>
                  ))}
                </SelectGroup>
              </SelectContent>
            </Select>
          </div>
          {filters.map(({ value }) => (
            <TabsContent key={value} value={value}>
              {filter === value && (
                <>
                  <p aria-live="polite" className="sr-only">
                    พบ {results.length} เครื่อง
                  </p>
                  {results.length > 0 ? (
                    <div
                      className="grid grid-cols-1 gap-5 min-[520px]:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5"
                      data-testid="machine-grid"
                    >
                      {results.map((machine) => (
                        <MachineCard
                          key={machine.id}
                          machine={machine}
                          onSelect={(selectedMachine, trigger) => {
                            rentTriggerRef.current = trigger;
                            setSelectedId(selectedMachine.id);
                          }}
                        />
                      ))}
                    </div>
                  ) : (
                    <Empty className="min-h-80">
                      <EmptyHeader>
                        <EmptyMedia variant="icon">
                          <MonitorX />
                        </EmptyMedia>
                        <EmptyTitle>ไม่มีเครื่องในสถานะนี้</EmptyTitle>
                        <EmptyDescription>
                          ลองเลือกสถานะอื่นเพื่อดูเครื่องที่เปิดให้เช่า
                        </EmptyDescription>
                      </EmptyHeader>
                      <EmptyContent>
                        <Button variant="outline" onClick={resetFilters}>
                          ล้างตัวกรองทั้งหมด
                        </Button>
                      </EmptyContent>
                    </Empty>
                  )}
                </>
              )}
            </TabsContent>
          ))}
        </Tabs>
        )}
      </main>
      <Footer />
      <RentalDialog
        machine={selected}
        available={selected ? selected.status === "available" : true}
        onClose={() => setSelectedId(null)}
        returnFocusRef={rentTriggerRef}
      />
    </div>
  );
}
