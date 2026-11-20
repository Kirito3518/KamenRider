import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AdminTopupSettingsResponse, BankAccountAdmin } from "@/lib/topup/types";
import { TopupSettingsManager } from "@/components/admin/settings/topup-settings-manager";

const toastMocks = vi.hoisted(() => ({
  success: vi.fn(),
  error: vi.fn(),
}));

vi.mock("react-hot-toast", () => ({ default: toastMocks }));

const SETTINGS_URL = "/api/admin/topup-settings";
const EASYSLIP_PROVIDER_URL = "/api/admin/topup-providers/easyslip";
const BANK_ACCOUNTS_URL = "/api/admin/topup-bank-accounts";

const BASE_UPDATED_AT = 1_763_100_000_000;
const EASYSLIP_UPDATED_AT = 1_763_100_010_000;
const SLIPOK_UPDATED_AT = 1_763_100_020_000;

/** A raw key that must never be rendered by the admin page. */
const RAW_EASYSLIP_KEY = "8f4c2a1b-9d3e-4f60-8a91-b2c3d4e5abcd";
const RAW_SLIPOK_KEY = "slipok-super-secret-value";

const BANK_ACCOUNT: BankAccountAdmin = {
  id: "bank-1",
  bankCode: "004",
  bankName: "ธนาคารกสิกรไทย",
  accountNumber: "1234567890",
  accountName: "ร้านค้าทดสอบ",
  logoKey: "kbank",
  logoDataUrl: null,
  isActive: true,
  archivedAt: null,
  createdAt: 1_762_900_000_000,
  updatedAt: 1_763_000_000_000,
};

function initialData(): AdminTopupSettingsResponse {
  return {
    settings: {
      truemoney: { enabled: true, phone: "0812345678" },
      promptpay: {
        enabled: false,
        type: "msisdn",
        number: "0899999999",
        accountName: "ร้านค้าทดสอบ",
      },
      bankTransfer: { enabled: false },
      coupon: {
        enabled: true,
        message: "เติมเงินครบ 100 บาท รับคูปองส่วนลด 10 บาท",
      },
      slipProvider: "easyslip",
      updatedAt: BASE_UPDATED_AT,
      updatedBy: "admin-1",
      updatedByName: "ผู้ดูแลระบบ",
    },
    providers: {
      easyslip: {
        provider: "easyslip",
        configured: true,
        maskedKey: "••••••••f6a7",
        branchId: null,
        lastTestAt: 1_763_000_100_000,
        lastTestOk: true,
        lastTestMessage: "เชื่อมต่อ EasySlip สำเร็จ",
        updatedAt: EASYSLIP_UPDATED_AT,
      },
      slipok: {
        provider: "slipok",
        configured: false,
        maskedKey: "",
        branchId: null,
        lastTestAt: null,
        lastTestOk: null,
        lastTestMessage: null,
        updatedAt: SLIPOK_UPDATED_AT,
      },
    },
    bankAccounts: [BANK_ACCOUNT],
    audit: [
      {
        id: "audit-1",
        actorName: "ผู้ดูแลระบบ",
        action: "settings.update",
        targetType: "settings",
        targetId: "1",
        changes: "{}",
        createdAt: BASE_UPDATED_AT,
      },
    ],
    pendingVerifications: 0,
  };
}

function cloneData(data: AdminTopupSettingsResponse): AdminTopupSettingsResponse {
  return JSON.parse(JSON.stringify(data)) as AdminTopupSettingsResponse;
}

let serverData: AdminTopupSettingsResponse;
let getErrorStatus: number | null;
let settingsPutError: { status: number; body: unknown } | null;

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function bodyOf(init?: RequestInit): Record<string, unknown> {
  return JSON.parse(String(init?.body ?? "{}")) as Record<string, unknown>;
}

