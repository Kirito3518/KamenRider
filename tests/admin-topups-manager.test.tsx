import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TopupsManager } from "@/components/admin/topups/topups-manager";
import type {
  AdminTopupDashboardResponse,
  AdminTopupDetail,
  AdminTopupEntry,
  AdminTopupStats,
  TopupChartPoint,
} from "@/lib/topup/types";

const DASHBOARD_URL = "/api/admin/topups";
const DETAIL_URL = "/api/admin/topups/t1";
const VERIFY_URL = "/api/admin/topups/t1/verify";

const CREATED_ALICE = Date.parse("2026-11-18T03:00:00.000Z");
const CREATED_BOB = Date.parse("2026-11-17T03:00:00.000Z");

const ALICE_ENTRY: AdminTopupEntry = {
  id: "t1",
  reference: "TP-2026-11-18-0001",
  channel: "promptpay",
  status: "completed",
  requestedSatang: 12_000,
  creditedSatang: 12_000,
  createdAt: CREATED_ALICE,
  completedAt: CREATED_ALICE + 60_000,
  expiresAt: null,
  verificationStatus: "verified",
  failureCode: null,
  failureMessage: null,
  bankName: null,
  bankCode: null,
  receiverAccountName: "Kamen Rider Shop",
  userId: "user-1",
  userName: "Alice",
  userEmail: "alice@example.com",
  slipProvider: "easyslip",
  slipTransRef: "SLIP001",
  slipAmountSatang: 12_000,
};

const BOB_ENTRY: AdminTopupEntry = {
  id: "t2",
  reference: "TP-2026-11-17-0002",
  channel: "bank_transfer",
  status: "processing",
  requestedSatang: 8_000,
  creditedSatang: 0,
  createdAt: CREATED_BOB,
  completedAt: null,
  expiresAt: CREATED_BOB + 24 * 60 * 60 * 1_000,
  verificationStatus: "unknown",
  failureCode: "process_timeout",
  failureMessage: null,
  bankName: "Kasikornbank",
  bankCode: "KBANK",
  receiverAccountName: "Kamen Rider Shop",
  userId: "user-2",
  userName: "Bob",
  userEmail: "bob@example.com",
  slipProvider: "slipok",
  slipTransRef: null,
  slipAmountSatang: null,
};

const STATS: AdminTopupStats = {
  requestedSatang: 250_000,
  total: 22,
  completed: 18,
  pending: 3,
  failed: 1,
  creditedSatang: 200_000,
};

const PREVIOUS: AdminTopupStats = {
  requestedSatang: 150_000,
  total: 11,
  completed: 9,
  pending: 0,
  failed: 2,
  creditedSatang: 220_000,
};

const CHART: TopupChartPoint[] = [
  {
    key: "2026-11-18",
    label: "18 พ.ย.",
    truemoneySatang: 5_000,
    promptpaySatang: 12_000,
    bankTransferSatang: 0,
    couponSatang: 0,
    truemoneyCount: 1,
    promptpayCount: 1,
    bankTransferCount: 0,
    couponCount: 0,
  },
  {
    key: "2026-11-19",
    label: "19 พ.ย.",
    truemoneySatang: 0,
    promptpaySatang: 0,
    bankTransferSatang: 8_000,
    couponSatang: 2_000,
    truemoneyCount: 0,
    promptpayCount: 0,
    bankTransferCount: 1,
    couponCount: 1,
  },
];

