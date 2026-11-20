// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { easySlipGenerateQr, easySlipVerifySlip } from "@/lib/topup/providers/easyslip";
import { slipOkVerifySlip } from "@/lib/topup/providers/slipok";
import { trueMoneyClaim } from "@/lib/topup/providers/truemoney";

const FILE = {
  bytes: new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10]),
  mime: "image/jpeg",
  fileName: "slip.jpg",
};

function json(body: unknown, status = 200, headers: Record<string, string> = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...headers },
  });
}

describe("EasySlip provider", () => {
  it("generates a PromptPay QR with the exact amount and parses the image", async () => {
    const fetchImpl = vi.fn(async () =>
      json({
        status: 200,
        data: {
          image: "aGVsbG8=",
          mime: "image/png",
          payload: "000201...",
        },
      }),
    ) as unknown as typeof fetch;

    const result = await easySlipGenerateQr(
      {
        apiKey: "test-key",
        promptpayType: "msisdn",
        promptpayNumber: "0812345678",
        amountSatang: 10_050,
      },
      fetchImpl,
    );
    expect(result.kind).toBe("ok");
    if (result.kind !== "ok") return;
    expect(result.data.imageDataUrl).toBe("data:image/png;base64,aGVsbG8=");
    const [url, init] = (fetchImpl as unknown as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(String(url)).toContain("/v1/qr/generate");
    expect((init as RequestInit).method).toBe("POST");
    const body = JSON.parse(String((init as RequestInit).body));
    expect(body).toEqual({
      type: "PROMPTPAY",
      msisdn: "0812345678",
      amount: 100.5,
    });
  });

  it("maps duplicate_slip to a definitive provider failure", async () => {
    const fetchImpl = vi.fn(async () =>
      json({ status: 400, message: "duplicate_slip", data: {} }, 400),
    ) as unknown as typeof fetch;
    const result = await easySlipVerifySlip(
      {
        apiKey: "test-key",
        file: FILE,
        expectedAmountSatang: 10_000,
        checkDuplicate: true,
      },
      fetchImpl,
    );
    expect(result.kind).toBe("provider-error");
    if (result.kind === "ok") return;
    expect(result.code).toBe("duplicate_slip");
    expect(result.retryable).toBe(false);
  });

  it("normalizes receiver and amount from the verify response", async () => {
    const fetchImpl = vi.fn(async () =>
      json(
        {
          status: 200,
          data: {
            transRef: "TRANS-1",
            date: "2026-11-20T10:00:00+07:00",
            amount: { amount: 100 },
            sender: { account: { name: { th: "นาย ผู้โอน" } } },
            receiver: {
              bank: { id: "004", name: "กสิกรไทย" },
              account: {
                name: { th: "ร้าน ทดสอบ" },
                proxy: { type: "MSISDN", account: "081xxx5678" },
              },
            },
          },
        },
        200,
        { "x-request-id": "req-1" },
      ),
    ) as unknown as typeof fetch;
    const result = await easySlipVerifySlip(
      {
        apiKey: "test-key",
        file: FILE,
        expectedAmountSatang: 10_000,
        checkDuplicate: true,
      },
      fetchImpl,
    );
    expect(result.kind).toBe("ok");
    if (result.kind !== "ok") return;
    expect(result.data).toMatchObject({
      provider: "easyslip",
      transRef: "TRANS-1",
      amountSatang: 10_000,
      receiverName: "ร้าน ทดสอบ",
      receiverBankCode: "004",
      receiverProxyType: "MSISDN",
      receiverProxyValue: "081xxx5678",
    });
    expect(result.requestId).toBe("req-1");
  });
});

describe("SlipOk provider", () => {
  it("uses the branch URL and x-authorization header, then normalizes the slip", async () => {
    const fetchImpl = vi.fn(async () =>
      json({
        success: true,
        data: {
          success: true,
          transRef: "SLIPOK-1",
          transTimestamp: "2026-11-20T03:00:00.000Z",
          receivingBank: "014",
          amount: 250.5,
          receiver: {
            displayName: "ร้าน ทดสอบ",
            account: { type: "BANKAC", value: "xxx-x-x1234-x" },
          },
          sender: { displayName: "นาย ผู้โอน" },
        },
      }),
    ) as unknown as typeof fetch;

    const result = await slipOkVerifySlip(
      {
        apiKey: "slipok-key",
        branchId: "branch-1",
        file: FILE,
        expectedAmountSatang: 25_050,
        checkDuplicate: false,
      },
      fetchImpl,
    );
    expect(result.kind).toBe("ok");
    if (result.kind !== "ok") return;
    expect(result.data).toMatchObject({
      provider: "slipok",
      transRef: "SLIPOK-1",
      amountSatang: 25_050,
      receiverName: "ร้าน ทดสอบ",
      receiverBankCode: "014",
      receiverAccountNumber: "xxx-x-x1234-x",
    });
    const [url, init] = (fetchImpl as unknown as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(String(url)).toContain("/api/line/apikey/branch-1");
    expect((init as RequestInit).headers).toMatchObject({
      "x-authorization": "slipok-key",
    });
    expect((init as RequestInit).body).toBeInstanceOf(FormData);
    const form = (init as RequestInit).body as FormData;
    expect(form.get("amount")).toBe("250.5");
    expect(form.get("log")).toBe("false");
  });

  it("maps duplicate code 1012", async () => {
    const fetchImpl = vi.fn(async () =>
      json({ code: 1012, message: "สลิปซ้ำ" }, 400),
    ) as unknown as typeof fetch;
    const result = await slipOkVerifySlip(
      {
        apiKey: "slipok-key",
        branchId: "branch-1",
        file: FILE,
        expectedAmountSatang: 10_000,
        checkDuplicate: false,
      },
      fetchImpl,
    );
    expect(result.kind).toBe("provider-error");
    if (result.kind === "ok") return;
    expect(result.code).toBe("1012");
  });
});

describe("TrueMoney provider", () => {
  it("claims a voucher and parses the amount from a string", async () => {
    const fetchImpl = vi.fn(async () =>
      json(
        {
          success: true,
          status: 200,
          message: "รับเงินสำเร็จ",
          code: "SUCCESS",
          data: { name: "นาย ทดสอบ", amount: "10.00" },
        },
        200,
        { "x-request-id": "tm-1" },
      ),
    ) as unknown as typeof fetch;
    const result = await trueMoneyClaim(
      { phone: "0812345678", voucherId: "abc123" },
      fetchImpl,
    );
    expect(result.kind).toBe("ok");
    if (result.kind !== "ok") return;
    expect(result.data.amountSatang).toBe(1_000);
    expect(result.data.requestId).toBe("tm-1");
    const body = JSON.parse(
      String((fetchImpl as unknown as ReturnType<typeof vi.fn>).mock.calls[0][1].body),
    );
    expect(body).toEqual({ phone: "0812345678", voucher_id: "abc123" });
  });

  it("maps 429 with Retry-After", async () => {
    const fetchImpl = vi.fn(async () =>
      json(
        { success: false, status: 429, message: "ยิงคำขอเร็วเกินไป", code: "RATE_LIMITED" },
        429,
        { "retry-after": "30" },
      ),
    ) as unknown as typeof fetch;
    const result = await trueMoneyClaim(
      { phone: "0812345678", voucherId: "abc123" },
      fetchImpl,
    );
    expect(result.kind).toBe("provider-error");
    if (result.kind === "ok") return;
    expect(result.retryAfterSeconds).toBe(30);
    expect(result.retryable).toBe(true);
  });

  it("does not mark a timeout as retryable because the claim may have happened", async () => {
    const fetchImpl = vi.fn(async () => {
      const error = new Error("timed out");
      error.name = "TimeoutError";
      throw error;
    }) as unknown as typeof fetch;
    const result = await trueMoneyClaim(
      { phone: "0812345678", voucherId: "abc123" },
      fetchImpl,
    );
    expect(result.kind).toBe("timeout");
    if (result.kind !== "timeout") return;
    expect(result.retryable).toBe(false);
  });
});
