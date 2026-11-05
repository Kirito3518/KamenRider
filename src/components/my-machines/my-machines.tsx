"use client";
import { useEffect, useState } from "react";
import type { Lease, Offer } from "@/lib/machine-contracts";
import { Navbar } from "@/components/ui/layout/Navbar";
import { Footer } from "@/components/ui/layout/Footer";
const foundationSunshineSlot = "foundation-sunshine-single-pending-slot";
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
  useEffect(() => {
    let active = true;
    api("/api/my-leases")
      .then((data) => {
        if (active) {
          setLeases(data);
          setMessage("");
        }
      })
      .catch((e) => {
        if (active) setMessage(String(e));
      });
    return () => {
      active = false;
    };
  }, []);
  return (
    <div className="flex min-h-screen flex-col">
      <Navbar />
      <main className="mx-auto w-full max-w-4xl flex-1 space-y-5 p-5">
        <h1 className="text-3xl font-bold">เครื่องของฉัน</h1>
        <p>ระบบทดลอง — สิทธิ์และการจับคู่ขึ้นกับสถานะจริงของ Agent</p>
        <p role="status">{message}</p>
        <button
          className="rounded border p-2"
          onClick={() =>
            api("/api/my-leases")
              .then(setLeases)
              .catch((e) => setMessage(String(e)))
          }
        >
          รีเฟรช
        </button>
        {!message && !leases.length ? (
          <p>คุณยังไม่มีสิทธิ์ทดลอง ติดต่อผู้ดูแล</p>
        ) : null}
        {leases.map((lease) => (
          <LeaseCard key={lease.id} lease={lease} />
        ))}
      </main>
      <Footer />
    </div>
  );
}
function LeaseCard({ lease }: { lease: Lease }) {
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
        </>
      ) : (
        <p>รอเครื่องและสิทธิ์พร้อมก่อนจับคู่</p>
      )}
      <p role="status">{message}</p>
    </section>
  );
}
