"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { AlertTriangle, Box, CheckCircle2, Package, Radio, Tag } from "lucide-react";
import { AdminCard, AdminPage, AdminPageHeader, AdminStatCard, AdminStatGrid } from "@/components/admin/admin-page";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { isAgentOnline, updateStatusLabels, type AgentRelease } from "@/lib/agent-releases";
import type { Machine } from "@/lib/machine-contracts";

const fieldClassName =
  "mt-1 w-full rounded-xl border border-transparent bg-input/50 px-3 py-2 text-sm outline-none transition-[color,box-shadow,background-color] focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/30";

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

  return (
    <AdminPage>
      <AdminPageHeader
        icon={Package}
        title="Agent releases"
        description="ติดตามเวอร์ชันและเผยแพร่ package สำหรับอัปเดตเครื่องทีละเครื่อง"
      />

      {(machineError || releaseError) ? (
        <Alert variant="destructive">
          <AlertTriangle aria-hidden />
          <AlertTitle>โหลดข้อมูลไม่สำเร็จ</AlertTitle>
          <AlertDescription>{machineError} {releaseError}</AlertDescription>
        </Alert>
      ) : null}

      <AdminStatGrid>
        <AdminStatCard
          label="Agent รายงานเวอร์ชัน"
          value={machines ? `${versioned} / ${machines.length}` : machineError ? "โหลดไม่ได้" : "กำลังโหลด…"}
          hint="จาก heartbeat ล่าสุด รวมเครื่องที่ออฟไลน์"
          icon={Box}
        />
        <AdminStatCard
          label="ออนไลน์ (heartbeat 20 วินาที)"
          value={machines ? `${online} / ${machines.length}` : machineError ? "โหลดไม่ได้" : "กำลังโหลด…"}
          hint="ติดต่อได้ภายใน 20 วินาที"
          icon={Radio}
          tone="available"
        />
        <AdminStatCard
          label="stable ล่าสุดที่เผยแพร่"
          value={releases ? latestStable?.version || "ยังไม่มี stable" : releaseError ? "โหลดไม่ได้" : "กำลังโหลด…"}
          hint="ช่องทาง stable"
          icon={Tag}
        />
        <AdminStatCard
          label="ใช้ stable ล่าสุด"
          value={machines && releases ? latestStable ? `${current} / ${machines.length}` : "ยังไม่มี stable" : machineError || releaseError ? "โหลดไม่ได้" : "กำลังโหลด…"}
          hint="เครื่องที่รายงานเวอร์ชันตรงกับ stable ล่าสุด"
          icon={CheckCircle2}
          tone="busy"
        />
      </AdminStatGrid>

      <AdminCard>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <AlertTriangle aria-hidden className="size-4 text-status-busy" />
            สิ่งที่ต้องดูแล
          </CardTitle>
        </CardHeader>
        <CardContent className="text-sm">
          {!machines ? <p className="text-muted-foreground">{machineError || "กำลังโหลดสถานะเครื่อง…"}</p> : (
            <div className="flex flex-col gap-2">
              {offline ? <p>{offline} เครื่อง ยังไม่รายงานหรือติดต่อไม่ได้ภายใน 20 วินาที</p> : null}
              {failed ? <p>{failed} เครื่อง อัปเดตล้มเหลวและต้องตรวจสอบการกู้คืน</p> : null}
              {!offline && !failed ? <p className="text-muted-foreground">ไม่มีคำเตือนจากข้อมูลเครื่องล่าสุด</p> : null}
              {(offline || failed) ? <Link className="w-fit underline underline-offset-4" href="/admin/machines">ดูเครื่องและสถานะการอัปเดต</Link> : null}
            </div>
          )}
        </CardContent>
      </AdminCard>

      <div className="grid items-start gap-4 lg:grid-cols-3">
        <section aria-label="สถานะอัปเดตล่าสุดของแต่ละเครื่อง" className="min-w-0 lg:col-span-2">
          <AdminCard className="h-full">
            <CardHeader>
              <CardTitle>สถานะอัปเดตล่าสุดของแต่ละเครื่อง</CardTitle>
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
          </AdminCard>
        </section>
        <section aria-label="เวอร์ชันที่เครื่องรายงาน" className="min-w-0">
          <AdminCard className="h-full">
            <CardHeader>
              <CardTitle>เวอร์ชันที่เครื่องรายงาน</CardTitle>
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
          </AdminCard>
        </section>
      </div>

      <AdminCard>
        <CardHeader>
          <CardTitle>Release ที่เผยแพร่แล้ว</CardTitle>
          <CardDescription>SHA-256 คำนวณและตรวจสอบโดย Controller เมื่อเผยแพร่ · release เดิมไม่สามารถเขียนทับได้</CardDescription>
        </CardHeader>
        <CardContent>
          {!releases ? <p role="status" className="text-sm">{releaseError || "กำลังโหลด release…"}</p> : !releases.length ? <p className="text-sm text-muted-foreground">ยังไม่มี release ที่เผยแพร่</p> : (
            <ul className="divide-y rounded-xl border">
              {releases.map((release) => <li key={release.version} className="flex flex-col gap-1 p-3 text-sm">
                <div className="flex flex-wrap items-center gap-2">
                  <strong className="min-w-0 break-all">{release.version}</strong>
                  <Badge variant="secondary">{release.channel}</Badge>
                  <time className="text-xs text-muted-foreground" dateTime={release.publishedAtUtc}>{new Date(release.publishedAtUtc).toLocaleString()}</time>
                </div>
                <p className="whitespace-pre-wrap break-words">{release.releaseNotes}</p>
                <p className="break-all font-mono text-xs text-muted-foreground">SHA-256: {release.packageSha256.slice(0, 12)}…</p>
              </li>)}
            </ul>
          )}
        </CardContent>
      </AdminCard>

      <AdminCard>
        <CardHeader>
          <CardTitle>เผยแพร่ release ใหม่</CardTitle>
          <CardDescription>ใช้ AgentPackage.zip แบบเต็ม เวอร์ชันต้องตรงกับ manifest และ Agent binary; การเผยแพร่ไม่เริ่มอัปเดตเครื่อง</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={publish} className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
            <label className="flex flex-col text-sm">เวอร์ชัน release<input className={fieldClassName} name="version" required maxLength={64} placeholder="1.2.3" /></label>
            <label className="flex flex-col text-sm">ช่องทาง release<select className={fieldClassName} name="channel" defaultValue="stable"><option value="stable">stable</option><option value="beta">beta</option></select></label>
            <label className="flex flex-col text-sm">Release notes<textarea className={fieldClassName} name="releaseNotes" required rows={2} /></label>
            <label className="flex flex-col text-sm">AgentPackage.zip<input className={fieldClassName} name="package" type="file" accept=".zip,application/zip" required /></label>
            <div className="md:col-span-2 xl:col-span-4">
              <Button type="submit" disabled={uploading}>{uploading ? "กำลังเผยแพร่…" : "เผยแพร่ release"}</Button>
              <p role="status" aria-live="polite" className="mt-2 text-sm">{uploadStatus}</p>
            </div>
          </form>
        </CardContent>
      </AdminCard>
    </AdminPage>
  );
}
