import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MyMachines } from "@/components/my-machines/my-machines";

vi.mock("@/components/ui/layout/Navbar", () => ({ Navbar: () => null }));
vi.mock("@/components/ui/layout/Footer", () => ({ Footer: () => null }));
vi.mock("react-hot-toast", () => ({
  default: { success: vi.fn(), error: vi.fn() },
}));
vi.mock("motion/react", async (importOriginal) => {
  const actual = await importOriginal<typeof import("motion/react")>();
  return { ...actual, useReducedMotion: () => true };
});

const authState = vi.hoisted(() => ({ balanceSatang: 100_000 }));
vi.mock("@/lib/auth-client", () => ({
  authClient: {
    useSession: () => ({
      data: {
        user: {
          id: "renter-1",
          name: "Renter",
          email: "renter@example.com",
          image: null,
          balanceSatang: authState.balanceSatang,
          role: "user",
        },
      },
      isPending: false,
      refetch: vi.fn(async () => ({ data: null })),
    }),
  },
}));

const NOW = Date.parse("2026-11-08T03:30:00.000Z");
const HEARTBEAT_AT = "2026-11-08T03:25:00.000Z";

function payment(overrides: Record<string, unknown> = {}) {
  return {
    hourlyPriceSatang: 40_000,
    durationSeconds: 7_200,
    extensionSeconds: 0,
    paidSatang: 80_000,
    refundedSatang: 0,
    extendable: true,
    ...overrides,
  };
}

function lease(overrides: Record<string, unknown> = {}) {
  return {
    id: "rental-1",
    machineId: "pc-one",
    machineDisplayName: "PC ONE",
    state: "active",
    revision: 1,
    pendingRevision: null,
    startsAtUtc: "2026-11-08T02:30:00.000Z",
    expiresAtUtc: "2026-11-08T04:30:00.000Z",
    endedAtUtc: null,
    connection: { address: "203.0.113.7:47989", stale: false },
    agent: { reachability: "reachable", lastSeenAt: HEARTBEAT_AT },
    payment: payment(),
    parsec: null,
    ...overrides,
  };
}

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

