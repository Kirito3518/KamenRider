// @vitest-environment node
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import Database from "better-sqlite3";

const h = vi.hoisted(() => ({
  session: null as
    | null
    | { user: { id: string; role: string; name: string; email: string } },
  db: null as Database.Database | null,
}));

vi.mock("@/lib/auth", () => ({
  getAuth: async () => ({
    api: { getSession: async () => h.session },
    options: { database: h.db },
  }),
}));

import { GET as getConfig } from "@/app/api/topups/config/route";
import { GET as getTopups } from "@/app/api/topups/route";
import { POST as postPromptpay } from "@/app/api/topups/promptpay/route";
import { POST as postTrueMoney } from "@/app/api/topups/truemoney/route";
import {
  GET as getSlip,
  POST as postSlip,
} from "@/app/api/topups/[topupId]/slip/route";
import { POST as postRetry } from "@/app/api/topups/[topupId]/retry/route";
import { GET as getAdminTopups } from "@/app/api/admin/topups/route";
import { GET as getAdminTopupDetail } from "@/app/api/admin/topups/[topupId]/route";
import { POST as postAdminReview } from "@/app/api/admin/topups/[topupId]/review/route";
import { GET as getAdminSettings } from "@/app/api/admin/topup-settings/route";
import { ensureTopupSchema } from "@/lib/topup/schema";
import {
  getAdminTopupSettings,
  updateTopupProvider,
  updateTopupSettings,
} from "@/lib/topup/settings";

const EASYSLIP_KEY = "123e4567-e89b-42d3-a456-426614174000";
const ADMIN = { id: "admin-1", name: "Admin Test", email: "admin@example.com" };
const USER = {
  user: {
    id: "user-1",
    role: "user",
    name: "Topup User",
    email: "user@example.com",
  },
};
const ADMIN_SESSION = {
  user: {
    id: "admin-1",
    role: "admin",
    name: "Admin Test",
    email: "admin@example.com",
  },
};

const JPEG = new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46]);

function makeDb() {
  const db = new Database(":memory:");
  db.exec(`
    CREATE TABLE user(
      id TEXT PRIMARY KEY,
      name TEXT,
      email TEXT,
      balanceSatang INTEGER NOT NULL DEFAULT 0,
      updatedAt TEXT
    );
  `);
  db.prepare(
    "INSERT INTO user(id,name,email,balanceSatang,updatedAt) VALUES(?,?,?,?,?)",
  ).run("user-1", "Topup User", "user@example.com", 100_000, new Date(0).toISOString());
  db.prepare(
    "INSERT INTO user(id,name,email,balanceSatang,updatedAt) VALUES(?,?,?,?,?)",
  ).run("user-2", "Other User", "other@example.com", 0, new Date(0).toISOString());
  db.prepare(
    "INSERT INTO user(id,name,email,balanceSatang,updatedAt) VALUES(?,?,?,?,?)",
  ).run("admin-1", "Admin Test", "admin@example.com", 0, new Date(0).toISOString());
  ensureTopupSchema(db);
  return db;
}

function configurePromptpay(db: Database.Database) {
  const initial = getAdminTopupSettings(db);
  updateTopupProvider(db, ADMIN, "easyslip", {
    expectedUpdatedAt: initial.providers.easyslip.updatedAt,
    apiKey: EASYSLIP_KEY,
  });
  const afterKey = getAdminTopupSettings(db);
  updateTopupSettings(db, ADMIN, {
    expectedUpdatedAt: afterKey.settings.updatedAt,
    promptpay: {
      enabled: true,
      type: "msisdn",
      number: "0812345678",
      accountName: "ร้านทดสอบ",
      bankCode: "004",
      accountNumber: "1234567890",
    },
  });
}

function json(body: unknown, status = 200, headers: Record<string, string> = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...headers },
  });
}

function postRequest(path: string, body: unknown) {
  return new Request(`http://localhost${path}`, {
    method: "POST",
    headers: { origin: "http://localhost", "content-type": "application/json" },
    body: JSON.stringify(body),
  });
}

function getRequest(path: string) {
  return new Request(`http://localhost${path}`);
}

function slipRequest(topupId: string, bytes = JPEG) {
  const form = new FormData();
  form.append("file", new Blob([bytes], { type: "image/jpeg" }), "slip.jpg");
  return new Request(`http://localhost/api/topups/${topupId}/slip`, {
    method: "POST",
    headers: { origin: "http://localhost" },
    body: form,
  });
}

