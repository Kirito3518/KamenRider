import { enabledProviders } from "@/lib/auth";
import { connection } from "next/server";

export async function GET() {
  await connection();
  return Response.json(enabledProviders, { headers: { "Cache-Control": "no-store" } });
}
