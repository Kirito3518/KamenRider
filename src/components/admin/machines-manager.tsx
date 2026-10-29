"use client";
import { useEffect, useState } from "react";
import type { Machine } from "@/lib/machine-contracts";
async function request(path: string, body?: unknown, method = "POST") {
  const r = await fetch(path, {
    method,
    cache: "no-store",
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const data = await r.json();
  if (!r.ok) throw new Error(`${r.status}: ${data.error}`);
  return data;
}
export function MachinesManager() {
  const [machines, setMachines] = useState<Machine[]>([]);
  const [message, setMessage] = useState("กำลังโหลด…");
  async function load() {
    try {
      setMachines(await request("/api/admin/machines", undefined, "GET"));
      setMessage("");
    } catch (e) {
      setMessage(String(e));
    }
  }
  useEffect(() => {
    let active = true;
    fetch("/api/admin/machines", { cache: "no-store" })
      .then(async (r) => {
        if (!r.ok) throw new Error("โหลดเครื่องไม่ได้");
        const data = await r.json();
        if (active) {
          setMachines(data);
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
  async function act(path: string, body: unknown, method = "POST") {
    try {
      setMessage("กำลังส่ง…");
      await request(path, body, method);
      await load();
      setMessage("บันทึกคำขอแล้ว ตรวจสถานะ Agent ก่อนใช้งาน");
    } catch (e) {
      setMessage(String(e));
    }
  }
  return (
    <main className="mx-auto max-w-5xl space-y-5 p-5">
      <h1 className="text-3xl font-bold">เครื่องและ Agent</h1>
      <p>
        ระบบทดลอง ไม่มีการหักยอดเงิน ความพร้อมจริงยังต้องผ่านการตรวจสอบ Agent
      </p>
      <button className="rounded border p-2" onClick={load}>
        รีเฟรช
      </button>
      <p role="status">{message}</p>
      {!machines.length && !message ? <p>ยังไม่มีเครื่องลงทะเบียน</p> : null}
      {machines.map((m) => (
        <section
          key={`${m.machineId}-${m.configRevision}`}
          className="space-y-3 rounded-xl border p-4"
        >
          <h2 className="font-bold">
            {m.machineId} · {m.approvalStatus}
          </h2>
          <p>
            Readiness: {m.readiness} · Config revision: {m.configRevision} ·
            ACK: {m.appliedConfigRevision ?? "ยังไม่ยืนยัน"}{" "}
            {m.appliedConfigRevision === m.configRevision
              ? "(ตรงกับ config)"
              : "(รอ Agent ACK)"}
          </p>
          <p className="break-all font-mono text-sm">
            Fingerprint: {m.publicKeyFingerprint || "ยังไม่มีข้อมูลยืนยัน"}
          </p>
          <form
            className="grid gap-3 sm:grid-cols-2"
            onSubmit={(e) => {
              e.preventDefault();
              const d = new FormData(e.currentTarget);
              void act(
                `/api/admin/machines/${encodeURIComponent(m.machineId)}`,
                {
                  displayName: d.get("name"),
                  hourlyPriceMinorUnits: Number(d.get("price")),
                  moonlightPort: Number(d.get("moon")),
                  sunshinePort: Number(d.get("sun")),
                  expectedRevision: m.configRevision,
                },
                "PATCH",
              );
            }}
          >
            <label>
              ชื่อ
              <Input name="name" value={m.displayName} />
            </label>
            <label>
              ราคา (สตางค์/ชั่วโมง)
              <Input
                name="price"
                value={m.hourlyPriceMinorUnits}
                type="number"
              />
            </label>
            <label>
              Moonlight port
              <Input name="moon" value={m.moonlightPort} type="number" />
            </label>
            <label>
              Management port
              <Input name="sun" value={m.sunshinePort} type="number" />
            </label>
            <button className="rounded border p-2">บันทึก metadata</button>
          </form>
          {m.approvalStatus === "pending" ? (
            <form
              className="space-y-2"
              onSubmit={(e) => {
                e.preventDefault();
                const fingerprint = String(
                  new FormData(e.currentTarget).get("fingerprint"),
                );
                void act(
                  `/api/admin/machines/${encodeURIComponent(m.machineId)}/approve`,
                  { fingerprint },
                );
              }}
            >
              <label>
                พิมพ์ fingerprint ที่ตรวจสอบจากเครื่องจริง
                <Input name="fingerprint" value="" />
              </label>
              <button
                disabled={!m.publicKeyFingerprint}
                className="rounded border p-2 disabled:opacity-50"
              >
                อนุมัติ
              </button>
              <button
                type="button"
                className="ml-3 rounded border p-2"
                onClick={() =>
                  act(
                    `/api/admin/machines/${encodeURIComponent(m.machineId)}/reject`,
                    { fingerprint: m.publicKeyFingerprint },
                  )
                }
              >
                ปฏิเสธ pending
              </button>
            </form>
          ) : (
            <button
              className="rounded border p-2"
              onClick={() =>
                act(
                  `/api/admin/machines/${encodeURIComponent(m.machineId)}/wake`,
                  {},
                )
              }
            >
              Wake-on-LAN
            </button>
          )}
        </section>
      ))}
      <form
        className="grid gap-3 rounded border p-4"
        onSubmit={(e) => {
          e.preventDefault();
          const d = new FormData(e.currentTarget);
          void act("/api/admin/trial-leases", {
            userId: d.get("user"),
            machineId: d.get("machine"),
            durationSeconds: Number(d.get("duration")),
            idempotencyKey: crypto.randomUUID(),
          });
        }}
      >
        <h2>ให้สิทธิ์ทดลอง (ไม่หักเงิน)</h2>
        <label>
          User ID
          <Input name="user" value="" />
        </label>
        <label>
          Machine ID
          <Input name="machine" value="" />
        </label>
        <label>
          ระยะเวลา (วินาที 1–86400)
          <Input name="duration" type="number" value={600} />
        </label>
        <button className="rounded border p-2">สร้างสิทธิ์ทดลอง</button>
      </form>
      <form
        className="grid gap-3 rounded border p-4"
        onSubmit={(e) => {
          e.preventDefault();
          const d = new FormData(e.currentTarget);
          void act(
            `/api/admin/leases/${encodeURIComponent(String(d.get("rental")))}/extend`,
            {
              additionalSeconds: Number(d.get("duration")),
              expectedRevision: Number(d.get("revision")),
              idempotencyKey: crypto.randomUUID(),
            },
          );
        }}
      >
        <h2>ต่อเวลาทดลอง</h2>
        <label>
          Rental ID
          <Input name="rental" value="" />
        </label>
        <label>
          Revision
          <Input name="revision" type="number" value={1} />
        </label>
        <label>
          เพิ่ม (วินาที)
          <Input name="duration" type="number" value={600} />
        </label>
        <button className="rounded border p-2">ขอต่อเวลาทดลอง</button>
      </form>
    </main>
  );
}
function Input({
  name,
  value,
  type = "text",
}: {
  name: string;
  value: string | number;
  type?: string;
}) {
  return (
    <input
      className="mt-1 block w-full rounded border p-2"
      name={name}
      defaultValue={value}
      type={type}
      required
    />
  );
}
