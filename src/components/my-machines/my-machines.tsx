"use client";
import { useEffect, useRef, useState } from "react";
import type { Lease, Offer } from "@/lib/machine-contracts";
import { Navbar } from "@/components/ui/layout/Navbar";
import { Footer } from "@/components/ui/layout/Footer";
const foundationSunshineSlot = "foundation-sunshine-single-pending-slot";
const leaseRefreshIntervalMs = 15_000;
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
      r.status === 401 ? "กรุณาเข้าสู่ระบบ" : `${r.status}: ${data.error}`,
    );
  return data;
}
export function MyMachines() {
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
        <h1 className="text-3xl font-bold">เครื่องของฉัน</h1>
        <p>ระบบทดลอง — สิทธิ์และการจับคู่ขึ้นกับสถานะจริงของ Agent</p>
        <p role="status">{message}</p>
        <button
          className="rounded border p-2"
          onClick={() => void refreshLeasesRef.current?.()}
        >
          รีเฟรช
        </button>
        {!message && !leases.length ? (
          <p>คุณยังไม่มีสิทธิ์ทดลอง ติดต่อผู้ดูแล</p>
        ) : null}
        {leases.map((lease) => (
          <LeaseCard
            key={lease.id}
            lease={lease}
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
          />
        ))}
      </main>
      <Footer />
    </div>
  );
}
function LeaseCard({
  lease,
  onLeaseMutationStart,
  onLeaseMutationEnd,
  onLeaseUpdate,
}: {
  lease: Lease;
  onLeaseMutationStart: () => void;
  onLeaseMutationEnd: () => void;
  onLeaseUpdate: (lease: Lease) => void;
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
  const [extensionMinutes, setExtensionMinutes] = useState(60);
  const [extensionBusy, setExtensionBusy] = useState(false);
  const extensionRequestKey = useRef<string | null>(null);
  const path = `/api/my-leases/${encodeURIComponent(lease.id)}/pair`;
  async function pending() {
    try {
      setOffer(await api(path));
      setMessage("");
    } catch (e) {
      setOffer(null);
      setMessage(String(e));
    }
  }
  return (
    <section className="space-y-3 rounded-xl border p-4">
      <h2 className="text-xl font-bold">{lease.machineId}</h2>
      <p>
        สถานะ: {lease.state} · Revision {lease.revision}
        {lease.pendingRevision !== null
          ? ` · รอ ACK ${lease.pendingRevision}`
          : ""}
      </p>
      <p>สิ้นสุด: {lease.expiresAtUtc || "ยังไม่เริ่มนับเวลา"}</p>
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
      <p className="text-sm text-muted-foreground">
        {lease.connection?.address
          ? `${lease.connection.address}${lease.connection.stale ? " (ข้อมูลเก่า — ตรวจสอบกับผู้ดูแล)" : ""}`
          : "ยังไม่มีที่อยู่สาธารณะที่ตรวจสอบแล้ว กรุณาติดต่อผู้ดูแล"}
      </p>
      {lease.state === "active" ? (
        <>
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
            onSubmit={async (event) => {
              event.preventDefault();
              setExtensionBusy(true);
              onLeaseMutationStart();
              try {
                const updated = (await api(
                  `/api/my-leases/${encodeURIComponent(lease.id)}/extend`,
                  {
                    additionalSeconds: extensionMinutes * 60,
                    expectedRevision: lease.revision,
                    idempotencyKey:
                      extensionRequestKey.current ??
                      (extensionRequestKey.current = crypto.randomUUID()),
                  },
                )) as Lease;
                onLeaseUpdate(updated);
                extensionRequestKey.current = null;
                setMessage("ส่งคำขอต่อเวลาทดลองแล้ว รอเครื่องยืนยัน");
              } catch (error) {
                setMessage(String(error));
              } finally {
                onLeaseMutationEnd();
                setExtensionBusy(false);
              }
            }}
          >
            <label className="block">
              เพิ่มเวลา (นาที)
              <input
                className="ml-2 w-24 rounded border p-2"
                type="number"
                name="extensionMinutes"
                min={1}
                max={1440}
                step={1}
                value={extensionMinutes}
                onChange={(event) => {
                  extensionRequestKey.current = null;
                  setExtensionMinutes(Number(event.target.value));
                }}
                required
              />
            </label>
            <button
              disabled={extensionBusy || lease.pendingRevision !== null}
              className="rounded border p-2 disabled:opacity-50"
            >
              ต่อเวลาทดลอง
            </button>
            <p className="text-sm text-muted-foreground">
              ช่วงทดลองยังไม่มีการหักเงิน การต่อเวลาจะมีผลเมื่อเครื่องยืนยันคำสั่งแล้ว
            </p>
          </form>
        </>
      ) : (
        <p>รอเครื่องและสิทธิ์พร้อมก่อนจับคู่</p>
      )}
      <p role="status">{message}</p>
    </section>
  );
}