describe("MyMachines", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  let leases: unknown[];
  let balanceSatang: number;
  let leaseListRequests: number;
  let visibilityDescriptor: PropertyDescriptor | undefined;

  /** The lease feed returns `{ leases, balanceSatang }` after settling refunds. */
  function listResponse(nextLeases: unknown[] = leases) {
    return response({ leases: nextLeases, balanceSatang });
  }

  beforeEach(() => {
    authState.balanceSatang = 100_000;
    balanceSatang = 100_000;
    vi.useFakeTimers({
      toFake: ["setInterval", "clearInterval", "Date"],
      now: NOW,
    });
    visibilityDescriptor = Object.getOwnPropertyDescriptor(
      document,
      "visibilityState",
    );
    setVisibility("visible");
    leases = [lease()];
    leaseListRequests = 0;
    fetchSpy = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/my-leases") {
        leaseListRequests += 1;
        return listResponse();
      }
      throw new Error(`Unexpected request: ${url}`);
    });
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    if (visibilityDescriptor) {
      Object.defineProperty(document, "visibilityState", visibilityDescriptor);
    } else {
      Reflect.deleteProperty(document, "visibilityState");
    }
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("shows only active rentals and explains preparing rentals", async () => {
    leases = [
      lease(),
      lease({
        id: "rental-done",
        machineId: "pc-done",
        machineDisplayName: "PC DONE",
        state: "completed",
      }),
      lease({
        id: "rental-pending",
        machineId: "pc-pending",
        machineDisplayName: "PC PENDING",
        state: "pending",
        startsAtUtc: null,
        expiresAtUtc: null,
        connection: { address: null, stale: false },
        agent: { reachability: "unknown", lastSeenAt: null },
      }),
    ];

    render(<MyMachines />);

    expect(
      await screen.findByRole("heading", { level: 2, name: "PC ONE" }),
    ).toBeDefined();
    expect(screen.queryByText("PC DONE")).toBeNull();
    expect(screen.queryByText("PC PENDING")).toBeNull();
    expect(screen.getByText(/กำลังตรวจสอบการชำระเงิน 1 รายการ/)).toBeDefined();
    expect(screen.getByText("พร้อมเชื่อมต่อ")).toBeDefined();
    expect(screen.getByText("กำลังใช้งาน")).toBeDefined();
    expect(screen.getByText("฿800.00")).toBeDefined();
    expect(screen.getByText("฿400.00 / ชม.")).toBeDefined();
    // The wallet is shown from the locally tracked balance.
    expect(screen.getByText("฿1,000.00")).toBeDefined();
    // The ip:port lives in the Moonlight dialog, not on the card itself.
    expect(screen.queryByText("203.0.113.7:47989")).toBeNull();
  });

  it("shows the empty state when no rental is active or preparing", async () => {
    leases = [
      lease({
        id: "rental-done",
        machineId: "pc-done",
        machineDisplayName: "PC DONE",
        state: "completed",
      }),
    ];

    render(<MyMachines />);

    expect(
      await screen.findByText("ยังไม่มีเครื่องที่กำลังเช่า"),
    ).toBeDefined();
    expect(screen.queryByText("PC DONE")).toBeNull();
  });

  it("shows a power card for a prepared rental instead of the empty state", async () => {
    leases = [
      lease({
        id: "rental-pending",
        machineId: "pc-pending",
        machineDisplayName: "PC PENDING",
        state: "preparing",
        startsAtUtc: null,
        expiresAtUtc: null,
        connection: { address: null, stale: false },
      }),
    ];

    render(<MyMachines />);

    expect(
      await screen.findByRole("heading", { level: 2, name: "PC PENDING" }),
    ).toBeDefined();
    expect(screen.getByRole("button", { name: "เปิดเครื่อง PC PENDING" })).toBeDefined();
    expect(screen.queryByText("ยังไม่มีเครื่องที่กำลังเช่า")).toBeNull();
  });

  it("renders one card at a time behind machine-name tabs", async () => {
    leases = [
      lease(),
      lease({
        id: "rental-2",
        machineId: "pc-two",
        machineDisplayName: "PC TWO",
        connection: { address: "203.0.113.8:47989", stale: false },
      }),
    ];

    render(<MyMachines />);

    const tabOne = await screen.findByRole("tab", { name: /PC ONE/ });
    const tabTwo = screen.getByRole("tab", { name: /PC TWO/ });
    expect(tabOne.getAttribute("aria-selected")).toBe("true");
    expect(
      screen.getByRole("heading", { level: 2, name: "PC ONE" }),
    ).toBeDefined();
    expect(
      screen.queryByRole("heading", { level: 2, name: "PC TWO" }),
    ).toBeNull();
    expect(screen.getAllByText("เชื่อมต่อเครื่อง")).toHaveLength(1);

    fireEvent.click(tabTwo);

    expect(
      await screen.findByRole("heading", { level: 2, name: "PC TWO" }),
    ).toBeDefined();
    expect(
      screen.queryByRole("heading", { level: 2, name: "PC ONE" }),
    ).toBeNull();
    expect(screen.getAllByText("เชื่อมต่อเครื่อง")).toHaveLength(1);
  });

  it("shows the offline status when the Agent is unreachable", async () => {
    leases = [
      lease({
        agent: { reachability: "unreachable", lastSeenAt: HEARTBEAT_AT },
      }),
    ];

    render(<MyMachines />);

    expect(
      await screen.findByText("ขาดการติดต่อกับเครื่อง"),
    ).toBeDefined();
    expect(screen.getByText("ออฟไลน์")).toBeDefined();
    expect(screen.getByText("ติดต่อล่าสุด 5 นาทีที่แล้ว")).toBeDefined();
  });

  it("flags a stale connect address", async () => {
    leases = [
      lease({ connection: { address: "203.0.113.7:47989", stale: true } }),
    ];

    render(<MyMachines />);

    expect(await screen.findByText("ที่อยู่อาจเปลี่ยน")).toBeDefined();
    expect(
      screen.getByText("ที่อยู่เชื่อมต่ออาจล้าสมัย"),
    ).toBeDefined();
  });

  it("waits for a verified connect address", async () => {
    leases = [lease({ connection: { address: null, stale: false } })];

    render(<MyMachines />);

    expect(await screen.findByText("รอที่อยู่")).toBeDefined();
    expect(screen.getByText("รอที่อยู่เชื่อมต่อ")).toBeDefined();
  });

  it("shows the Parsec option only when the machine is enabled for it", async () => {
    leases = [lease()];
    const view = render(<MyMachines />);
    await screen.findByRole("heading", { level: 2, name: "PC ONE" });
    expect(screen.queryByRole("button", { name: /Parsec/ })).toBeNull();
    view.unmount();

    leases = [
      lease({
        parsec: {
          available: true,
          bootKeyId: "boot-key-1",
          publicKey: "public-key",
          attempt: null,
        },
      }),
    ];
    render(<MyMachines />);
    const parsecButton = await screen.findByRole("button", { name: /Parsec/ });
    expect((parsecButton as HTMLButtonElement).disabled).toBe(false);
  });

  it("opens Parsec when the Agent has not advertised it and explains why inside the dialog", async () => {
    const notReady = {
      available: false,
      bootKeyId: null,
      publicKey: null,
      attempt: null,
    };
    leases = [lease({ parsec: notReady })];
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/my-leases") return listResponse();
      if (url === "/api/my-leases/rental-1/parsec") return response(notReady);
      throw new Error(`Unexpected request: ${url}`);
    });

    render(<MyMachines />);

    const parsecButton = await screen.findByRole("button", {
      name: /Parsec/,
    });
    // The button is no longer disabled: the dialog itself carries the explanatory alert.
    expect((parsecButton as HTMLButtonElement).disabled).toBe(false);
    expect(screen.getByText("ยังไม่พร้อม")).toBeDefined();

    fireEvent.click(parsecButton);

    const dialog = await screen.findByRole("dialog");
    expect(
      await within(dialog).findByText("เครื่องนี้ยังไม่พร้อมสำหรับ Parsec"),
    ).toBeDefined();
    expect(within(dialog).queryByLabelText("อีเมล Parsec")).toBeNull();
  });

  it("shows an expired Parsec attempt as a timeout with a retry path, not an endless spinner", async () => {
    const parsecState = {
      available: true,
      bootKeyId: "boot-key-1",
      publicKey: "public-key",
      attempt: {
        attemptId: "attempt-1",
        state: "starting",
        errorCode: null,
        message: null,
        createdAtUtc: "2026-11-08T03:24:00.000Z",
        updatedAtUtc: "2026-11-08T03:24:00.000Z",
        expiresAtUtc: "2026-11-08T03:26:00.000Z",
      },
    };
    leases = [lease({ parsec: parsecState })];
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/my-leases") return listResponse();
      // The read path may still race with the Agent; keep reporting the stale running attempt.
      if (url === "/api/my-leases/rental-1/parsec") return response(parsecState);
      throw new Error(`Unexpected request: ${url}`);
    });

    render(<MyMachines />);

    fireEvent.click(await screen.findByRole("button", { name: /Parsec/ }));

    const dialog = await screen.findByRole("dialog");
    expect(
      await within(dialog).findByText(/หมดเวลารอผลจากเครื่อง/),
    ).toBeDefined();
    expect(within(dialog).queryByLabelText("Loading")).toBeNull();
    fireEvent.click(within(dialog).getByRole("button", { name: "ลองใหม่" }));
    expect(await within(dialog).findByLabelText("อีเมล Parsec")).toBeDefined();
  });

  it("removes leases omitted by an authoritative refresh", async () => {
    leases = [
      lease(),
      lease({
        id: "rental-2",
        machineId: "pc-two",
        machineDisplayName: "PC TWO",
      }),
    ];
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      if (String(input) !== "/api/my-leases")
        throw new Error(`Unexpected request: ${String(input)}`);
      leaseListRequests += 1;
      leases =
        leaseListRequests === 1
          ? leases
          : leases.filter(
              (item) => (item as { id: string }).id === "rental-1",
            );
      return listResponse();
    });

    render(<MyMachines />);
    expect(
      await screen.findByRole("tab", { name: /PC TWO/ }),
    ).toBeDefined();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "รีเฟรช" }));
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(leaseListRequests).toBe(2);
    expect(screen.queryByRole("tab", { name: /PC TWO/ })).toBeNull();
    expect(
      screen.getByRole("heading", { level: 2, name: "PC ONE" }),
    ).toBeDefined();
  });

  it("polls only while visible, skips overlapping requests, and clears its interval", async () => {
    const firstRequest = deferred<Response>();
    fetchSpy.mockReturnValueOnce(firstRequest.promise);
    const view = render(<MyMachines />);
    expect(fetchSpy).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetchSpy).toHaveBeenCalledTimes(1);

    firstRequest.resolve(listResponse());
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_000);
    });
    expect(fetchSpy).toHaveBeenCalledTimes(2);

    setVisibility("hidden");
    act(() => document.dispatchEvent(new Event("visibilitychange")));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetchSpy).toHaveBeenCalledTimes(2);

    setVisibility("visible");
    act(() => document.dispatchEvent(new Event("visibilitychange")));
    expect(fetchSpy).toHaveBeenCalledTimes(3);

    view.unmount();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetchSpy).toHaveBeenCalledTimes(3);
  });

  it("updates the header balance from polling and announces an unexpected refund", async () => {
    render(<MyMachines />);

    await screen.findByRole("heading", { level: 2, name: "PC ONE" });
    expect(screen.getByText("฿1,000.00")).toBeDefined();
    expect(screen.queryByText(/คืนเงินค่าเช่าที่ไม่ได้ใช้/)).toBeNull();

    // The list route settled an unused-time refund before responding.
    balanceSatang = 102_667;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_000);
    });

    expect(
      await screen.findByText(
        "คืนเงินค่าเช่าที่ไม่ได้ใช้ ฿26.67 เข้ายอดเงินแล้ว",
      ),
    ).toBeDefined();
    expect(screen.getByText("฿1,026.67")).toBeDefined();
  });

  it("does not announce a poll-driven refund while a local mutation is in flight", async () => {
    const extendRequest = deferred<Response>();
    fetchSpy.mockImplementation(
      async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url === "/api/my-leases") return listResponse();
        if (url === "/api/my-leases/rental-1/extend") {
          expect(init?.method).toBe("POST");
          return extendRequest.promise;
        }
        throw new Error(`Unexpected request: ${url}`);
      },
    );

    render(<MyMachines />);
    fireEvent.click(await screen.findByRole("button", { name: "ต่อเวลา" }));
    fireEvent.click(screen.getByRole("button", { name: "ยืนยันต่อเวลา" }));

    await act(async () => {
      await Promise.resolve();
    });

    // A refund-looking poll response that lands during the extend must be dropped.
    balanceSatang = 110_000;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(9_000);
    });
    expect(screen.queryByText(/คืนเงินค่าเช่าที่ไม่ได้ใช้/)).toBeNull();

    extendRequest.resolve(response(leases[0]));
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(
      await screen.findByText("ต่อเวลาแล้ว รอเครื่องยืนยันการเปลี่ยนแปลง"),
    ).toBeDefined();
  });

  it("extends a rental through the dialog and keeps the pending ACK visible", async () => {
    const initial = lease();
    leases = [initial];
    fetchSpy.mockImplementation(
      async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url === "/api/my-leases") return listResponse();
        if (url === "/api/my-leases/rental-1/extend") {
          expect(JSON.parse(String(init?.body))).toMatchObject({
            additionalSeconds: 7_200,
            expectedRevision: 1,
          });
          leases = [{ ...initial, revision: 2, pendingRevision: 2 }];
          return response(leases[0]);
        }
        throw new Error(`Unexpected request: ${url}`);
      },
    );

    render(<MyMachines />);
    fireEvent.click(
      await screen.findByRole("button", { name: "ต่อเวลา" }),
    );

    fireEvent.change(
      screen.getByLabelText("ระยะเวลา (ชั่วโมง)"),
      { target: { value: "2" } },
    );
    fireEvent.click(screen.getByRole("button", { name: "ยืนยันต่อเวลา" }));

    expect(
      await screen.findByText("ต่อเวลาแล้ว รอเครื่องยืนยันการเปลี่ยนแปลง"),
    ).toBeDefined();
    expect(
      screen.getAllByText("กำลังรอเครื่องยืนยันการต่อเวลา").length,
    ).toBeGreaterThan(0);
    expect(
      (screen.getByRole("button", { name: "ต่อเวลา" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
  });

  it("applies the cancel response balance without reporting an unexpected refund", async () => {
    const refreshGate = deferred<Response>();
    let listRequests = 0;
    fetchSpy.mockImplementation(
      async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url === "/api/my-leases") {
          listRequests += 1;
          // Keep the post-cancel refresh pending so the confirmation stays mounted.
          return listRequests === 1 ? listResponse() : refreshGate.promise;
        }
        if (url === "/api/my-leases/rental-1/cancel") {
          expect(JSON.parse(String(init?.body))).toMatchObject({
            expectedRevision: 1,
          });
          balanceSatang = 102_667;
          return response({
            lease: { id: "rental-1", state: "cancelled" },
            refundedSatang: 2_667,
            paymentRefundedSatang: 2_667,
            balanceSatang,
          });
        }
        throw new Error(`Unexpected request: ${url}`);
      },
    );

    render(<MyMachines />);
    fireEvent.click(
      await screen.findByRole("button", { name: "ยกเลิกการเช่า" }),
    );
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(
      within(dialog).getByRole("button", { name: "ยืนยันยกเลิก" }),
    );

    expect(await screen.findByText("ยกเลิกแล้ว คืนเงิน ฿26.67")).toBeDefined();
    expect(screen.getByText("฿1,026.67")).toBeDefined();
    // The refund came from this local cancel, so the poll-driven banner stays quiet.
    expect(screen.queryByText(/คืนเงินค่าเช่าที่ไม่ได้ใช้/)).toBeNull();

    // Let the authoritative refresh land: the cancelled card disappears.
    leases = [];
    await act(async () => {
      refreshGate.resolve(
        response({ leases, balanceSatang: 102_667 }),
      );
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(
      await screen.findByText("ยังไม่มีเครื่องที่กำลังเช่า"),
    ).toBeDefined();
    expect(screen.queryByText(/คืนเงินค่าเช่าที่ไม่ได้ใช้/)).toBeNull();
  });

  it("blocks extension when the remaining balance is too low", async () => {
    authState.balanceSatang = 1_000;
    balanceSatang = 1_000;
    leases = [lease({ payment: payment({ hourlyPriceSatang: 40_000 }) })];

    render(<MyMachines />);
    fireEvent.click(
      await screen.findByRole("button", { name: "ต่อเวลา" }),
    );

    expect(screen.getByText("ยอดเงินไม่เพียงพอ")).toBeDefined();
    expect(
      (
        screen.getByRole("button", {
          name: "ยืนยันต่อเวลา",
        }) as HTMLButtonElement
      ).disabled,
    ).toBe(true);
  });

  it("cannot extend a legacy lease without a payment record", async () => {
    leases = [lease({ payment: null })];

    render(<MyMachines />);

    expect(
      await screen.findByText(/ไม่มีข้อมูลการชำระเงิน/),
    ).toBeDefined();
    expect(
      (screen.getByRole("button", { name: "ต่อเวลา" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
  });

  it("disables extension while a previous extension waits for the Agent", async () => {
    leases = [lease({ pendingRevision: 2 })];

    render(<MyMachines />);

    expect(
      await screen.findAllByText("กำลังรอเครื่องยืนยันการต่อเวลา"),
    ).not.toHaveLength(0);
    expect(
      (screen.getByRole("button", { name: "ต่อเวลา" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
  });

  function setVisibility(state: DocumentVisibilityState) {
    Object.defineProperty(document, "visibilityState", {
      configurable: true,
      value: state,
    });
  }
});
