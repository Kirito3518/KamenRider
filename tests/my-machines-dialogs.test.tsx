import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MoonlightDialog } from "@/components/my-machines/moonlight-dialog";
import { ParsecDialog } from "@/components/my-machines/parsec-dialog";
import { ExtendRentalDialog } from "@/components/my-machines/extend-rental-dialog";
import type { Lease } from "@/lib/machine-contracts";
import type { ParsecLeaseState } from "@/lib/parsec-contracts";

const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("react-hot-toast", () => ({ default: toast }));
vi.mock("motion/react", async (importOriginal) => {
  const actual = await importOriginal<typeof import("motion/react")>();
  return { ...actual, useReducedMotion: () => true };
});

const sealMock = vi.hoisted(() =>
  vi.fn(async () => ({
    wrappedKey: "wrapped-key",
    iv: "initialization-vector",
    ciphertext: "ciphertext",
  })),
);
vi.mock("@/lib/parsec-crypto", () => ({
  sealParsecCredentials: sealMock,
}));

const NOW = Date.parse("2026-11-08T03:30:00.000Z");
const FUTURE = "2026-11-08T03:40:00.000Z";
const ATTEMPT_EXPIRY = "2026-11-08T03:35:00.000Z";

const LEASE: Lease = {
  id: "rental-1",
  machineId: "pc-one",
  machineDisplayName: "PC ONE",
  state: "active",
  revision: 1,
  pendingRevision: null,
  startsAtUtc: "2026-11-08T02:30:00.000Z",
  expiresAtUtc: "2026-11-08T04:30:00.000Z",
  connection: { address: "203.0.113.7:47989", stale: false },
  agent: { reachability: "reachable", lastSeenAt: "2026-11-08T03:25:00.000Z" },
  payment: {
    hourlyPriceSatang: 40_000,
    durationSeconds: 7_200,
    extensionSeconds: 0,
    paidSatang: 80_000,
    refundedSatang: 0,
    extendable: true,
  },
  parsec: null,
};

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function attempt(
  attemptId: string,
  state: "queued" | "starting" | "waiting_email" | "ready" | "failed",
) {
  return {
    attemptId,
    state,
    errorCode: null,
    message: null,
    createdAtUtc: new Date(NOW).toISOString(),
    updatedAtUtc: new Date(NOW).toISOString(),
    expiresAtUtc: ATTEMPT_EXPIRY,
  };
}

