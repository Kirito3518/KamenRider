"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { AlertTriangle, Box, CheckCircle2, Radio, Tag } from "lucide-react";
import { isAgentOnline, updateStatusLabels, type AgentRelease } from "@/lib/agent-releases";
import type { Machine } from "@/lib/machine-contracts";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

const control = "mt-1 block w-full rounded-md border bg-background px-3 py-2 text-sm";

export function AgentReleasesManager() {
  const [machines, setMachines] = useState<Machine[] | null>(null);
  const [releases, setReleases] = useState<AgentRelease[] | null>(null);
  const [machineError, setMachineError] = useState("");
  const [releaseError, setReleaseError] = useState("");
  const [uploadStatus, setUploadStatus] = useState("");
  const [uploading, setUploading] = useState(false);
  const [now, setNow] = useState(0);
  const latestLoad = useRef(0);

  const load = useCallback(async (signal?: AbortSignal) => {
    const run = ++latestLoad.current;
    const get = async <T,>(url: string): Promise<T> => {
      const response = await fetch(url, { cache: "no-store", signal });
      if (!response.ok) throw new Error();
      return response.json() as Promise<T>;
    };
    const [machineResult, releaseResult] = await Promise.allSettled([
      get<Machine[]>("/api/admin/machines"), get<AgentRelease[]>("/api/admin/agent-releases"),
    ]);
    if (signal?.aborted || run !== latestLoad.current) return;
    setNow(Date.now());
    if (machineResult.status === "fulfilled") { setMachines(machineResult.value); setMachineError(""); }
    else setMachineError("โหลดข้อมูลเครื่องไม่ได้ ข้อมูลที่แสดงอาจไม่ล่าสุด");
    if (releaseResult.status === "fulfilled") { setReleases(releaseResult.value); setReleaseError(""); }
    else setReleaseError("โหลดรายการ release ไม่ได้ ข้อมูลที่แสดงอาจไม่ล่าสุด");
  }, []);

  useEffect(() => {
    const loads = latestLoad;
    const controller = new AbortController();
    let busy = false;
    const refresh = async () => {
      if (busy) return;
      busy = true;
      try { await load(controller.signal); } finally { busy = false; }
    };
    void refresh();
    const timer = window.setInterval(() => { if (document.visibilityState === "visible") void refresh(); }, 10_000);
    return () => { loads.current++; controller.abort(); window.clearInterval(timer); };
  }, [load]);

  async function publish(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const fields = new FormData(form);
    const version = String(fields.get("version") || "").trim();
    const channel = String(fields.get("channel") || "");
    const notes = String(fields.get("releaseNotes") || "").trim();
    const file = (form.elements.namedItem("package") as HTMLInputElement | null)?.files?.[0];
    if (!version || version.length > 64 || !notes || !["stable", "beta"].includes(channel)) {
      setUploadStatus("กรอกเวอร์ชัน ช่องทาง และ release notes ให้ครบ"); return;
    }
    if (!(file instanceof File) || file.name.toLowerCase() !== "agentpackage.zip" || !file.size || file.size > 100 * 1024 * 1024) {
      setUploadStatus("เลือกไฟล์ AgentPackage.zip ที่มีข้อมูลและขนาดไม่เกิน 100 MiB"); return;
    }
    const body = new FormData();
    body.append("version", version);
    body.append("channel", channel);
    body.append("releaseNotes", notes);
    body.append("package", file.slice(0, file.size, "application/zip"), "AgentPackage.zip");
    setUploading(true); setUploadStatus("กำลังตรวจสอบและเผยแพร่ package…");
    try {
      const response = await fetch("/api/admin/agent-releases", { method: "POST", body, cache: "no-store" });
      if (!response.ok) throw new Error();
      form.reset();
      setUploadStatus("เผยแพร่ release แล้ว Controller ตรวจสอบ manifest และ SHA-256 ของ package แล้ว");
      await load();
    } catch {
      setUploadStatus("เผยแพร่ไม่สำเร็จ ตรวจสอบเวอร์ชันให้ตรงกับ manifest, package และ release ที่มีอยู่ แล้วลองอีกครั้ง");
    } finally { setUploading(false); }
  }

  const latestStable = releases?.find((release) => release.channel === "stable");
  const online = machines?.filter((machine) => isAgentOnline(machine, now)).length ?? 0;
  const versioned = machines?.filter((machine) => Boolean(machine.agentVersion)).length ?? 0;
  const current = machines?.filter((machine) => machine.agentVersion === latestStable?.version).length ?? 0;
  const failed = machines?.filter((machine) => machine.agentUpdateStatus === "failed").length ?? 0;
  const offline = machines ? machines.length - online : 0;
  const versions = new Map<string, number>();
  for (const machine of machines ?? []) {
    const version = machine.agentVersion || "ยังไม่รายงานเวอร์ชัน";
    versions.set(version, (versions.get(version) ?? 0) + 1);
  }

  return <div className="mx-auto w-full max-w-7xl space-y-6">
    <div><h1 className="text-3xl font-bold tracking-tight">Agent releases</h1><p className="mt-1 text-sm text-muted-foreground">ติดตามเวอร์ชันและเผยแพร่ package สำหรับอัปเดตเครื่องทีละเครื่อง</p></div>
    {(machineError || releaseError) ? <div role="alert" className="rounded-xl border border-destructive/40 p-4 text-sm">{machineError} {releaseError}</div> : null}
    <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
      <Summary title="Agent รายงานเวอร์ชัน" value={machines ? `${versioned} / ${machines.length}` : machineError ? "โหลดไม่ได้" : "กำลังโหลด…"} icon={<Box className="size-4" />} />
      <Summary title="ออนไลน์ (heartbeat 20 วินาที)" value={machines ? `${online} / ${machines.length}` : machineError ? "โหลดไม่ได้" : "กำลังโหลด…"} icon={<Radio className="size-4" />} />
      <Summary title="stable ล่าสุดที่เผยแพร่" value={releases ? latestStable?.version || "ยังไม่มี stable" : releaseError ? "โหลดไม่ได้" : "กำลังโหลด…"} icon={<Tag className="size-4" />} />
      <Summary title="ใช้ stable ล่าสุด" value={machines && releases ? latestStable ? `${current} / ${machines.length}` : "ยังไม่มี stable" : machineError || releaseError ? "โหลดไม่ได้" : "กำลังโหลด…"} icon={<CheckCircle2 className="size-4" />} />
    </div>
    <section className="rounded-xl border bg-card p-4">
      <h2 className="flex items-center gap-2 font-semibold"><AlertTriangle className="size-4 text-amber-500" /> สิ่งที่ต้องดูแล</h2>
      {!machines ? <p className="mt-3 text-sm text-muted-foreground">{machineError || "กำลังโหลดสถานะเครื่อง…"}</p> : <div className="mt-3 space-y-2 text-sm">
        {offline ? <p>{offline} เครื่อง ยังไม่รายงานหรือติดต่อไม่ได้ภายใน 20 วินาที</p> : null}
        {failed ? <p>{failed} เครื่อง อัปเดตล้มเหลวและต้องตรวจสอบการกู้คืน</p> : null}
        {!offline && !failed ? <p className="text-muted-foreground">ไม่มีคำเตือนจากข้อมูลเครื่องล่าสุด</p> : null}
        {(offline || failed) ? <Link className="inline-block underline underline-offset-4" href="/admin/machines">ดูเครื่องและสถานะการอัปเดต</Link> : null}
      </div>}
    </section>
    <div className="grid items-start gap-4 lg:grid-cols-3">
      <section aria-label="สถานะอัปเดตล่าสุดของแต่ละเครื่อง" className="min-w-0 lg:col-span-2">
        <Card size="sm">
          <CardHeader>
            <CardTitle><h2>สถานะอัปเดตล่าสุดของแต่ละเครื่อง</h2></CardTitle>
            <CardDescription>นับเฉพาะเครื่องที่เคยมีการอัปเดต · ผลอัปเดตแยกจากการอนุมัติและความพร้อมให้เช่า</CardDescription>
          </CardHeader>
          <CardContent>
            {!machines ? <p>{machineError || "กำลังโหลดสถานะเครื่อง…"}</p> : <dl className="grid grid-cols-2 gap-4 sm:grid-cols-4">
              {Object.entries(updateStatusLabels).map(([status, label]) => {
                const count = machines.filter((machine) => machine.agentUpdateStatus === status).length;
                return <div key={status} aria-label={`${label}: ${count}`} className="flex flex-col gap-1">
                  <dt className="text-xs text-muted-foreground">{label}</dt><dd className="text-xl font-semibold tabular-nums">{count}</dd>
                </div>;
              })}
            </dl>}
          </CardContent>
        </Card>
      </section>
      <section aria-label="เวอร์ชันที่เครื่องรายงาน" className="min-w-0">
        <Card size="sm">
          <CardHeader>
            <CardTitle><h2>เวอร์ชันที่เครื่องรายงาน</h2></CardTitle>
            <CardDescription>จาก heartbeat ล่าสุด รวมเครื่องที่ออฟไลน์</CardDescription>
          </CardHeader>
          <CardContent>
            {!machines ? <p>{machineError || "กำลังโหลดสถานะเครื่อง…"}</p> : !machines.length ? <p>ยังไม่มีเครื่องในระบบ</p> : <ul className="flex flex-col gap-2">
              {Array.from(versions, ([version, count]) => <li key={version} aria-label={`${version}: ${count} เครื่อง`} className="flex items-start justify-between gap-3 text-sm">
                <span className="min-w-0 break-all">{version}</span><span className="shrink-0 tabular-nums">{count} เครื่อง</span>
              </li>)}
            </ul>}
            <Link className="mt-4 inline-block text-sm underline underline-offset-4" href="/admin/machines">เลือกเครื่องเพื่อ Update Agent</Link>
          </CardContent>
        </Card>
      </section>
    </div>
    <section className="space-y-3 rounded-xl border bg-card p-5">
      <div><h2 className="text-lg font-semibold">Release ที่เผยแพร่แล้ว</h2><p className="text-sm text-muted-foreground">SHA-256 คำนวณและตรวจสอบโดย Controller เมื่อเผยแพร่ · release เดิมไม่สามารถเขียนทับได้</p></div>
      {!releases ? <p role="status" className="text-sm">{releaseError || "กำลังโหลด release…"}</p> : !releases.length ? <p className="text-sm text-muted-foreground">ยังไม่มี release ที่เผยแพร่</p> : <ul className="divide-y rounded-lg border">
        {releases.map((release) => <li key={release.version} className="space-y-1 p-3 text-sm">
          <div className="flex flex-wrap items-center gap-2"><strong>{release.version}</strong><span className="rounded border px-1.5 py-0.5 text-xs">{release.channel}</span><time className="text-xs text-muted-foreground" dateTime={release.publishedAtUtc}>{new Date(release.publishedAtUtc).toLocaleString()}</time></div>
          <p className="whitespace-pre-wrap break-words">{release.releaseNotes}</p><p className="break-all font-mono text-xs text-muted-foreground">SHA-256: {release.packageSha256.slice(0, 12)}…</p>
        </li>)}
      </ul>}
    </section>
    <section className="space-y-3 rounded-xl border bg-card p-5">
      <div><h2 className="text-lg font-semibold">เผยแพร่ release ใหม่</h2><p className="text-sm text-muted-foreground">ใช้ AgentPackage.zip แบบเต็ม เวอร์ชันต้องตรงกับ manifest และ Agent binary; การเผยแพร่ไม่เริ่มอัปเดตเครื่อง</p></div>
      <form onSubmit={publish} className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
        <label className="text-sm">เวอร์ชัน release<input className={control} name="version" required maxLength={64} placeholder="1.2.3" /></label>
        <label className="text-sm">ช่องทาง release<select className={control} name="channel" defaultValue="stable"><option value="stable">stable</option><option value="beta">beta</option></select></label>
        <label className="text-sm">Release notes<textarea className={control} name="releaseNotes" required rows={2} /></label>
        <label className="text-sm">AgentPackage.zip<input className={control} name="package" type="file" accept=".zip,application/zip" required /></label>
        <div className="md:col-span-2 xl:col-span-4"><button className="rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground disabled:opacity-50" disabled={uploading}>{uploading ? "กำลังเผยแพร่…" : "เผยแพร่ release"}</button><p role="status" aria-live="polite" className="mt-2 text-sm">{uploadStatus}</p></div>
      </form>
    </section>
  </div>;
}

function Summary({ title, value, icon }: { title: string; value: string; icon: React.ReactNode }) {
  return <section className="rounded-xl border bg-card p-4"><div className="flex items-center justify-between gap-2 text-sm text-muted-foreground"><span>{title}</span>{icon}</div><p className="mt-3 text-xl font-semibold">{value}</p></section>;
}
