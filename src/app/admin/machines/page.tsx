import { requireAdminSession } from "@/lib/session";
import { MachinesManager } from "@/components/admin/machines-manager";
export default async function Page() {
  await requireAdminSession();
  return <MachinesManager />;
}
