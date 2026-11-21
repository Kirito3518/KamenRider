import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TopupPage } from "@/components/topup/topup-page";
import { BankLogo } from "@/components/topup/topup-display";

vi.mock("@/components/ui/layout/Navbar", () => ({ Navbar: () => null }));
vi.mock("@/components/ui/layout/Footer", () => ({ Footer: () => null }));

const CONFIG = {
  minSatang: 2_000,
  maxSatang: 10_000_000,
  qrExpirySeconds: 900,
  bankExpirySeconds: 86_400,
  slipVerificationAvailable: true,
  bankRail: "promptpay_qr",
  truemoney: { enabled: true },
  promptpay: {
    enabled: true,
    type: "msisdn",
    numberMasked: "081-•••-5678",
    accountName: "ร้านทดสอบ",
  },
  bankTransfer: { enabled: false, accounts: [] },
  coupon: { enabled: true, message: "คูปองจะเปิดให้บริการเร็ว ๆ นี้" },
};

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

const TOPUP = {
  id: "topup-1",
  reference: "TP20261120ABCD1234",
  channel: "promptpay",
  status: "pending",
  requestedSatang: 10_000,
  creditedSatang: 0,
  createdAt: Date.now(),
  completedAt: null,
  expiresAt: Date.now() + 900_000,
  verificationStatus: "pending",
  failureCode: null,
  failureMessage: null,
  bankName: null,
  bankCode: null,
  receiverAccountName: "ร้านทดสอบ",
  updatedAt: Date.now(),
  qrImageDataUrl: "data:image/png;base64,aGVsbG8=",
  qrPayload: "000201",
  qrExpiresAt: Date.now() + 900_000,
  receiverAccountNumber: null,
  promptpayType: "msisdn",
  promptpayNumberMasked: "081-•••-5678",
  truemoneyPhoneMasked: null,
  slipProvider: null,
  slipTransRef: null,
  slipAmountSatang: null,
  slipSenderName: null,
  slipReceiverName: null,
  slipReceiverAccountMasked: null,
  slipImageUrl: null,
  verifiedAt: null,
  verificationCode: null,
  verificationMessage: null,
  attemptsCount: 0,
  canResume: true,
  canRetry: false,
  events: [],
};

describe("TopupPage", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchSpy = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("/api/topups/config")) return json(CONFIG);
      if (url.includes("/api/topups/promptpay")) {
        return json({
          topup: TOPUP,
          balanceSatang: 100_000,
          repeated: false,
          outcome: "pending",
          message: "สร้าง QR แล้ว",
        });
      }
      throw new Error(`unexpected fetch ${url}`);
    });
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders the channel list with TrueMoney selected by default", async () => {
    render(<TopupPage initialBalance={100_000} />);

    expect(await screen.findByText("ช่องทางการเติมเงิน")).toBeDefined();
    expect(screen.getByText("TrueMoney อั่งเปา")).toBeDefined();
    expect(screen.getByText("PromptPay QR Code")).toBeDefined();
    expect(screen.getByText("คูปอง")).toBeDefined();
    expect(screen.getByText("฿1,000.00")).toBeDefined();
    expect(screen.getByLabelText("ลิงก์ซองอั่งเปา TrueMoney")).toBeDefined();
  });

  it("opens the coupon dialog without calling an API", async () => {
    render(<TopupPage initialBalance={100_000} />);
    await screen.findByText("ช่องทางการเติมเงิน");

    fireEvent.click(screen.getByRole("button", { name: /คูปอง/ }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("ใช้รหัสคูปอง")).toBeDefined();
    expect(within(dialog).getByText("คูปองอยู่ระหว่างเปิดให้บริการ")).toBeDefined();
    expect(within(dialog).getByText("คูปองจะเปิดให้บริการเร็ว ๆ นี้")).toBeDefined();

    const calls = fetchSpy.mock.calls.map((call) => String(call[0]));
    expect(calls.some((url) => url.includes("coupon"))).toBe(false);
  });

  it("creates a PromptPay transaction and renders the QR upload flow", async () => {
    render(<TopupPage initialBalance={100_000} />);
    await screen.findByText("ช่องทางการเติมเงิน");

    fireEvent.click(screen.getByRole("button", { name: /PromptPay QR Code/ }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("เติมเงินด้วย PromptPay QR Code")).toBeDefined();

    const amount = within(dialog).getByLabelText("จำนวนเงินที่ต้องการเติม (บาท)");
    fireEvent.change(amount, { target: { value: "100" } });
    fireEvent.click(within(dialog).getByText("สร้าง QR PromptPay"));

    expect(await within(dialog).findByAltText(/QR PromptPay/)).toBeDefined();
    expect(within(dialog).getByText("บันทึกรูป QR")).toBeDefined();
    expect(
      within(dialog).getByText("อัปโหลดสลิปใบเสร็จ หรือวางไฟล์ที่นี่"),
    ).toBeDefined();
    expect(within(dialog).getByRole("button", { name: "ยืนยันสลิป" })).toBeDefined();
  });

  it("shows a configuration error with retry", async () => {
    fetchSpy.mockResolvedValueOnce(json({ error: "ระบบขัดข้อง" }, 500));
    render(<TopupPage initialBalance={0} />);
    expect(await screen.findByText("โหลดข้อมูลการเติมเงินไม่สำเร็จ")).toBeDefined();
    expect(screen.getByText("ระบบขัดข้อง")).toBeDefined();
    expect(screen.getByText("ลองใหม่")).toBeDefined();
  });
});

describe("BankLogo", () => {
  it("puts preset bank marks on the bank brand colour", () => {
    render(<BankLogo bankCode="004" bankName="ธนาคารกสิกรไทย" />);

    const image = screen.getByAltText("โลโก้ ธนาคารกสิกรไทย");
    const tile = image.parentElement as HTMLElement;
    const probe = document.createElement("span");
    probe.style.backgroundColor = "#138f2d";

    expect(tile.style.backgroundColor).toBe(probe.style.backgroundColor);
  });
});
