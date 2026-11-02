import { requireAdminSession } from "@/lib/session";
import { MachinesManager } from "@/components/admin/machines-manager";
import { Suspense } from "react";

async function ProtectedMachines() {
  await requireAdminSession();
  return <MachinesManager />;
}

export default function Page() {
  return <Suspense fallback={<p role="status">กำลังตรวจสอบสิทธิ์…</p>}><ProtectedMachines /></Suspense>;
}