describe("MoonlightDialog", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  let clipboard: { writeText: ReturnType<typeof vi.fn> };
  let originalClipboard: PropertyDescriptor | undefined;

  beforeEach(() => {
    vi.useFakeTimers({
      toFake: ["setInterval", "clearInterval", "Date"],
      now: NOW,
    });
    toast.success.mockReset();
    toast.error.mockReset();
    clipboard = { writeText: vi.fn(async () => undefined) };
    originalClipboard = Object.getOwnPropertyDescriptor(
      navigator,
      "clipboard",
    );
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: clipboard,
    });
    fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    if (originalClipboard) {
      Object.defineProperty(navigator, "clipboard", originalClipboard);
    } else {
      Reflect.deleteProperty(navigator, "clipboard");
    }
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  function renderDialog(lease: Lease = LEASE) {
    return render(
      <MoonlightDialog
        lease={lease}
        open
        onClose={vi.fn()}
        returnFocusRef={{ current: null }}
      />,
    );
  }

  it("shows the address with copy and pairs with the discovered PIN slot", async () => {
    fetchSpy.mockImplementation(
      async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url === "/api/my-leases/rental-1/pair" && init?.method !== "POST")
          return response({
            pairingIds: ["foundation-sunshine-single-pending-slot"],
            validUntilUtc: FUTURE,
          });
        if (
          url === "/api/my-leases/rental-1/pair" &&
          init?.method === "POST"
        ) {
          const body = JSON.parse(String(init.body));
          expect(body.pin).toBe("1234");
          expect(body.pairingId).toBe(
            "foundation-sunshine-single-pending-slot",
          );
          return response({ commandId: "command-1", status: "queued" });
        }
        if (url === "/api/my-leases/rental-1/pair/command-1")
          return response({ commandId: "command-1", status: "succeeded" });
        throw new Error(`Unexpected request: ${url}`);
      },
    );

    renderDialog();

    expect(
      await screen.findByText("203.0.113.7:47989"),
    ).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "คัดลอก" }));
    await waitFor(() =>
      expect(clipboard.writeText).toHaveBeenCalledWith("203.0.113.7:47989"),
    );
    expect(toast.success).toHaveBeenCalledWith("คัดลอกที่อยู่เชื่อมต่อแล้ว");
    expect(screen.getByRole("button", { name: "คัดลอกแล้ว" })).toBeDefined();

    fireEvent.change(screen.getByLabelText("PIN 4 หลัก"), {
      target: { value: "1234" },
    });
    fireEvent.click(screen.getByRole("button", { name: "ยืนยัน PIN" }));

    expect(await screen.findByText("จับคู่สำเร็จ")).toBeDefined();
  });

  it("keeps the PIN form disabled until a pairing slot appears", async () => {
    let offer: unknown = null;
    let offerRequests = 0;
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      if (String(input) !== "/api/my-leases/rental-1/pair")
        throw new Error(`Unexpected request: ${String(input)}`);
      offerRequests += 1;
      if (offerRequests >= 2)
        offer = {
          pairingIds: ["foundation-sunshine-single-pending-slot"],
          validUntilUtc: FUTURE,
        };
      return response(offer);
    });

    renderDialog();

    expect(
      await screen.findByText(/กำลังรอคำขอจับคู่จาก Moonlight/),
    ).toBeDefined();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2_500);
    });

    expect(await screen.findByLabelText("PIN 4 หลัก")).toBeDefined();
  });

  it("warns about a stale address and handles clipboard failures", async () => {
    clipboard.writeText.mockRejectedValueOnce(new Error("denied"));
    fetchSpy.mockResolvedValue(response(null));

    renderDialog({
      ...LEASE,
      connection: { address: "203.0.113.7:47989", stale: true },
    });

    expect(
      await screen.findByText("203.0.113.7:47989"),
    ).toBeDefined();
    expect(
      screen.getByText("ข้อมูลที่อยู่เชื่อมต่ออาจล้าสมัย — ตรวจสอบกับผู้ดูแลก่อนใช้งาน"),
    ).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "คัดลอก" }));
    expect(
      await screen.findByText(
        "คัดลอกอัตโนมัติไม่ได้ กรุณาเลือกข้อความแล้วคัดลอกเอง",
      ),
    ).toBeDefined();
    expect(toast.error).toHaveBeenCalledWith("คัดลอกที่อยู่ไม่สำเร็จ");
  });

  it("surfaces pairing conflicts from the API", async () => {
    fetchSpy.mockImplementation(
      async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url === "/api/my-leases/rental-1/pair" && init?.method !== "POST")
          return response({
            pairingIds: ["foundation-sunshine-single-pending-slot"],
            validUntilUtc: FUTURE,
          });
        if (url === "/api/my-leases/rental-1/pair" && init?.method === "POST")
          return response({ error: "Operation rejected" }, 409);
        throw new Error(`Unexpected request: ${url}`);
      },
    );

    renderDialog();
    fireEvent.change(await screen.findByLabelText("PIN 4 หลัก"), {
      target: { value: "0000" },
    });
    fireEvent.click(screen.getByRole("button", { name: "ยืนยัน PIN" }));

    expect(
      await screen.findByText(
        "รายการขัดแย้งกับสถานะล่าสุด กรุณารีเฟรชแล้วลองใหม่",
      ),
    ).toBeDefined();
    expect(toast.error).toHaveBeenCalled();
  });
});

