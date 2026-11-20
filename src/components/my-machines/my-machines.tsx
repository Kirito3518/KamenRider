"use client";
import { useEffect, useRef, useState } from "react";
import Link from "next/link";
import { Wallet } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Footer } from "@/components/ui/layout/Footer";
import { Navbar } from "@/components/ui/layout/Navbar";
import { authClient } from "@/lib/auth-client";
import { ParsecConnect } from "@/components/my-machines/parsec-connect";
import { formatBalance } from "@/lib/money";
import {
  formatRentalDateTime,
  formatRentalDuration,
  rentalStateLabel,
  rentalStateTone,
} from "@/lib/rental-history";
import type { Lease, Offer } from "@/lib/machine-contracts";

const foundationSunshineSlot = "foundation-sunshine-single-pending-slot";
const leaseRefreshIntervalMs = 15_000;
const maxExtensionHours = 24;

async function api(path: string, body?: unknown) {
  const r = await fetch(path, {
    method: body ? "POST" : "GET",
    cache: "no-store",
    headers: { "Content-Type": "application/json" },
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await r.json();
  if (!r.ok)
    throw new Error(
      r.status === 401 ? "กรุณาเข้าสู่ระบบ" : data.error || `${r.status}: คำขอไม่สำเร็จ`,
    );
  return data;
}

export function MyMachines() {
  const { data: session, refetch: refetchSession } = authClient.useSession();
  const [leases, setLeases] = useState<Lease[]>([]);
  const [message, setMessage] = useState("กำลังโหลด…");
  const refreshLeasesRef = useRef<(() => Promise<void>) | null>(null);
  const leaseMutationVersion = useRef(0);
  const leaseMutationsInFlight = useRef(0);
  const refreshAfterMutation = useRef(false);
  useEffect(() => {
    let active = true;
    let inFlight = false;
    const refresh = async () => {
      if (!active || document.visibilityState !== "visible" || inFlight) return;
      inFlight = true;
      if (leaseMutationsInFlight.current === 0)
        refreshAfterMutation.current = false;
      const mutationVersionAtStart = leaseMutationVersion.current;
      try {
        const data = (await api("/api/my-leases")) as Lease[];
        if (active) {
          if (
            leaseMutationsInFlight.current > 0 ||
            mutationVersionAtStart !== leaseMutationVersion.current
          )
            return;
          setLeases(data);
          setMessage("");
        }
      } catch (error) {
        if (
          active &&
          leaseMutationsInFlight.current === 0 &&
          mutationVersionAtStart === leaseMutationVersion.current
        )
          setMessage(String(error));
      } finally {
        inFlight = false;
        if (
          active &&
          document.visibilityState === "visible" &&
          leaseMutationsInFlight.current === 0 &&
          refreshAfterMutation.current
        ) {
          refreshAfterMutation.current = false;
          void refresh();
        }
      }
    };
    refreshLeasesRef.current = refresh;
    void refresh();
    const intervalId = window.setInterval(
      () => void refresh(),
      leaseRefreshIntervalMs,
    );
    document.addEventListener("visibilitychange", refresh);
    return () => {
      active = false;
      window.clearInterval(intervalId);
      document.removeEventListener("visibilitychange", refresh);
      if (refreshLeasesRef.current === refresh) refreshLeasesRef.current = null;
    };
  }, []);
  const beginLeaseMutation = () => {
    leaseMutationsInFlight.current += 1;
    leaseMutationVersion.current += 1;
  };
  const endLeaseMutation = () => {
    leaseMutationsInFlight.current -= 1;
    leaseMutationVersion.current += 1;
    if (leaseMutationsInFlight.current === 0) {
      refreshAfterMutation.current = true;
      void refreshLeasesRef.current?.();
    }
  };
  return (
    <div className="flex min-h-screen flex-col">
      <Navbar />
      <main className="mx-auto w-full max-w-4xl flex-1 space-y-5 p-5">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h1 className="text-3xl font-bold">เครื่องของฉัน</h1>
            <p className="text-sm text-muted-foreground">
              รายการเช่าของคุณ — สถานะและการจับคู่มาจากเครื่องจริง
            </p>
          </div>
          <div className="flex items-center gap-2 rounded-xl border px-3 py-2 text-sm">
            <Wallet aria-hidden className="size-4 text-muted-foreground" />
            <span className="text-muted-foreground">ยอดเงินคงเหลือ</span>
            <span className="font-medium tabular-nums">
              {formatBalance(session?.user.balanceSatang ?? 0)}
            </span>
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => void refreshLeasesRef.current?.()}
          >
            รีเฟรช
          </Button>
          <Button variant="outline" size="sm" nativeButton={false} render={<Link href="/history" />}>
            ประวัติการเช่า
          </Button>
          <Button variant="outline" size="sm" nativeButton={false} render={<Link href="/rent" />}>
            เช่าเครื่องเพิ่ม
          </Button>
        </div>
        <p role="status">{message}</p>
        {!message && !leases.length ? (
          <div className="flex flex-col items-center gap-3 rounded-xl border border-dashed p-8 text-center">
            <p>คุณยังไม่มีรายการเช่า</p>
            <Button nativeButton={false} render={<Link href="/rent" />}>เลือกเครื่องที่หน้าเช่า</Button>
          </div>
        ) : null}
        {leases.map((lease) => (
          <LeaseCard
            key={lease.id}
            lease={lease}
            balanceSatang={session?.user.balanceSatang ?? 0}
            onLeaseMutationStart={beginLeaseMutation}
            onLeaseMutationEnd={endLeaseMutation}
            onLeaseUpdate={(updated) => {
              setLeases((current) =>
                current.map((item) =>
                  item.id === updated.id
                    ? { ...item, ...updated, agent: updated.agent ?? item.agent }
                    : item,
                ),
              );
            }}
            onBalanceRefresh={() => void refetchSession()}
          />
        ))}
      </main>
      <Footer />
    </div>
  );
}

function LeaseCard({
  lease,
  balanceSatang,
  onLeaseMutationStart,
  onLeaseMutationEnd,
  onLeaseUpdate,
  onBalanceRefresh,
}: {
  lease: Lease;
  balanceSatang: number;
  onLeaseMutationStart: () => void;
  onLeaseMutationEnd: () => void;
  onLeaseUpdate: (lease: Lease) => void;
  onBalanceRefresh: () => void;
}) {
  const [offer, setOffer] = useState<Offer>(null);
  useEffect(() => {
    if (!offer) return;
    const timer = setTimeout(
      () => setOffer(null),
      Math.max(0, Date.parse(offer.validUntilUtc) - Date.now()),
    );
    return () => clearTimeout(timer);
  }, [offer]);
  const [message, setMessage] = useState("");
  const [command, setCommand] = useState("");
  const [extensionHours, setExtensionHours] = useState(1);
  const [extensionBusy, setExtensionBusy] = useState(false);
  const extensionRequestKey = useRef<string | null>(null);
  const path = `/api/my-leases/${encodeURIComponent(lease.id)}/pair`;
  const validExtensionHours =
    Number.isSafeInteger(extensionHours) &&
    extensionHours >= 1 &&
    extensionHours <= maxExtensionHours;
  const extensionCost = lease.payment
    ? lease.payment.hourlyPriceSatang * extensionHours
    : null;
  const extensionAffordable =
    extensionCost !== null && extensionCost <= balanceSatang;
  const canExtend =
    lease.state === "active" &&
    lease.pendingRevision === null &&
    lease.payment?.extendable === true;

  async function pending() {
    try {
      setOffer(await api(path));
      setMessage("");
    } catch (e) {
      setOffer(null);
      setMessage(String(e));
    }
  }

  async function extend() {
    if (!validExtensionHours || !canExtend || extensionBusy) return;
    setExtensionBusy(true);
    onLeaseMutationStart();
    try {
      const updated = (await api(
        `/api/my-leases/${encodeURIComponent(lease.id)}/extend`,
        {
          additionalSeconds: extensionHours * 3_600,
          expectedRevision: lease.revision,
          idempotencyKey:
            extensionRequestKey.current ??
            (extensionRequestKey.current = crypto.randomUUID()),
        },
      )) as Lease;
      onLeaseUpdate(updated);
      extensionRequestKey.current = null;
      setMessage("ต่อเวลาแล้ว รอเครื่องยืนยันการเปลี่ยนแปลง");
      onBalanceRefresh();
    } catch (error) {
      setMessage(String(error));
    } finally {
      onLeaseMutationEnd();
      setExtensionBusy(false);
    }
  }

  return (
    <section className="space-y-3 rounded-xl border p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-xl font-bold">
          {lease.machineDisplayName || lease.machineId}
        </h2>
        <Badge variant={rentalStateTone(lease.state)}>
          {rentalStateLabel(lease.state)}
        </Badge>
      </div>
      <p>
        Revision {lease.revision}
        {lease.pendingRevision !== null
          ? ` · รอ ACK ${lease.pendingRevision}`
          : ""}
      </p>
      <p>
        สิ้นสุด:{" "}
        {lease.expiresAtUtc
          ? formatRentalDateTime(lease.expiresAtUtc)
          : "ยังไม่เริ่มนับเวลา"}
        {lease.endedAtUtc ? ` · สิ้นสุดจริง ${formatRentalDateTime(lease.endedAtUtc)}` : ""}
      </p>
      <p>
        {lease.agent?.reachability === "reachable"
          ? "Agent ติดต่อได้"
          : lease.agent?.reachability === "unreachable"
            ? "Agent ติดต่อไม่ได้"
            : "ยังไม่ทราบสถานะการเชื่อมต่อของ Agent"}
      </p>
      <p className="text-sm text-muted-foreground">
        Heartbeat ล่าสุด: {lease.agent?.lastSeenAt || "ไม่มีข้อมูล"}
      </p>

      {lease.payment ? (
        <dl className="grid grid-cols-2 gap-3 rounded-lg bg-muted/50 p-3 text-sm sm:grid-cols-4">
          <div>
            <dt className="text-muted-foreground">ราคา/ชม.</dt>
            <dd className="font-medium tabular-nums">
              {formatBalance(lease.payment.hourlyPriceSatang)}
            </dd>
          </div>
          <div>
            <dt className="text-muted-foreground">เวลาที่ชำระ</dt>
            <dd className="font-medium">
              {formatRentalDuration(
                lease.payment.durationSeconds + lease.payment.extensionSeconds,
              )}
            </dd>
          </div>
          <div>
            <dt className="text-muted-foreground">ชำระแล้ว</dt>
            <dd className="font-medium tabular-nums">
              {formatBalance(lease.payment.paidSatang)}
            </dd>
          </div>
          <div>
            <dt className="text-muted-foreground">คืนเงินแล้ว</dt>
            <dd className="font-medium tabular-nums">
              {formatBalance(lease.payment.refundedSatang)}
            </dd>
          </div>
        </dl>
      ) : lease.state !== "pending" ? (
        <p className="text-sm text-muted-foreground">
          รายการนี้ไม่มีข้อมูลการชำระเงิน (สร้างก่อนเปิดระบบเช่าจริง) จึงไม่สามารถต่อเวลาได้
        </p>
      ) : null}

      {lease.state === "active" ? (
        <>
          <p className="break-all">
            {lease.connection?.address
              ? `เชื่อมต่อ: ${lease.connection.address}`
              : "เชื่อมต่อ: ยังไม่มีที่อยู่สาธารณะที่ตรวจสอบแล้ว กรุณาติดต่อผู้ดูแล"}
          </p>
          {lease.connection?.address && lease.connection.stale ? (
            <p className="text-sm text-status-busy">
              ข้อมูลที่อยู่เชื่อมต่ออาจล้าสมัย — ตรวจสอบกับผู้ดูแลก่อนใช้งาน
            </p>
          ) : null}
          <button className="rounded border p-2" onClick={pending}>
            โหลดช่องจับคู่จาก Agent
          </button>
          <p>
            กดโหลดช่องจับคู่ แล้วเริ่ม Pair ใน Moonlight จากนั้นส่ง PIN 4
            หลักที่ Moonlight แสดงภายใน 30 วินาที Sunshine รับคำขอจับคู่ได้ครั้งละหนึ่งรายการ
          </p>
          <form
            className="space-y-3"
            onSubmit={async (e) => {
              e.preventDefault();
              const form = e.currentTarget;
              const d = new FormData(form);
              try {
                const result = await api(path, {
                  pairingId: d.get("pairingId"),
                  pin: d.get("pin"),
                  idempotencyKey: crypto.randomUUID(),
                });
                setCommand(result.commandId);
                setMessage(`สถานะ: ${result.status}`);
                form.reset();
                setOffer(null);
              } catch (error) {
                setMessage(String(error));
              }
            }}
          >
            <label className="block">
              อุปกรณ์ที่จะจับคู่
              <select
                className="ml-2 max-w-full rounded border p-2"
                name="pairingId"
                required
                defaultValue=""
              >
                <option value="" disabled>
                  เลือกช่องจับคู่
                </option>
                {offer?.pairingIds.map((id) => (
                  <option key={id} value={id}>
                    {id === foundationSunshineSlot
                      ? "Sunshine ของเครื่องนี้"
                      : id}
                  </option>
                ))}
              </select>
            </label>
            <label className="block">
              PIN
              <input
                className="ml-2 rounded border p-2"
                name="pin"
                inputMode="numeric"
                pattern="[0-9]{4}"
                maxLength={4}
                autoComplete="off"
                required
              />
            </label>
            <button
              disabled={!offer?.pairingIds.length}
              className="rounded border p-2 disabled:opacity-50"
            >
              ส่ง PIN
            </button>
          </form>
          {command ? (
            <button
              className="rounded border p-2"
              onClick={() =>
                api(`${path}/${encodeURIComponent(command)}`)
                  .then((s) => setMessage(`สถานะ: ${s.status}`))
                  .catch((e) => setMessage(String(e)))
              }
            >
              ตรวจสถานะการจับคู่
            </button>
          ) : null}
          <form
            className="space-y-3 rounded-lg border p-3"
            onSubmit={(event) => {
              event.preventDefault();
              void extend();
            }}
          >
            <label className="block">
              ต่อเวลา (ชั่วโมง)
              <input
                className="ml-2 w-24 rounded border p-2"
                type="number"
                name="extensionHours"
                min={1}
                max={maxExtensionHours}
                step={1}
                value={extensionHours}
                onChange={(event) => {
                  extensionRequestKey.current = null;
                  setExtensionHours(Number(event.target.value));
                }}
                required
              />
            </label>
            {extensionCost !== null ? (
              <p className="text-sm">
                ค่าใช้จ่ายครั้งนี้{" "}
                <span className="font-medium tabular-nums">
                  {formatBalance(extensionCost)}
                </span>{" "}
                · ยอดเงินคงเหลือ{" "}
                <span className="font-medium tabular-nums">
                  {formatBalance(balanceSatang)}
                </span>
              </p>
            ) : null}
            {extensionCost !== null && !extensionAffordable ? (
              <p className="text-sm text-status-maintenance">
                ยอดเงินไม่เพียงพอ{" "}
                <Link href="/topup" className="underline underline-offset-4">
                  ไปหน้าเติมเงิน
                </Link>
              </p>
            ) : null}
            <button
              disabled={
                extensionBusy ||
                !canExtend ||
                !validExtensionHours ||
                !extensionAffordable
              }
              className="rounded border p-2 disabled:opacity-50"
            >
              ต่อเวลา
            </button>
            <p className="text-sm text-muted-foreground">
              ระบบหักค่าเช่าตามชั่วโมงที่ต่อทันที และจะคืนเงินตามเวลาที่ไม่ได้ใช้ให้อัตโนมัติเมื่อสิ้นสุดการเช่า
            </p>
          </form>
          <ParsecConnect leaseId={lease.id} initial={lease.parsec ?? null} />
        </>
      ) : lease.state === "pending" ? (
        <p>ระบบกำลังยืนยันการเช่ากับเครื่อง กรุณารอสักครู่แล้วรีเฟรชอีกครั้ง</p>
      ) : (
        <p>รอเครื่องและสิทธิ์พร้อมก่อนจับคู่</p>
      )}
      <p role="status">{message}</p>
    </section>
  );
}