function context(topupId: string) {
  return { params: Promise.resolve({ topupId }) };
}

function balance(db: Database.Database, userId = "user-1") {
  return (
    db.prepare("SELECT balanceSatang FROM user WHERE id = ?").get(userId) as {
      balanceSatang: number;
    }
  ).balanceSatang;
}

function qrMock() {
  return json({
    status: 200,
    data: {
      image: "aGVsbG8=",
      mime: "image/png",
      payload: "000201010212",
    },
  });
}

function verifyMock(overrides: Record<string, unknown> = {}) {
  return json({
    status: 200,
    data: {
      transRef: `REF-${crypto.randomUUID()}`,
      date: new Date().toISOString(),
      amount: { amount: 100 },
      sender: { account: { name: { th: "นาย ผู้โอน" } } },
      receiver: {
        bank: { id: "004" },
        account: {
          name: { th: "ร้านทดสอบ" },
          proxy: { type: "MSISDN", account: "0812345678" },
        },
      },
      ...overrides,
    },
  });
}

function fetchWithQrAndVerify(verifyResponse: () => Response = () => verifyMock()) {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url.includes("/v1/qr/generate")) return qrMock();
    if (url.includes("/v1/verify")) return verifyResponse();
    throw new Error(`unexpected fetch ${url}`);
  });
}