describe("ParsecDialog", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  let parsecState: ParsecLeaseState;

  beforeEach(() => {
    vi.useFakeTimers({
      toFake: ["setInterval", "clearInterval", "Date"],
      now: NOW,
    });
    toast.success.mockReset();
    toast.error.mockReset();
    sealMock.mockClear();
    parsecState = {
      available: true,
      bootKeyId: "boot-key-1",
      publicKey: "public-key",
      attempt: null,
    };
    fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  function renderDialog() {
    return render(
      <ParsecDialog
        lease={{ ...LEASE, parsec: parsecState }}
        open
        onClose={vi.fn()}
        returnFocusRef={{ current: null }}
      />,
    );
  }

  it("submits round one, holds the e-mail alert, then submits round two", async () => {
    let postCount = 0;
    const postedBodies: Record<string, unknown>[] = [];
    fetchSpy.mockImplementation(
      async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        expect(url).toBe("/api/my-leases/rental-1/parsec");
        if (init?.method !== "POST") return response(parsecState);
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        postedBodies.push(body);
        postCount += 1;
        if (postCount === 1) {
          const first = attempt("attempt-1", "waiting_email");
          parsecState = { ...parsecState, attempt: first };
          return response(first);
        }
        const second = attempt("attempt-2", "queued");
        parsecState = { ...parsecState, attempt: second };
        return response(second);
      },
    );

    renderDialog();

    fireEvent.change(await screen.findByLabelText("อีเมล Parsec"), {
      target: { value: "renter@example.com" },
    });
    fireEvent.change(screen.getByLabelText("รหัสผ่าน Parsec"), {
      target: { value: "secret-password" },
    });
    fireEvent.click(screen.getByRole("button", { name: "เชื่อมต่อ Parsec" }));

    const alert = await screen.findByRole("alertdialog");
    expect(within(alert).getByText("ยืนยันอีเมล Parsec")).toBeDefined();
    expect(postedBodies).toHaveLength(1);
    expect(postedBodies[0]).not.toHaveProperty("retryAttemptId");
    expect(sealMock).toHaveBeenCalledTimes(1);

    await waitFor(() =>
      expect(
        (
          within(alert).getByRole("button", {
            name: "ยืนยันอีเมลแล้ว",
          }) as HTMLButtonElement
        ).disabled,
      ).toBe(false),
    );
    // The alert is acknowledgement-only: no cancel/close control and Escape is ignored.
    expect(within(alert).getAllByRole("button")).toHaveLength(1);
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("alertdialog")).not.toBeNull();

    fireEvent.click(
      within(alert).getByRole("button", { name: "ยืนยันอีเมลแล้ว" }),
    );

    await waitFor(() => expect(postedBodies).toHaveLength(2));
    expect(postedBodies[1].retryAttemptId).toBe("attempt-1");
    expect(sealMock).toHaveBeenCalledTimes(2);
    expect(toast.success).toHaveBeenCalledWith(
      "ส่งคำขอเชื่อมต่ออีกครั้งหลังยืนยันอีเมลแล้ว",
    );

    // The second attempt finishes without another e-mail challenge.
    parsecState = {
      ...parsecState,
      attempt: attempt("attempt-2", "ready"),
    };
    await act(async () => {
      await vi.advanceTimersByTimeAsync(2_000);
    });

    const finish = await within(alert).findByRole("button", {
      name: "เสร็จสิ้น",
    });
    fireEvent.click(finish);
    await waitFor(() =>
      expect(screen.queryByRole("alertdialog")).toBeNull(),
    );
    expect(await screen.findByText("เชื่อมต่อ Parsec สำเร็จแล้ว")).toBeDefined();
  });

  it("does not show e-mail confirmation controls before Parsec requires it", async () => {
    fetchSpy.mockImplementation(
      async (_input: RequestInfo | URL, init?: RequestInit) => {
        if (init?.method !== "POST") return response(parsecState);
        const first = attempt("attempt-1", "queued");
        // No waiting_email state is ever observed; the attempt completes directly.
        return response(first);
      },
    );

    renderDialog();
    fireEvent.change(await screen.findByLabelText("อีเมล Parsec"), {
      target: { value: "renter@example.com" },
    });
    fireEvent.change(screen.getByLabelText("รหัสผ่าน Parsec"), {
      target: { value: "secret-password" },
    });
    fireEvent.click(screen.getByRole("button", { name: "เชื่อมต่อ Parsec" }));

    const alert = await screen.findByRole("alertdialog");
    const action = within(alert).getByRole("button", {
      name: "กำลังรอ Parsec ส่งอีเมล…",
    }) as HTMLButtonElement;
    expect(action.disabled).toBe(true);
  });

  it("explains when the machine is not ready for Parsec", async () => {
    parsecState = {
      available: false,
      bootKeyId: null,
      publicKey: null,
      attempt: null,
    };
    fetchSpy.mockImplementation(async () => response(parsecState));

    renderDialog();

    expect(
      await screen.findByText("เครื่องนี้ยังไม่พร้อมสำหรับ Parsec"),
    ).toBeDefined();
    expect(screen.queryByLabelText("อีเมล Parsec")).toBeNull();
  });
});

describe("ExtendRentalDialog", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "Date"], now: NOW });
    toast.success.mockReset();
    toast.error.mockReset();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function renderDialog({
    balanceSatang = 100_000,
    lease = LEASE,
    onExtend = vi.fn(async () => undefined),
  }: {
    balanceSatang?: number;
    lease?: Lease;
    onExtend?: (hours: number, key: string) => Promise<void>;
  } = {}) {
    const onClose = vi.fn();
    render(
      <ExtendRentalDialog
        lease={lease}
        balanceSatang={balanceSatang}
        open
        onClose={onClose}
        onExtend={onExtend}
        returnFocusRef={{ current: null }}
      />,
    );
    return { onClose, onExtend };
  }

  it("submits the selected hours with the current revision", async () => {
    const { onClose, onExtend } = renderDialog();

    fireEvent.change(screen.getByLabelText("ระยะเวลา (ชั่วโมง)"), {
      target: { value: "2" },
    });
    fireEvent.click(screen.getByRole("button", { name: "ยืนยันต่อเวลา" }));

    await waitFor(() => expect(onExtend).toHaveBeenCalledTimes(1));
    expect(onExtend).toHaveBeenCalledWith(2, expect.stringMatching(/^[0-9a-f-]{36}$/));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(toast.success).toHaveBeenCalledWith(
      "ต่อเวลาแล้ว รอเครื่องยืนยันการเปลี่ยนแปลง",
    );
  });

  it("blocks the submission when the balance is insufficient", () => {
    renderDialog({ balanceSatang: 1_000 });

    expect(screen.getByText("ยอดเงินไม่เพียงพอ")).toBeDefined();
    expect(
      (
        screen.getByRole("button", {
          name: "ยืนยันต่อเวลา",
        }) as HTMLButtonElement
      ).disabled,
    ).toBe(true);
  });
});
