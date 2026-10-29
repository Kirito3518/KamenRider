import { headers } from "next/headers";
import { redirect } from "next/navigation";
import { getAuth } from "@/lib/auth";
import { cache } from "react";

export async function requireSession() {
  const auth = await getAuth();
  const session = await auth.api.getSession({ headers: await headers() });
  if (!session) redirect("/login");
  return session;
}

export const requireAdminSession = cache(async () => {
  const session = await requireSession();
  if (session.user.role !== "admin") redirect("/");
  return session;
});