describe("top-up customer routes", () => {
  let db: Database.Database;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    process.env.BETTER_AUTH_URL = "http://localhost";
    h.session = USER;
    db = makeDb();
    h.db = db;
    configurePromptpay(db);
    fetchMock = fetchWithQrAndVerify();
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    db.close();
    h.db = null;
    h.session = null;
    vi.unstubAllGlobals();
  });

  it("requires a session and trusted origin", async () => {
    h.session = null;
    expect((await postPromptpay(postRequest("/api/topups/promptpay", {
      requestId: crypto.randomUUID(),
      amountSatang: 10_000,
    }))).status).toBe(401);

    h.session = USER;
    const crossOrigin = new Request("http://localhost/api/topups/promptpay", {
      method: "POST",
      headers: { origin: "https://evil.example", "content-type": "application/json" },
      body: JSON.stringify({ requestId: crypto.randomUUID(), amountSatang: 10_000 }),
    });
    expect((await postPromptpay(crossOrigin)).status).toBe(403);
  });

  it("creates a PromptPay transaction and returns the QR idempotently", async () => {
    const requestId = crypto.randomUUID();
    const response = await postPromptpay(
      postRequest("/api/topups/promptpay", { requestId, amountSatang: 10_000 }),
    );
    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.outcome).toBe("pending");
    expect(body.topup.channel).toBe("promptpay");
    expect(body.topup.qrImageDataUrl).toBe("data:image/png;base64,aGVsbG8=");
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(
      db
        .prepare(
          "SELECT bank_code, receiver_account_number, receiver_account_name FROM topup_transactions WHERE id = ?",
        )
        .get(body.topup.id),
    ).toEqual({
      bank_code: "004",
      receiver_account_number: "1234567890",
      receiver_account_name: "ร้านทดสอบ",
    });

    const repeated = await postPromptpay(
      postRequest("/api/topups/promptpay", { requestId, amountSatang: 10_000 }),
    );
    const repeatedBody = await repeated.json();
    expect(repeatedBody.repeated).toBe(true);
    expect(repeatedBody.topup.id).toBe(body.topup.id);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("verifies a slip exactly once and credits the wallet", async () => {
    const created = await (
      await postPromptpay(
        postRequest("/api/topups/promptpay", {
          requestId: crypto.randomUUID(),
          amountSatang: 10_000,
        }),
      )
    ).json();
    const topupId = created.topup.id as string;
    const response = await postSlip(slipRequest(topupId), context(topupId));
    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.outcome).toBe("completed");
    expect(balance(db)).toBe(110_000);
    expect(
      db.prepare("SELECT count(*) AS count FROM wallet_transactions").get(),
    ).toMatchObject({ count: 1 });
    expect(
      db.prepare("SELECT count(*) AS count FROM slip_verification_attempts").get(),
    ).toMatchObject({ count: 1 });

    const repeated = await postSlip(slipRequest(topupId), context(topupId));
    expect((await repeated.json()).outcome).toBe("completed");
    expect(balance(db)).toBe(110_000);
  });

  it("rejects a slip whose receiver does not match the selected account", async () => {
    fetchMock = fetchWithQrAndVerify(() =>
      verifyMock({
        receiver: {
          bank: { id: "014" },
          account: {
            name: { th: "ร้านอื่น" },
            value: "1111111111",
            proxy: { type: "MSISDN", account: "0899999999" },
          },
        },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);
    const created = await (
      await postPromptpay(
        postRequest("/api/topups/promptpay", {
          requestId: crypto.randomUUID(),
          amountSatang: 10_000,
        }),
      )
    ).json();
    const topupId = created.topup.id as string;
    const response = await postSlip(slipRequest(topupId), context(topupId));
    const body = await response.json();
    expect(body.outcome).toBe("failed");
    expect(balance(db)).toBe(100_000);
    expect(
      db
        .prepare(
          "SELECT status, verification_status, failure_code FROM topup_transactions WHERE id = ?",
        )
        .get(topupId),
    ).toMatchObject({
      status: "failed",
      verification_status: "rejected",
      failure_code: "recipient_mismatch",
    });
  });

  it("rejects a duplicate slip without a second provider call", async () => {
    const first = await (
      await postPromptpay(
        postRequest("/api/topups/promptpay", {
          requestId: crypto.randomUUID(),
          amountSatang: 10_000,
        }),
      )
    ).json();
    await postSlip(slipRequest(first.topup.id), context(first.topup.id));
    const second = await (
      await postPromptpay(
        postRequest("/api/topups/promptpay", {
          requestId: crypto.randomUUID(),
          amountSatang: 10_000,
        }),
      )
    ).json();
    const callsBefore = fetchMock.mock.calls.length;
    const response = await postSlip(slipRequest(second.topup.id), context(second.topup.id));
    expect(response.status).toBe(409);
    expect((await response.json()).code).toBe("duplicate-slip");
    expect(fetchMock.mock.calls.length).toBe(callsBefore);
    expect(balance(db)).toBe(110_000);
  });

  it("does not credit when the slip amount does not match", async () => {
    fetchMock = fetchWithQrAndVerify(() =>
      verifyMock({ amount: { amount: 50 } }),
    );
    vi.stubGlobal("fetch", fetchMock);
    const created = await (
      await postPromptpay(
        postRequest("/api/topups/promptpay", {
          requestId: crypto.randomUUID(),
          amountSatang: 10_000,
        }),
      )
    ).json();
    const response = await postSlip(slipRequest(created.topup.id), context(created.topup.id));
    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.outcome).toBe("failed");
    expect(balance(db)).toBe(100_000);
  });

  it("keeps the transaction for review on timeout, then allows an explicit retry", async () => {
    const created = await (
      await postPromptpay(
        postRequest("/api/topups/promptpay", {
          requestId: crypto.randomUUID(),
          amountSatang: 10_000,
        }),
      )
    ).json();
    const topupId = created.topup.id as string;

    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        const error = new Error("timeout");
        error.name = "TimeoutError";
        throw error;
      }),
    );
    const timedOut = await postSlip(slipRequest(topupId), context(topupId));
    expect(timedOut.status).toBe(200);
    const timedOutBody = await timedOut.json();
    expect(timedOutBody.outcome).toBe("processing");
    expect(balance(db)).toBe(100_000);

    const successFetch = fetchWithQrAndVerify();
    vi.stubGlobal("fetch", successFetch);
    const retried = await postRetry(
      new Request(`http://localhost/api/topups/${topupId}/retry`, {
        method: "POST",
        headers: { origin: "http://localhost" },
      }),
      context(topupId),
    );
    expect(retried.status).toBe(200);
    const retriedBody = await retried.json();
    expect(retriedBody.outcome).toBe("completed");
    expect(balance(db)).toBe(110_000);
    expect(
      db.prepare("SELECT count(*) AS count FROM wallet_transactions").get(),
    ).toMatchObject({ count: 1 });
  });

  it("only lists the session user's history and protects other users' details", async () => {
    await postPromptpay(
      postRequest("/api/topups/promptpay", {
        requestId: crypto.randomUUID(),
        amountSatang: 10_000,
      }),
    );
    const list = await getTopups(getRequest("/api/topups"));
    expect(list.status).toBe(200);
    const body = await list.json();
    expect(body.entries).toHaveLength(1);
    expect(body.entries[0].userId).toBeUndefined();

    h.session = {
      user: {
        id: "user-2",
        role: "user",
        name: "Other User",
        email: "other@example.com",
      },
    };
    const otherList = await (await getTopups(getRequest("/api/topups"))).json();
    expect(otherList.entries).toHaveLength(0);
    const topupId = body.entries[0].id as string;
    const slipResponse = await getSlip(
      getRequest(`/api/topups/${topupId}/slip`),
      context(topupId),
    );
    expect(slipResponse.status).toBe(404);
  });

  it("returns the public config without secrets or receiver phone", async () => {
    const response = await getConfig(getRequest("/api/topups/config"));
    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.promptpay.enabled).toBe(true);
    expect(body.slipVerificationAvailable).toBe(true);
    const text = JSON.stringify(body);
    expect(text).not.toContain(EASYSLIP_KEY);
    expect(text).not.toContain("0812345678");
  });
});