const DETAIL: AdminTopupDetail = {
  ...ALICE_ENTRY,
  updatedAt: CREATED_ALICE + 120_000,
  qrImageDataUrl: "data:image/png;base64,iVBORw0KGgo=",
  qrPayload: "000201010212",
  qrExpiresAt: null,
  receiverAccountNumber: "1234567890",
  promptpayType: "msisdn",
  promptpayNumberMasked: "081-•••-2345",
  truemoneyPhoneMasked: null,
  slipProvider: "easyslip",
  slipTransRef: "SLIP001",
  slipAmountSatang: 12_000,
  slipSenderName: "Alice",
  slipReceiverName: "Kamen Rider Shop",
  slipReceiverAccountMasked: "123••••7890",
  slipImageUrl: "/api/topups/t1/slip",
  verifiedAt: CREATED_ALICE + 60_000,
  verificationCode: "200",
  verificationMessage: "ตรวจสอบสลิปสำเร็จ",
  attemptsCount: 1,
  canResume: false,
  canRetry: true,
  events: [
    {
      id: "e1",
      fromStatus: null,
      toStatus: "pending",
      reason: "สร้างรายการเติมเงิน",
      actor: "user",
      createdAt: CREATED_ALICE,
    },
    {
      id: "e2",
      fromStatus: "pending",
      toStatus: "processing",
      reason: "อัปโหลดสลิปเพื่อตรวจสอบ",
      actor: "user",
      createdAt: CREATED_ALICE + 30_000,
    },
    {
      id: "e3",
      fromStatus: "processing",
      toStatus: "completed",
      reason: "ตรวจสอบสลิปผ่าน",
      actor: "system",
      createdAt: CREATED_ALICE + 60_000,
    },
  ],
  userId: "user-1",
  userName: "Alice",
  userEmail: "alice@example.com",
  providerRequestId: "req-1",
  attempts: [
    {
      id: "a1",
      attemptNo: 1,
      provider: "easyslip",
      outcome: "verified",
      httpStatus: 200,
      errorCode: null,
      errorMessage: null,
      transRef: "SLIP001",
      amountSatang: 12_000,
      durationMs: 420,
      retryAfterSeconds: null,
      requestSummary: '{"transRef":"SLIP001","amount":120}',
      responseExcerpt:
        '{"status":"success","providerTransRef":"EXCERPT-UNIQUE-123"}',
      createdAt: CREATED_ALICE + 45_000,
    },
  ],
};

const UPDATED_DETAIL: AdminTopupDetail = {
  ...DETAIL,
  verificationStatus: "unknown",
  verificationCode: "provider_timeout",
  verificationMessage: "ผู้ให้บริการไม่ตอบกลับภายในเวลาที่กำหนด",
  attemptsCount: 2,
  attempts: [
    ...DETAIL.attempts,
    {
      id: "a2",
      attemptNo: 2,
      provider: "slipok",
      outcome: "timeout",
      httpStatus: null,
      errorCode: "ETIMEDOUT",
      errorMessage: "หมดเวลาเชื่อมต่อผู้ให้บริการ",
      transRef: null,
      amountSatang: null,
      durationMs: 20_000,
      retryAfterSeconds: 30,
      requestSummary: '{"attempt":2}',
      responseExcerpt: '{"error":"timeout","marker":"EXCERPT-AFTER-RETRY"}',
      createdAt: CREATED_ALICE + 90_000,
    },
  ],
};

type MockState = {
  entries: AdminTopupEntry[];
  total: number;
  chart: TopupChartPoint[];
  rangeError: string;
  dashboardError: boolean;
  detail: AdminTopupDetail;
  detailError: boolean;
  verifyMessage: string;
  verifyOutcome: "completed" | "processing" | "failed";
  verifyCalls: number;
  detailCalls: number;
};

