import type Database from "better-sqlite3";

export type MemberProvider = { providerId: string; accountId: string };
export type MemberAudit = { action: string; amountSatang: number; reason: string; balanceAfter: number; roleAfter: string; createdAt: number; actorName: string };
export type ManagedMember = { id: string; name: string; email: string; username: string | null; image: string | null; role: string; balanceSatang: number; createdAt: number; emailVerified: number; providers: MemberProvider[]; history: MemberAudit[] };
const day = 86_400_000;
const offset = 7 * 3_600_000;
const dateKey = (timestamp: number) => new Date(timestamp + offset).toISOString().slice(0, 10);
const midnight = (key: string) => Date.parse(`${key}T00:00:00+07:00`);
const validDate = (value: string) => /^\d{4}-\d{2}-\d{2}$/.test(value) && Number.isFinite(midnight(value)) && dateKey(midnight(value)) === value;
// Better Auth stores ISO timestamps; older fixtures may contain epoch milliseconds.
const timestampSql = (column: string) => `(CASE WHEN typeof(${column}) IN ('integer','real') THEN ${column} ELSE CAST(round(unixepoch(${column}, 'subsec') * 1000) AS INTEGER) END)`;

export function readAdminMembers(database: Database.Database, raw: Record<string, string | undefined>, now = Date.now()) {
  const today = dateKey(now);
  const period = ["day", "week", "month", "year", "custom"].includes(raw.period || "") ? raw.period! : "month";
  let from = period === "day" ? today : period === "week" ? dateKey(now - 6 * day) : period === "year" ? `${today.slice(0, 4)}-01-01` : `${today.slice(0, 7)}-01`;
  let to = today;
  let rangeError = "";
  if (period === "custom") {
    if (raw.from && raw.to && validDate(raw.from) && validDate(raw.to) && raw.from <= raw.to && midnight(raw.to) - midnight(raw.from) < 366 * day) { from = raw.from; to = raw.to; }
    else rangeError = "เลือกวันที่เริ่มต้นและสิ้นสุดให้ถูกต้อง ช่วงเวลาต้องไม่เกิน 366 วัน";
  }
  const start = midnight(from), end = midnight(to) + day;
  const stats = database.prepare(`SELECT count(*) AS totalMembers, coalesce(sum(balanceSatang),0) AS totalBalanceSatang,
    (SELECT count(*) FROM user u WHERE EXISTS (SELECT 1 FROM account a WHERE a.userId=u.id AND a.providerId IN ('google','discord'))) AS linkedMembers,
    (SELECT count(DISTINCT userId) FROM account WHERE providerId='google') AS google,
    (SELECT count(DISTINCT userId) FROM account WHERE providerId='discord') AS discord FROM user`).get() as { totalMembers: number; totalBalanceSatang: number; linkedMembers: number; google: number; discord: number };
  const created = timestampSql("createdAt");
  const counts = database.prepare(`SELECT coalesce(sum(CASE WHEN ${created} >= ? AND ${created} < ? THEN 1 ELSE 0 END),0) AS newMembers, coalesce(sum(CASE WHEN ${created} >= ? AND ${created} < ? THEN 1 ELSE 0 END),0) AS previousMembers FROM user`).get(start, end, start - (end - start), start) as { newMembers: number; previousMembers: number };
  const hourly = from === to;
  const monthly = end - start > 62 * day;
  const pattern = hourly ? "%H" : monthly ? "%Y-%m" : "%Y-%m-%d";
  const registrations = database.prepare(`SELECT strftime(?, ${created} / 1000, 'unixepoch', '+7 hours') AS bucket, count(*) AS count FROM user WHERE ${created} >= ? AND ${created} < ? GROUP BY bucket`).all(pattern, start, end) as { bucket: string; count: number }[];
  const byBucket = new Map(registrations.map(row => [row.bucket, row.count]));
  const chart: { key: string; label: string; count: number }[] = [];
  const dateFormat = new Intl.DateTimeFormat("th-TH", { timeZone: "Asia/Bangkok", day: "numeric", month: "short" });
  if (hourly) for (let hour = 0; hour < 24; hour++) { const key = String(hour).padStart(2, "0"); chart.push({ key, label: `${key}:00`, count: byBucket.get(key) || 0 }); }
  else for (let timestamp = start; timestamp < end; timestamp += day) {
    const key = dateKey(timestamp).slice(0, monthly ? 7 : 10);
    if (chart.at(-1)?.key === key) continue;
    chart.push({ key, label: monthly ? new Intl.DateTimeFormat("th-TH", { month: "short", timeZone: "Asia/Bangkok" }).format(timestamp) : dateFormat.format(timestamp), count: byBucket.get(key) || 0 });
  }
  const q = (raw.q || "").trim().slice(0, 100);
  const role = ["user", "admin"].includes(raw.role || "") ? raw.role! : "all";
  const provider = ["google", "discord", "credential"].includes(raw.provider || "") ? raw.provider! : "all";
  const linked = ["linked", "unlinked"].includes(raw.linked || "") ? raw.linked! : "all";
  const sort = raw.sort === "oldest" ? "oldest" : "newest";
  const clauses: string[] = [], values: (string | number)[] = [];
  if (q) {
    const needle = `%${q.replace(/[\\%_]/g, "\\$&")}%`;
    clauses.push("(u.name LIKE ? ESCAPE '\\' OR u.email LIKE ? ESCAPE '\\' OR u.username LIKE ? ESCAPE '\\' OR EXISTS (SELECT 1 FROM account a WHERE a.userId=u.id AND a.accountId LIKE ? ESCAPE '\\'))");
    values.push(needle, needle, needle, needle);
  }
  if (role !== "all") { clauses.push("u.role = ?"); values.push(role); }
  if (provider !== "all") { clauses.push("EXISTS (SELECT 1 FROM account a WHERE a.userId=u.id AND a.providerId=?)"); values.push(provider); }
  if (linked !== "all") clauses.push(`${linked === "unlinked" ? "NOT " : ""}EXISTS (SELECT 1 FROM account a WHERE a.userId=u.id AND a.providerId IN ('google','discord'))`);
  const where = clauses.length ? `WHERE ${clauses.join(" AND ")}` : "";
  const total = (database.prepare(`SELECT count(*) AS count FROM user u ${where}`).get(...values) as { count: number }).count;
  const pageCount = Math.max(1, Math.ceil(total / 20));
  const requested = Number(raw.page || 1);
  const page = Math.min(pageCount, Number.isSafeInteger(requested) && requested > 0 ? requested : 1);
  const order = sort === "oldest" ? "ASC" : "DESC";
  const members = database.prepare(`SELECT u.id, u.name, u.email, u.username, u.image, u.role, u.balanceSatang, ${timestampSql("u.createdAt")} AS createdAt, u.emailVerified FROM user u ${where} ORDER BY ${timestampSql("u.createdAt")} ${order}, u.id ${order} LIMIT 20 OFFSET ?`).all(...values, (page - 1) * 20) as ManagedMember[];
  const accounts = members.length ? database.prepare(`SELECT userId, providerId, accountId FROM account WHERE userId IN (${members.map(() => "?").join(",")}) ORDER BY createdAt, id`).all(...members.map(member => member.id)) as (MemberProvider & { userId: string })[] : [];
  const hasAudit = !!database.prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name='adminMemberAudit'").get();
  for (const member of members) {
    member.providers = accounts.filter(account => account.userId === member.id).map(({ providerId, accountId }) => ({ providerId, accountId }));
    member.history = hasAudit ? database.prepare("SELECT a.action, a.amountSatang, a.reason, a.balanceAfter, a.roleAfter, a.createdAt, coalesce(u.name, 'ผู้ดูแล') AS actorName FROM adminMemberAudit a LEFT JOIN user u ON u.id=a.actorId WHERE a.targetId=? ORDER BY a.createdAt DESC, a.rowid DESC LIMIT 5").all(member.id) as MemberAudit[] : [];
  }
  return { stats: { ...stats, ...counts }, chart, members, total, page, pageCount, filters: { q, role, provider, linked, sort, period, from, to }, rangeError };
}
export type AdminMembersData = ReturnType<typeof readAdminMembers>;