async function handleFetch(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<Response> {
  const url = String(input);
  const method = init?.method ?? "GET";

  if (url === SETTINGS_URL && method === "GET") {
    if (getErrorStatus !== null) {
      return json({ error: "อ่านการตั้งค่าไม่สำเร็จ (ทดสอบ)" }, getErrorStatus);
    }
    return json(cloneData(serverData));
  }

  if (url === SETTINGS_URL && method === "PUT") {
    if (settingsPutError) {
      return json(settingsPutError.body, settingsPutError.status);
    }
    const body = bodyOf(init);
    const next = cloneData(serverData);
    next.settings.updatedAt = Number(body.expectedUpdatedAt) + 1;
    const truemoney = body.truemoney as
      | { enabled?: boolean; phone?: string | null }
      | undefined;
    if (truemoney) {
      next.settings.truemoney = {
        enabled: Boolean(truemoney.enabled),
        phone: truemoney.phone ?? null,
      };
    }
    const promptpay = body.promptpay as
      | { enabled?: boolean; type?: "msisdn" | "natId" }
      | undefined;
    if (promptpay) {
      next.settings.promptpay = {
        ...next.settings.promptpay,
        enabled: Boolean(promptpay.enabled),
        ...(promptpay.type ? { type: promptpay.type } : {}),
      };
    }
    if (typeof body.slipProvider === "string") {
      next.settings.slipProvider = body.slipProvider as "easyslip" | "slipok";
    }
    serverData = next;
    return json(next);
  }

  if (url === EASYSLIP_PROVIDER_URL && method === "PUT") {
    const body = bodyOf(init);
    const next = cloneData(serverData);
    if (body.clearApiKey === true) {
      next.providers.easyslip = {
        ...next.providers.easyslip,
        configured: false,
        maskedKey: "",
        updatedAt: Number(body.expectedUpdatedAt) + 1,
      };
    } else if (typeof body.apiKey === "string") {
      next.providers.easyslip = {
        ...next.providers.easyslip,
        configured: true,
        maskedKey: `••••••••${body.apiKey.slice(-4)}`,
        updatedAt: Number(body.expectedUpdatedAt) + 1,
      };
    }
    serverData = next;
    return json(next);
  }

  if (url === BANK_ACCOUNTS_URL && method === "POST") {
    const body = bodyOf(init);
    const account: BankAccountAdmin = {
      id: "bank-2",
      bankCode: String(body.bankCode),
      bankName: String(body.bankName),
      accountNumber: String(body.accountNumber),
      accountName: String(body.accountName),
      logoKey: typeof body.logoKey === "string" ? body.logoKey : null,
      logoDataUrl:
        typeof body.logoDataUrl === "string" ? body.logoDataUrl : null,
      isActive: body.isActive !== false,
      archivedAt: null,
      createdAt: 1_763_100_500_000,
      updatedAt: 1_763_100_500_000,
    };
    serverData.bankAccounts.push(account);
    return json({ bankAccount: account });
  }

  throw new Error(`Unexpected fetch: ${method} ${url}`);
}

function mutationCalls(method: string) {
  return fetchSpy.mock.calls.filter((call) => {
    const init = call[1] as RequestInit | undefined;
    return (init?.method ?? "GET") === method;
  });
}

let fetchSpy: ReturnType<typeof vi.fn>;

beforeEach(() => {
  serverData = initialData();
  getErrorStatus = null;
  settingsPutError = null;
  toastMocks.success.mockReset();
  toastMocks.error.mockReset();
  fetchSpy = vi.fn(handleFetch);
  vi.stubGlobal("fetch", fetchSpy);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

async function renderLoaded() {
  render(<TopupSettingsManager />);
  await screen.findByText("TrueMoney Angpao");
}

describe("TopupSettingsManager", () => {
  it("renders every settings section, masked keys, badges and bank accounts", async () => {
    await renderLoaded();

    const tabs = screen.getAllByRole("tab");
    expect(tabs).toHaveLength(5);
    for (const label of [
      "TrueMoney",
      "PromptPay QR",
      "บัญชีธนาคาร",
      "ตรวจสอบสลิป",
      "คูปอง",
    ]) {
      expect(screen.getByRole("tab", { name: label })).toBeDefined();
    }

    // Channel cards
    expect(screen.getByText("TrueMoney Angpao")).toBeDefined();
    expect(screen.getByText("บัญชีธนาคารสำหรับโอนเงิน")).toBeDefined();
    expect(screen.getByText("ผู้ให้บริการตรวจสลิป")).toBeDefined();
    expect(screen.getByText("ตัวอย่างข้อความคูปอง")).toBeDefined();
    expect(screen.getByText("ตั้งค่าเติมเงิน")).toBeDefined();

    // Masked provider keys are shown, raw secrets never are.
    expect(screen.getByText("••••••••f6a7")).toBeDefined();
    expect(document.body.textContent).not.toContain(RAW_EASYSLIP_KEY);
    expect(document.body.textContent).not.toContain(RAW_SLIPOK_KEY);
    expect(document.querySelector('input[type="password"]')).not.toBeNull();

    // Configured / test status badges
    expect(screen.getAllByText("ตั้งค่าแล้ว").length).toBeGreaterThan(0);
    expect(screen.getAllByText("ยังไม่ได้ตั้งค่า").length).toBeGreaterThan(0);
    expect(screen.getByText("ทดสอบล่าสุดสำเร็จ")).toBeDefined();

    // Bank accounts and the one-rail-at-a-time summary
    expect(screen.getByText("ธนาคารกสิกรไทย")).toBeDefined();
    expect(screen.getByText("123-456-789-0")).toBeDefined();
    expect(screen.getAllByText("ร้านค้าทดสอบ").length).toBeGreaterThan(0);
    expect(screen.getByText("ยังไม่เปิดช่องทาง")).toBeDefined();

    // Audit trail is human readable and shows no key material.
    expect(screen.getByText("บันทึกการตั้งค่าเติมเงิน")).toBeDefined();
  });

  it("validates the TrueMoney phone before saving and sends the expected payload", async () => {
    await renderLoaded();

    const phoneInput = screen.getByLabelText(
      "เบอร์โทรศัพท์ผู้รับซองอั่งเปา",
    );
    fireEvent.change(phoneInput, { target: { value: "123" } });
    fireEvent.click(screen.getByRole("button", { name: "บันทึก TrueMoney" }));

    expect(
      await screen.findByText(
        "เบอร์โทรศัพท์ผู้รับซองอั่งเปาต้องเป็น 10 หลักขึ้นต้นด้วย 0",
      ),
    ).toBeDefined();
    expect(mutationCalls("PUT")).toHaveLength(0);

    fireEvent.change(phoneInput, { target: { value: "0891234567" } });
    fireEvent.click(screen.getByRole("button", { name: "บันทึก TrueMoney" }));

    await waitFor(() => expect(mutationCalls("PUT")).toHaveLength(1));
    const [url, init] = mutationCalls("PUT")[0] as [string, RequestInit];
    expect(url).toBe(SETTINGS_URL);
    expect(bodyOf(init)).toEqual({
      expectedUpdatedAt: BASE_UPDATED_AT,
      truemoney: { enabled: true, phone: "0891234567" },
    });
    expect(toastMocks.success).toHaveBeenCalled();
  });

  it("shows the stale-data banner with a refresh action when a save returns 409", async () => {
    settingsPutError = {
      status: 409,
      body: {
        error: "ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรชแล้วลองใหม่",
        code: "settings-conflict",
      },
    };
    await renderLoaded();

    fireEvent.change(screen.getByLabelText("เบอร์โทรศัพท์ผู้รับซองอั่งเปา"), {
      target: { value: "0891112222" },
    });
    fireEvent.click(screen.getByRole("button", { name: "บันทึก TrueMoney" }));

    expect(
      (await screen.findAllByText("ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรช")).length,
    ).toBeGreaterThan(0);
    expect(
      screen.getByRole("button", { name: "รีเฟรชข้อมูล" }),
    ).toBeDefined();
  });

  it("opens the bank-account dialog and saves an account through the API", async () => {
    await renderLoaded();

    fireEvent.click(screen.getByRole("tab", { name: "บัญชีธนาคาร" }));
    fireEvent.click(screen.getByRole("button", { name: "เพิ่มบัญชีธนาคาร" }));

    const dialog = await screen.findByRole("dialog");
    expect(
      within(dialog).getByText("เพิ่มบัญชีธนาคาร"),
    ).toBeDefined();

    fireEvent.change(within(dialog).getByLabelText("เลขที่บัญชี"), {
      target: { value: "9876543210" },
    });
    fireEvent.change(within(dialog).getByLabelText("ชื่อบัญชี"), {
      target: { value: "ร้านค้าใหม่" },
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "บันทึกบัญชีธนาคาร" }),
    );

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    const postCalls = fetchSpy.mock.calls.filter(
      ([input, init]) =>
        String(input) === BANK_ACCOUNTS_URL &&
        (init as RequestInit | undefined)?.method === "POST",
    );
    expect(postCalls).toHaveLength(1);
    const payload = bodyOf((postCalls[0] as unknown[])[1] as RequestInit);
    expect(payload).toMatchObject({
      bankCode: "002",
      bankName: "ธนาคารกรุงเทพ",
      accountNumber: "9876543210",
      accountName: "ร้านค้าใหม่",
      logoKey: "bbl",
      logoDataUrl: null,
      isActive: true,
    });
    expect(await screen.findByText("ร้านค้าใหม่")).toBeDefined();
  });

  it("saves a new EasySlip key, clears the input and only shows the masked key", async () => {
    await renderLoaded();

    fireEvent.click(screen.getByRole("tab", { name: "ตรวจสอบสลิป" }));
    const keyInput = screen.getByLabelText(
      "API Key ใหม่ (UUID)",
    ) as HTMLInputElement;
    expect(keyInput.type).toBe("password");
    expect(keyInput.value).toBe("");

    fireEvent.change(keyInput, { target: { value: RAW_EASYSLIP_KEY } });
    const easyslipCard = screen
      .getByText("••••••••f6a7")
      .closest('[data-slot="card"]') as HTMLElement;
    fireEvent.click(
      within(easyslipCard).getByRole("button", { name: "บันทึกคีย์" }),
    );

    await waitFor(() => expect(keyInput.value).toBe(""));
    expect(await screen.findByText("••••••••abcd")).toBeDefined();
    expect(document.body.textContent).not.toContain(RAW_EASYSLIP_KEY);
    expect(mutationCalls("PUT")).toHaveLength(1);
    const providerCall = mutationCalls("PUT")[0] as [string, RequestInit];
    expect(providerCall[0]).toBe(EASYSLIP_PROVIDER_URL);
    expect(bodyOf(providerCall[1])).toEqual({
      expectedUpdatedAt: EASYSLIP_UPDATED_AT,
      apiKey: RAW_EASYSLIP_KEY,
    });
  });

  it("shows a retryable error state when the settings request fails", async () => {
    getErrorStatus = 500;
    render(<TopupSettingsManager />);

    expect(
      await screen.findByText("โหลดการตั้งค่าไม่สำเร็จ"),
    ).toBeDefined();
    expect(screen.getByRole("alert")).toBeDefined();

    getErrorStatus = null;
    fireEvent.click(screen.getByRole("button", { name: "ลองใหม่" }));

    expect(await screen.findByText("TrueMoney Angpao")).toBeDefined();
    expect(
      fetchSpy.mock.calls.filter(([input]) => String(input) === SETTINGS_URL),
    ).toHaveLength(2);
  });
});
