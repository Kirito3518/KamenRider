"use client";

import { useEffect, useRef, useState } from "react";
import { MonitorX } from "lucide-react";
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
import {
  filterMachines,
  type MachineFilter,
  type MachineSort,
  type RentalMachine,
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
  const [selected, setSelected] = useState<RentalMachine | null>(null);
  const rentTriggerRef = useRef<HTMLButtonElement>(null);
  const [rentalMachines, setMachines] = useState<
    import("@/lib/rental-machines").RentalMachine[]
  >([]);
  const [loadMessage, setLoadMessage] = useState("");
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    let active = true;
    let current: AbortController | undefined;
    async function refresh() {
      current?.abort();
      const abort = new AbortController();
      current = abort;
      try {
        const response = await fetch("/api/machines", { cache: "no-store", signal: abort.signal });
        if (!response.ok) throw new Error();
        const machines = await response.json();
        if (active && !abort.signal.aborted) {
          setMachines(machines);
          setSelected((selected) => selected ? machines.find((machine: RentalMachine) => machine.id === selected.id && machine.status === "available") ?? null : null);
          setLoadMessage("");
          setLoading(false);
        }
      } catch {
        if (active && !abort.signal.aborted) {
          setMachines([]);
          setSelected(null);
          setLoadMessage("ติดต่อระบบเครื่องไม่ได้ กรุณาลองใหม่");
          setLoading(false);
        }
      }
    }
    void refresh();
    const interval = window.setInterval(refresh, 15000);
    window.addEventListener("focus", refresh);
    return () => {
      active = false;
      current?.abort();
      window.clearInterval(interval);
      window.removeEventListener("focus", refresh);
    };
  }, []);
  const available = rentalMachines.filter(
    (machine) => machine.status === "available",
  ).length;
  const counts = {
    all: rentalMachines.length,
    available,
    unavailable: rentalMachines.length - available,
  };
  const results = filterMachines(rentalMachines, filter, sort);

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

        {loadMessage ? (
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
                            setSelected(selectedMachine);
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
        onClose={() => setSelected(null)}
        returnFocusRef={rentTriggerRef}
      />
    </div>
  );
}
