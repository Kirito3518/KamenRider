import { getAuth } from "@/lib/auth";
import { applyMemberChange, MemberChangeError } from "@/lib/admin-member-changes";

export async function POST(request: Request, { params }: { params: Promise<{ userId: string }> }) {
  const expectedOrigin = new URL(process.env.BETTER_AUTH_URL || request.url).origin;
  if (request.headers.get("origin") !== expectedOrigin) return Response.json({ error: "คำขอมาจากเว็บไซต์ที่ไม่ได้รับอนุญาต" }, { status: 403 });
  const auth = await getAuth();
  const session = await auth.api.getSession({ headers: request.headers });
  if (!session) return Response.json({ error: "กรุณาเข้าสู่ระบบ" }, { status: 401 });
  if (session.user.role !== "admin") return Response.json({ error: "ไม่มีสิทธิ์จัดการสมาชิก" }, { status: 403 });
  let body: unknown;
  try { body = await request.json(); } catch { return Response.json({ error: "ข้อมูลคำขอไม่ถูกต้อง" }, { status: 400 }); }
  try {
    const { userId } = await params;
    const result = applyMemberChange(auth.options.database, session.user.id, userId, body);
    return Response.json(result, { headers: { "Cache-Control": "no-store" } });
  } catch (error) {
    if (error instanceof MemberChangeError) return Response.json({ error: error.message }, { status: error.status });
    console.error("Admin member update failed", error instanceof Error ? error.name : "unknown");
    return Response.json({ error: "บันทึกไม่สำเร็จ กรุณาลองใหม่" }, { status: 500 });
  }
}