let state: MockState;
let fetchSpy: ReturnType<typeof vi.fn>;

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function dashboardFor(url: URL): AdminTopupDashboardResponse {
  const q = (url.searchParams.get("q") ?? "").trim().toLowerCase();
  const status = url.searchParams.get("status") ?? "all";
  const channel = url.searchParams.get("channel") ?? "all";
  const period = url.searchParams.get("period") ?? "30d";
  const sort = url.searchParams.get("sort") ?? "newest";
  const page = Number(url.searchParams.get("page") ?? "1") || 1;
  const from = url.searchParams.get("from") ?? "";
  const to = url.searchParams.get("to") ?? "";

  let entries = state.entries;
  if (q) {
    entries = entries.filter((entry) =>
      [entry.userName, entry.userEmail, entry.reference]
        .join(" ")
        .toLowerCase()
        .includes(q),
    );
  }
  if (status !== "all") entries = entries.filter((entry) => entry.status === status);
  if (channel !== "all") entries = entries.filter((entry) => entry.channel === channel);
  if (period === "today") entries = entries.slice(0, 1);

  const filtered =
    Boolean(q) ||
    status !== "all" ||
    channel !== "all" ||
    period === "today";

  let entriesForPage = entries;
  let pagination = {
    page: 1,
    pageSize: 20,
    total: state.total,
    pageCount: Math.max(1, Math.ceil(state.total / 20)),
  };

  if (filtered) {
    pagination = {
      page: 1,
      pageSize: 20,
      total: entries.length,
      pageCount: Math.max(1, Math.ceil(entries.length / 20)),
    };
  } else if (page === 2 && state.total === 22) {
    entriesForPage = entries.slice(1, 2);
    pagination = { page: 2, pageSize: 20, total: 22, pageCount: 2 };
  }

  return {
    stats: STATS,
    previous: PREVIOUS,
    chart: state.chart,
    chartPrevious: { requestedSatang: 100_000, creditedSatang: 90_000, total: 9 },
    entries: entriesForPage,
    pagination,
    filters: {
      q,
      channel,
      status,
      period,
      from: period === "custom" ? from : "2026-11-01",
      to: period === "custom" ? to : "2026-11-20",
      sort,
    } as AdminTopupDashboardResponse["filters"],
    rangeError: state.rangeError,
  };
}

