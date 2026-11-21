import { getAdminMembers } from "@/lib/admin";
import { Suspense } from "react";
import { MembersManager } from "@/components/admin/members-manager";
import { AdminUsersPageSkeleton } from "@/components/loading";

async function AdminUsers({ searchParams }: { searchParams: PageProps<"/admin/users">["searchParams"] }) {
  const raw = await searchParams;
  const filters = Object.fromEntries(Object.entries(raw).map(([key, value]) => [key, Array.isArray(value) ? value[0] : value]));
  return <MembersManager data={await getAdminMembers(filters)} />;
}

export default function AdminUsersPage({ searchParams }: PageProps<"/admin/users">) {
  return (
    <Suspense fallback={<AdminUsersPageSkeleton />}>
      <AdminUsers searchParams={searchParams} />
    </Suspense>
  );
}