function configureTruemoney(db: Database.Database) {
  const settings = getAdminTopupSettings(db);
  updateTopupSettings(db, ADMIN, {
    expectedUpdatedAt: settings.settings.updatedAt,
    truemoney: { enabled: true, phone: "0812345678" },
  });
}

function trueMoneyClaimResponse(amount = "10.00") {
  return json(
    {
      success: true,
      status: 200,
      message: "รับเงินสำเร็จ",
      code: "SUCCESS",
      data: { name: "นาย ทดสอบ", amount },
    },
    200,
    { "x-request-id": "tm-request-1" },
  );
}

describe("TrueMoney route", () => {
  let db: Database.Database;

  beforeEach(() => {
    process.env.BETTER_AUTH_URL = "http://localhost";
    h.session = USER;
    db = makeDb();
    h.db = db;
    configureTruemoney(db);
  });

  afterEach(() => {
    db.close();
    h.db = null;
    h.session = null;
    vi.unstubAllGlobals();
  });

  it("claims a voucher exactly once and rejects the same voucher afterwards", async () => {
    const fetchMock = vi.fn(async () => trueMoneyClaimResponse());
    vi.stubGlobal("fetch", fetchMock);
    const link = "https://gift.truemoney.com/campaign/?v=abc123";
    const first = await postTrueMoney(
      postRequest("/api/topups/truemoney", {
        requestId: crypto.randomUUID(),
        link,
      }),
    );
    expect(first.status).toBe(200);
    const firstBody = await first.json();
    expect(firstBody.outcome).toBe("completed");
    expect(firstBody.topup.creditedSatang).toBe(1_000);
    expect(balance(db)).toBe(101_000);

    const duplicate = await postTrueMoney(
      postRequest("/api/topups/truemoney", {
        requestId: crypto.randomUUID(),
        link: "abc123",
      }),
    );
    expect(duplicate.status).toBe(409);
    expect((await duplicate.json()).code).toBe("duplicate-voucher");
    expect(balance(db)).toBe(101_000);
  });

  it("keeps an ambiguous timeout for review without crediting", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        const error = new Error("timeout");
        error.name = "TimeoutError";
        throw error;
      }),
    );
    const response = await postTrueMoney(
      postRequest("/api/topups/truemoney", {
        requestId: crypto.randomUUID(),
        link: "timeout-voucher",
      }),
    );
    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.outcome).toBe("processing");
    expect(balance(db)).toBe(100_000);
    const retry = await postRetry(
      new Request(`http://localhost/api/topups/${body.topup.id}/retry`, {
        method: "POST",
        headers: { origin: "http://localhost" },
      }),
      context(body.topup.id),
    );
    expect(retry.status).toBe(409);
  });

  it("allows a retry after a rate limit response", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        json(
          {
            success: false,
            status: 429,
            message: "ยิงคำขอเร็วเกินไป",
            code: "RATE_LIMITED",
            data: null,
          },
          429,
          { "retry-after": "30" },
        ),
      ),
    );
    const first = await postTrueMoney(
      postRequest("/api/topups/truemoney", {
        requestId: crypto.randomUUID(),
        link: "retry-voucher",
      }),
    );
    const body = await first.json();
    expect(body.outcome).toBe("pending");
    expect(body.topup.canRetry).toBe(true);
    expect(balance(db)).toBe(100_000);

    vi.stubGlobal("fetch", vi.fn(async () => trueMoneyClaimResponse()));
    const retried = await postRetry(
      new Request(`http://localhost/api/topups/${body.topup.id}/retry`, {
        method: "POST",
        headers: { origin: "http://localhost" },
      }),
      context(body.topup.id),
    );
    expect(retried.status).toBe(200);
    expect((await retried.json()).outcome).toBe("completed");
    expect(balance(db)).toBe(101_000);
  });
});