describe("Admin TopupsManager", () => {
  beforeEach(() => {
    state = {
      entries: [ALICE_ENTRY, BOB_ENTRY],
      total: 22,
      chart: CHART,
      rangeError: "",
      dashboardError: false,
      detail: DETAIL,
      detailError: false,
      verifyMessage: "ตรวจสอบสลิปผ่านและเพิ่มยอดเงินแล้ว",
      verifyOutcome: "completed",
      verifyCalls: 0,
      detailCalls: 0,
    };
    fetchSpy = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), "http://localhost");
      if (url.pathname === DASHBOARD_URL) {
        if (init?.method && init.method !== "GET") {
          throw new Error(`Unexpected method ${init.method} for ${url.pathname}`);
        }
        if (state.dashboardError) {
          return json({ error: "เชื่อมต่อเซิร์ฟเวอร์ไม่สำเร็จ" }, 503);
        }
        return json(dashboardFor(url));
      }
      if (url.pathname === DETAIL_URL) {
        state.detailCalls += 1;
        if (state.detailError) return json({ error: "โหลดรายละเอียดไม่สำเร็จ" }, 500);
        return json({ topup: state.detail });
      }
      if (url.pathname === VERIFY_URL) {
        if (init?.method !== "POST") {
          throw new Error(`Expected POST for ${url.pathname}`);
        }
        state.verifyCalls += 1;
        state.detail = UPDATED_DETAIL;
        return json({
          topup: state.detail,
          balanceSatang: 20_000,
          outcome: state.verifyOutcome,
          message: state.verifyMessage,
        });
      }
      throw new Error(`Unexpected fetch URL: ${String(input)}`);
    });
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  async function renderLoaded() {
    render(<TopupsManager />);
    expect(await screen.findByText("Alice")).toBeDefined();
    return screen;
  }

  it("shows the KPI cards, chart sections and transaction table from the dashboard API", async () => {
    await renderLoaded();

    expect(screen.getByText("ยอดที่ขอเติมรวม")).toBeDefined();
    expect(screen.getByText("รายการทั้งหมด")).toBeDefined();
    expect(screen.getAllByText("สำเร็จ").length).toBeGreaterThan(0);
    expect(screen.getByText("รอดำเนินการ")).toBeDefined();
    expect(screen.getByText("ไม่สำเร็จ")).toBeDefined();
    expect(screen.getByText("เงินที่เติมเข้าระบบจริง")).toBeDefined();
    expect(screen.getAllByText("฿2,500.00").length).toBeGreaterThan(0);
    expect(screen.getAllByText("฿2,000.00").length).toBeGreaterThan(0);

    expect(screen.getByText("ยอดเงินที่เติมสำเร็จตามช่องทาง")).toBeDefined();
    expect(screen.getByText("จำนวนรายการตามช่องทาง")).toBeDefined();

    expect(screen.getByText("bob@example.com")).toBeDefined();
    expect(screen.getByText("TP-2026-11-18-0001")).toBeDefined();
    expect(screen.getByText("TP-2026-11-17-0002")).toBeDefined();
    expect(screen.getByText("PromptPay QR")).toBeDefined();
    expect(screen.getByText("โอนผ่านธนาคาร")).toBeDefined();
    expect(screen.getByText("กำลังตรวจสอบ")).toBeDefined();
    expect(screen.getByText("รอผลตรวจสอบ")).toBeDefined();
    expect(screen.getAllByText("ตรวจสอบผ่าน").length).toBeGreaterThan(0);
    expect(screen.getAllByText("฿120.00").length).toBeGreaterThan(0);
    expect(screen.getAllByText("฿80.00").length).toBeGreaterThan(0);

    expect(screen.getByText("ยังไม่มีข้อมูลเปรียบเทียบ")).toBeDefined();
    expect(screen.getAllByText("+100.0%").length).toBeGreaterThan(0);
    expect(screen.getByText("+66.7%")).toBeDefined();
    expect(screen.getByText("-50.0%")).toBeDefined();

    expect(screen.getByText("แสดง 1–20 จาก 22 รายการ")).toBeDefined();
    expect(screen.getByText("หน้า 1 / 2")).toBeDefined();

    expect(fetchSpy).toHaveBeenCalledWith(
      expect.stringContaining(`${DASHBOARD_URL}?`),
      expect.objectContaining({ cache: "no-store" }),
    );
    const requestedUrl = String(fetchSpy.mock.calls[0][0]);
    expect(requestedUrl).toContain("period=30d");
    expect(requestedUrl).toContain("pageSize=20");
  });

  it("searches by name or reference and reloads the page from the API", async () => {
    await renderLoaded();

    fireEvent.change(
      screen.getByLabelText("ค้นหาผู้ใช้ อีเมล หรือเลขอ้างอิง"),
      { target: { value: "bob" } },
    );

    await waitFor(() =>
      expect(
        fetchSpy.mock.calls.some(([input]) => String(input).includes("q=bob")),
      ).toBe(true),
    );
    await waitFor(() => expect(screen.queryByText("Alice")).toBeNull());
    expect(screen.getByText("Bob")).toBeDefined();
    expect(screen.getByText("แสดง 1–1 จาก 1 รายการ")).toBeDefined();
    expect(screen.getByRole("button", { name: "ล้างตัวกรอง" })).toBeDefined();
  });

  it("switches the date range to today through the period selector", async () => {
    await renderLoaded();

    fireEvent.click(screen.getByRole("button", { name: "วันนี้" }));

    await waitFor(() =>
      expect(
        fetchSpy.mock.calls.some(([input]) => String(input).includes("period=today")),
      ).toBe(true),
    );
    await waitFor(() => expect(screen.queryByText("Bob")).toBeNull());
    expect(screen.getByText("Alice")).toBeDefined();
  });

  it("loads the next page and updates the pagination summary", async () => {
    await renderLoaded();

    expect(screen.getByText("แสดง 1–20 จาก 22 รายการ")).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "หน้าถัดไป" }));

    await waitFor(() =>
      expect(
        fetchSpy.mock.calls.some(([input]) => String(input).includes("page=2")),
      ).toBe(true),
    );
    expect(await screen.findByText("แสดง 21–22 จาก 22 รายการ")).toBeDefined();
    expect(screen.getByText("หน้า 2 / 2")).toBeDefined();
  });

  it("shows empty states for the table and charts when there is no data", async () => {
    state.entries = [];
    state.total = 0;
    state.chart = [];
    render(<TopupsManager />);

    expect(await screen.findByText("ยังไม่มีรายการเติมเงิน")).toBeDefined();
    expect(
      screen.getByText("ยังไม่มียอดเงินที่เติมสำเร็จในช่วงที่เลือก"),
    ).toBeDefined();
    expect(screen.getByText("ยังไม่มีรายการเติมเงินในช่วงที่เลือก")).toBeDefined();
    expect(screen.getByText("0 รายการ")).toBeDefined();
  });

  it("shows the API error alert and recovers when retrying", async () => {
    state.dashboardError = true;
    render(<TopupsManager />);

    expect(
      await screen.findByText("โหลดประวัติการเติมเงินไม่สำเร็จ"),
    ).toBeDefined();
    expect(
      screen.getAllByText("เชื่อมต่อเซิร์ฟเวอร์ไม่สำเร็จ").length,
    ).toBeGreaterThan(0);

    state.dashboardError = false;
    fireEvent.click(screen.getByRole("button", { name: "ลองใหม่" }));

    expect(await screen.findByText("Alice")).toBeDefined();
  });

  it("shows the range error reported by the API", async () => {
    state.rangeError = "ช่วงเวลาต้องไม่เกิน 366 วัน";
    render(<TopupsManager />);

    expect(
      await screen.findByText("ช่วงเวลาต้องไม่เกิน 366 วัน"),
    ).toBeDefined();
    expect(screen.getByText("ช่วงเวลาไม่ถูกต้อง")).toBeDefined();
  });

  it("opens the detail dialog with attempts, response excerpts and events", async () => {
    await renderLoaded();

    fireEvent.click(
      screen.getByRole("button", {
        name: "ดูรายละเอียด TP-2026-11-18-0001",
      }),
    );

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("รายละเอียดรายการเติมเงิน")).toBeDefined();
    expect(
      within(dialog).getAllByText(/TP-2026-11-18-0001/).length,
    ).toBeGreaterThan(0);
    expect(within(dialog).getAllByText("Alice").length).toBeGreaterThan(0);
    expect(within(dialog).getByText("alice@example.com")).toBeDefined();
    expect(within(dialog).getByText("ครั้งที่ 1")).toBeDefined();
    expect(within(dialog).getAllByText("EasySlip").length).toBeGreaterThan(0);
    expect(within(dialog).getByText(/EXCERPT-UNIQUE-123/)).toBeDefined();
    expect(within(dialog).getByText("ไทม์ไลน์รายการ")).toBeDefined();
    expect(
      within(dialog).getByRole("button", { name: "ตรวจสอบสลิปอีกครั้ง" }),
    ).toBeDefined();
    expect(state.detailCalls).toBe(1);
    expect(
      fetchSpy.mock.calls.some(([input]) => String(input) === DETAIL_URL),
    ).toBe(true);
  });

  it("re-verifies a stuck slip through the admin endpoint and refreshes the detail", async () => {
    await renderLoaded();

    fireEvent.click(
      screen.getByRole("button", {
        name: "ดูรายละเอียด TP-2026-11-18-0001",
      }),
    );
    const dialog = await screen.findByRole("dialog");

    fireEvent.click(
      within(dialog).getByRole("button", { name: "ตรวจสอบสลิปอีกครั้ง" }),
    );

    await waitFor(() => expect(state.verifyCalls).toBe(1));
    const verifyCall = fetchSpy.mock.calls.find(
      ([input]) => String(input) === VERIFY_URL,
    );
    expect(verifyCall).toBeDefined();
    expect((verifyCall?.[1] as RequestInit | undefined)?.method).toBe("POST");

    expect(
      await screen.findByText("ตรวจสอบสลิปผ่านและเพิ่มยอดเงินแล้ว"),
    ).toBeDefined();
    await waitFor(() =>
      expect(within(dialog).getByText(/EXCERPT-AFTER-RETRY/)).toBeDefined(),
    );
    expect(within(dialog).getByText("ครั้งที่ 2")).toBeDefined();
    expect(state.detailCalls).toBeGreaterThanOrEqual(2);
  });

  it("shows a detail loading error inside the dialog", async () => {
    await renderLoaded();
    state.detailError = true;

    fireEvent.click(
      screen.getByRole("button", {
        name: "ดูรายละเอียด TP-2026-11-18-0001",
      }),
    );

    const dialog = await screen.findByRole("dialog");
    expect(
      (await within(dialog).findAllByText("โหลดรายละเอียดไม่สำเร็จ")).length,
    ).toBeGreaterThan(0);
    expect(
      within(dialog).getAllByText("โหลดรายละเอียดไม่สำเร็จ").length,
    ).toBeGreaterThan(0);
  });
});
