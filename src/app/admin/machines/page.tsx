import { MachinesManager } from "@/components/admin/machines-manager";
import { requireAdminSession } from "@/lib/session";

export const instant = false;

export default async function Page() {
  const { user } = await requireAdminSession();
  return <MachinesManager actorId={user.id} />;
}
