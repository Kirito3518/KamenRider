import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CustomerHistory } from "@/components/history/customer-history";

vi.mock("@/components/ui/layout/Navbar", () => ({ Navbar: () => null }));
vi.mock("@/components/ui/layout/Footer", () => ({ Footer: () => null }));

const TOPUP_ENTRY = {
  id: "topup-1",
  reference: "TP20261120ABCD1234",
  channel: "promptpay",
  status: "completed",
  requestedSatang: 10_000,
  creditedSatang: 10_000,
  createdAt: Date.parse("2026-11-20T03:00:00.000Z"),
  completedAt: Date.parse("2026-11-20T03:05:00.000Z"),
  expiresAt: Date.parse("2026-11-20T03:15:00.000Z"),
  verificationStatus: "verified",
  failureCode: null,
  failureMessage: null,
  bankName: null,
  bankCode: null,
  receiverAccountName: "ร้านทดสอบ",
};

const TOPUP_HISTORY = {
  balanceSatang: 110_000,
  summary: { total: 1, completed: 1, pending: 0, failed: 0, completedSatang: 10_000 },
  entries: [TOPUP_ENTRY],
  pagination: { page: 1, pageSize: 20, total: 1, pageCount: 1 },
  filters: {
    q: "",
    channel: "all",
    status: "all",
    period: "all",
    from: "",
    to: "",
    sort: "newest",
  },
  rangeError: "",
};

const TOPUP_DETAIL = {
  ...TOPUP_ENTRY,
  updatedAt: Date.parse("2026-11-20T03:05:00.000Z"),
  qrImageDataUrl: "data:image/png;base64,aGVsbG8=",
  qrPayload: "000201",
  qrExpiresAt: Date.parse("2026-11-20T03:15:00.000Z"),
  receiverAccountNumber: "0812345678",
  promptpayType: "msisdn",
  promptpayNumberMasked: "081-•••-5678",
  truemoneyPhoneMasked: null,
  slipProvider: "easyslip",
  slipTransRef: "REF-1",
  slipAmountSatang: 10_000,
  slipSenderName: "นาย ผู้โอน",
  slipReceiverName: "ร้านทดสอบ",
  slipReceiverAccountMasked: "081••••5678",
  slipImageUrl: null,
  verifiedAt: Date.parse("2026-11-20T03:05:00.000Z"),
  verificationCode: "verified",
  verificationMessage: "ตรวจสอบสลิปผ่าน",
  attemptsCount: 1,
  canResume: false,
  canRetry: false,
  events: [],
};

const RENTAL_HISTORY = {
  balanceSatang: 110_000,
  controllerAvailable: true,
  summary: {
    totalRentals: 1,
    activeRentals: 0,
    paidSatang: 4_000,
    refundedSatang: 0,
    netSatang: 4_000,
  },
  entries: [
    {
      rentalId: "r1",
      machineId: "pc-1",
      machineName: "PC ONE",
      state: "completed",
      startsAtUtc: "2026-11-18T03:00:00.000Z",
      expiresAtUtc: "2026-11-18T04:00:00.000Z",
      endedAtUtc: "2026-11-18T03:30:00.000Z",
      createdAtUtc: "2026-11-18T02:59:00.000Z",
      bookedSeconds: 3_600,
      extensionSeconds: 0,
      hourlyPriceSatang: 4_000,
      paidSatang: 4_000,
      refundedSatang: 0,
      netSatang: 4_000,
      refundNote: null,
      rejectReason: null,
    },
  ],
  pagination: { page: 1, pageSize: 20, total: 1, pageCount: 1 },
  filters: {
    q: "",
    state: "all",
    machineId: "all",
    period: "all",
    from: "",
    to: "",
    sort: "newest",
  },
  rangeError: "",
  machines: [{ machineId: "pc-1", displayName: "PC ONE" }],
};

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("CustomerHistory", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchSpy = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("/api/topups/topup-1")) return json({ topup: TOPUP_DETAIL, balanceSatang: 110_000 });
      if (url.includes("/api/topups")) return json(TOPUP_HISTORY);
      if (url.includes("/api/rentals")) return json(RENTAL_HISTORY);
      throw new Error(`unexpected fetch ${url}`);
    });
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("shows both history tabs and the top-up table", async () => {
    render(<CustomerHistory initialBalance={100_000} />);
    expect(await screen.findByText("TP20261120ABCD1234")).toBeDefined();
    expect(screen.getByText("PromptPay QR")).toBeDefined();
    expect(screen.getByText("สำเร็จ")).toBeDefined();
    expect(screen.getByText("฿100.00")).toBeDefined();
    expect(screen.getByRole("tab", { name: /ประวัติการเติมเงิน/ })).toBeDefined();
    expect(screen.getByRole("tab", { name: /ประวัติการเช่า/ })).toBeDefined();
  });

  it("opens the top-up detail dialog with server data", async () => {
    render(<CustomerHistory initialBalance={100_000} />);
    await screen.findByText("TP20261120ABCD1234");
    fireEvent.click(screen.getByLabelText("ดูรายละเอียด TP20261120ABCD1234"));
    expect(await screen.findByText("รายละเอียดการเติมเงิน")).toBeDefined();
    expect((await screen.findAllByText(/ร้านทดสอบ/)).length).toBeGreaterThan(0);
  });

  it("switches to the rental tab and loads rental history", async () => {
    render(<CustomerHistory initialBalance={100_000} />);
    await screen.findByText("TP20261120ABCD1234");
    fireEvent.click(screen.getByRole("tab", { name: /ประวัติการเช่า/ }));
    expect(await screen.findByText("PC ONE")).toBeDefined();
    expect(screen.getByText("เสร็จสิ้น")).toBeDefined();
    expect(screen.getAllByText("฿40.00").length).toBeGreaterThan(0);
  });
});