describe("top-up admin routes", () => {
  let db: Database.Database;

  beforeEach(() => {
    process.env.BETTER_AUTH_URL = "http://localhost";
    h.session = ADMIN_SESSION;
    db = makeDb();
    h.db = db;
    configurePromptpay(db);
  });

  afterEach(() => {
    db.close();
    h.db = null;
    h.session = null;
  });

  it("enforces the admin role and never exposes raw API keys", async () => {
    h.session = USER;
    expect((await getAdminSettings(getRequest("/api/admin/topup-settings"))).status).toBe(403);
    expect((await getAdminTopups(getRequest("/api/admin/topups"))).status).toBe(403);

    h.session = ADMIN_SESSION;
    const settings = await getAdminSettings(getRequest("/api/admin/topup-settings"));
    expect(settings.status).toBe(200);
    const body = await settings.json();
    expect(body.providers.easyslip.configured).toBe(true);
    expect(JSON.stringify(body)).not.toContain(EASYSLIP_KEY);
  });

  it("returns stats/chart/table data and a detail with attempts", async () => {
    const fetchMock = fetchWithQrAndVerify();
    vi.stubGlobal("fetch", fetchMock);
    h.session = USER;
    const created = await (
      await postPromptpay(
        postRequest("/api/topups/promptpay", {
          requestId: crypto.randomUUID(),
          amountSatang: 10_000,
        }),
      )
    ).json();
    const topupId = created.topup.id as string;
    await postSlip(slipRequest(topupId), context(topupId));
    h.session = ADMIN_SESSION;

    const listResponse = await getAdminTopups(
      getRequest("/api/admin/topups?period=30d&channel=all"),
    );
    expect(listResponse.status).toBe(200);
    const dashboard = await listResponse.json();
    expect(dashboard.stats.creditedSatang).toBe(10_000);
    expect(dashboard.stats.completed).toBe(1);
    expect(Array.isArray(dashboard.chart)).toBe(true);
    expect(dashboard.entries[0].userEmail).toBe("user@example.com");

    const detailResponse = await getAdminTopupDetail(
      getRequest(`/api/admin/topups/${topupId}`),
      context(topupId),
    );
    expect(detailResponse.status).toBe(200);
    const detail = await detailResponse.json();
    expect(detail.topup.attempts).toHaveLength(1);
    expect(detail.topup.attempts[0].provider).toBe("easyslip");
    expect(JSON.stringify(detail)).not.toContain(EASYSLIP_KEY);
  });

  it("lets an Admin approve a row that waited for manual review", async () => {
    const fetchMock = fetchWithQrAndVerify(() =>
      verifyMock({
        receiver: {
          bank: { id: "004" },
          account: {
            name: { th: "ร้านทดสอบ" },
          },
        },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);
    h.session = USER;
    const created = await (
      await postPromptpay(
        postRequest("/api/topups/promptpay", {
          requestId: crypto.randomUUID(),
          amountSatang: 10_000,
        }),
      )
    ).json();
    const topupId = created.topup.id as string;
    const slip = await postSlip(slipRequest(topupId), context(topupId));
    const slipBody = await slip.json();
    expect(slipBody.outcome).toBe("processing");

    h.session = ADMIN_SESSION;
    const review = await postAdminReview(
      postRequest(`/api/admin/topups/${topupId}/review`, { action: "approve" }),
      context(topupId),
    );
    expect(review.status).toBe(200);
    const body = await review.json();
    expect(body.outcome).toBe("completed");
    expect(balance(db)).toBe(110_000);
    expect(
      db
        .prepare("SELECT verification_code FROM topup_transactions WHERE id = ?")
        .get(topupId),
    ).toMatchObject({ verification_code: "manual_approved" });
  });
});
