import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MyMachines } from "@/components/my-machines/my-machines";

vi.mock("@/components/ui/layout/Navbar", () => ({ Navbar: () => null }));
vi.mock("@/components/ui/layout/Footer", () => ({ Footer: () => null }));
vi.mock("@/lib/auth-client", () => ({
  authClient: {
    useSession: () => ({
      data: {
        user: {
          id: "renter-1",
          name: "Renter",
          email: "renter@example.com",
          image: null,
          balanceSatang: 100_000,
          role: "user",
        },
      },
      isPending: false,
      refetch: vi.fn(async () => ({ data: null })),
    }),
  },
}));

const HEARTBEAT_AT = "2026-11-08T03:04:05.000Z";
const LEASES = [
  {
    id: "rental-1",
    machineId: "pc-one",
    state: "active",
    revision: 1,
    pendingRevision: null,
    expiresAtUtc: "2026-11-08T04:00:00.000Z",
    connection: { address: null, stale: false },
    agent: { reachability: "reachable", lastSeenAt: HEARTBEAT_AT },
    payment: {
      hourlyPriceSatang: 40_000,
      durationSeconds: 3600,
      extensionSeconds: 0,
      paidSatang: 40_000,
      refundedSatang: 0,
      extendable: true,
    },
  },
];

function response(body: unknown) {
  return new Response(JSON.stringify(body), {
    status: 200,
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

describe("MyMachines lease refresh", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  let visibilityDescriptor: PropertyDescriptor | undefined;

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
    visibilityDescriptor = Object.getOwnPropertyDescriptor(
      document,
      "visibilityState",
    );
    setVisibility("visible");
    fetchSpy = vi.fn(async () => response(LEASES));
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

  it("shows the Controller reachability and last heartbeat on each lease", async () => {
    render(<MyMachines />);

    expect(await screen.findByText("pc-one")).toBeDefined();
    expect(screen.getByText("Agent ติดต่อได้")).toBeDefined();
    expect(screen.getByText(`Heartbeat ล่าสุด: ${HEARTBEAT_AT}`)).toBeDefined();
  });

  it("renders the Agent-reported machine display name when provided", async () => {
    fetchSpy.mockImplementation(async () =>
      response([{ ...LEASES[0], machineDisplayName: "DESKTOP-RENTER" }]),
    );

    render(<MyMachines />);

    expect(await screen.findByText("DESKTOP-RENTER")).toBeDefined();
  });

  it("shows the verified connect address for the renter's active lease", async () => {
    fetchSpy.mockImplementation(async () =>
      response([
        {
          ...LEASES[0],
          connection: { address: "203.0.113.7:47989", stale: false },
        },
      ]),
    );

    render(<MyMachines />);

    expect(
      await screen.findByText("เชื่อมต่อ: 203.0.113.7:47989"),
    ).toBeDefined();
    expect(
      screen.queryByText(
        "ข้อมูลที่อยู่เชื่อมต่ออาจล้าสมัย — ตรวจสอบกับผู้ดูแลก่อนใช้งาน",
      ),
    ).toBeNull();
  });

  it("flags a stale connect address", async () => {
    fetchSpy.mockImplementation(async () =>
      response([
        {
          ...LEASES[0],
          connection: { address: "203.0.113.7:47989", stale: true },
        },
      ]),
    );

    render(<MyMachines />);

    expect(
      await screen.findByText("เชื่อมต่อ: 203.0.113.7:47989"),
    ).toBeDefined();
    expect(
      screen.getByText(
        "ข้อมูลที่อยู่เชื่อมต่ออาจล้าสมัย — ตรวจสอบกับผู้ดูแลก่อนใช้งาน",
      ),
    ).toBeDefined();
  });

  it("does not show a connect address unless the lease is active", async () => {
    fetchSpy.mockImplementation(async () =>
      response([
        {
          ...LEASES[0],
          state: "ending",
          connection: { address: "203.0.113.9:47989", stale: false },
        },
      ]),
    );

    render(<MyMachines />);

    expect(await screen.findByText("pc-one")).toBeDefined();
    expect(screen.queryByText(/203\.0\.113\.9/)).toBeNull();
    expect(screen.queryByText(/^เชื่อมต่อ:/)).toBeNull();
  });

  it("explains when an active lease has no verified connect address", async () => {
    render(<MyMachines />);

    expect(
      await screen.findByText(
        "เชื่อมต่อ: ยังไม่มีที่อยู่สาธารณะที่ตรวจสอบแล้ว กรุณาติดต่อผู้ดูแล",
      ),
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

    firstRequest.resolve(response(LEASES));
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(15_000);
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

  it("keeps an extension pending when an older in-flight poll resolves", async () => {
    const stalePoll = deferred<Response>();
    const otherLease = {
      ...LEASES[0],
      id: "rental-2",
      machineId: "pc-two",
      state: "ending",
      revision: 4,
      pendingRevision: null,
      agent: {
        reachability: "unreachable",
        lastSeenAt: "2026-11-08T02:00:00.000Z",
      },
    };
    let leaseListRequests = 0;
    fetchSpy.mockImplementation((input: RequestInfo | URL) => {
      const path = String(input);
      if (path === "/api/my-leases") {
        leaseListRequests += 1;
        if (leaseListRequests === 1)
          return Promise.resolve(response([...LEASES, otherLease]));
        if (leaseListRequests === 2) return stalePoll.promise;
        return Promise.resolve(
          response([{ ...LEASES[0], pendingRevision: 2 }, otherLease]),
        );
      }
      if (path === "/api/my-leases/rental-1/extend")
        return Promise.resolve(
          response({ ...LEASES[0], pendingRevision: 2 }),
        );
      throw new Error(`Unexpected request: ${path}`);
    });

    render(<MyMachines />);
    expect(await screen.findByText("pc-one")).toBeDefined();
    expect(await screen.findByText("pc-two")).toBeDefined();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(15_000);
    });
    expect(leaseListRequests).toBe(2);

    fireEvent.click(screen.getByRole("button", { name: "ต่อเวลา" }));
    expect(
      await screen.findByText("ต่อเวลาแล้ว รอเครื่องยืนยันการเปลี่ยนแปลง"),
    ).toBeDefined();
    const showsPendingRevision = (_text: string, element: Element | null) =>
      element?.tagName === "P" &&
      element.textContent?.includes("Revision 1") === true &&
      element.textContent.includes("รอ ACK 2");
    expect(
      screen.getByText(showsPendingRevision),
    ).toBeDefined();

    stalePoll.resolve(response([...LEASES, otherLease]));
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });
    await waitFor(() => expect(leaseListRequests).toBe(3));

    expect(
      screen.getByText(showsPendingRevision),
    ).toBeDefined();
    expect(
      (screen.getByRole("button", { name: "ต่อเวลา" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
    expect(screen.getByText("Agent ติดต่อได้")).toBeDefined();
    expect(screen.getByText(`Heartbeat ล่าสุด: ${HEARTBEAT_AT}`)).toBeDefined();
    expect(screen.getByText("Agent ติดต่อไม่ได้")).toBeDefined();
    expect(
      screen.getByText("Heartbeat ล่าสุด: 2026-11-08T02:00:00.000Z"),
    ).toBeDefined();
  });

  it("removes leases omitted by an authoritative same-version refresh", async () => {
    const otherLease = {
      ...LEASES[0],
      id: "rental-2",
      machineId: "pc-two",
      state: "ending",
      agent: {
        reachability: "unreachable",
        lastSeenAt: "2026-11-08T02:00:00.000Z",
      },
    };
    let leaseListRequests = 0;
    fetchSpy.mockImplementation((input: RequestInfo | URL) => {
      if (String(input) !== "/api/my-leases")
        throw new Error(`Unexpected request: ${String(input)}`);
      leaseListRequests += 1;
      return Promise.resolve(
        response(leaseListRequests === 1 ? [...LEASES, otherLease] : LEASES),
      );
    });

    render(<MyMachines />);
    expect(await screen.findByText("pc-two")).toBeDefined();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "รีเฟรช" }));
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(leaseListRequests).toBe(2);
    expect(screen.queryByText("pc-two")).toBeNull();
    expect(screen.getByText("pc-one")).toBeDefined();
  });

  it("clears a pending revision when an authoritative refresh cancels it", async () => {
    const pendingLease = { ...LEASES[0], pendingRevision: 2 };
    let leaseListRequests = 0;
    fetchSpy.mockImplementation((input: RequestInfo | URL) => {
      if (String(input) !== "/api/my-leases")
        throw new Error(`Unexpected request: ${String(input)}`);
      leaseListRequests += 1;
      return Promise.resolve(
        response(leaseListRequests === 1 ? [pendingLease] : LEASES),
      );
    });

    render(<MyMachines />);
    expect(await screen.findByText("pc-one")).toBeDefined();
    const showsPendingAck = (_text: string, element: Element | null) =>
      element?.tagName === "P" &&
      element.textContent?.includes("รอ ACK 2") === true;
    expect(screen.getByText(showsPendingAck)).toBeDefined();
    const extendButton = screen.getByRole("button", {
      name: "ต่อเวลา",
    }) as HTMLButtonElement;
    expect(extendButton.disabled).toBe(true);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "รีเฟรช" }));
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(leaseListRequests).toBe(2);
    expect(screen.queryByText(showsPendingAck)).toBeNull();
    expect(extendButton.disabled).toBe(false);
  });

  it("does not let a delayed extension response undo a newer poll snapshot", async () => {
    const delayedExtension = deferred<Response>();
    const acknowledgedLease = {
      ...LEASES[0],
      revision: 2,
      pendingRevision: null,
    };
    let leaseListRequests = 0;
    const requestOrder: string[] = [];
    fetchSpy.mockImplementation((input: RequestInfo | URL) => {
      const path = String(input);
      requestOrder.push(path);
      if (path === "/api/my-leases") {
        leaseListRequests += 1;
        return Promise.resolve(
          response(leaseListRequests === 1 ? LEASES : [acknowledgedLease]),
        );
      }
      if (path === "/api/my-leases/rental-1/extend")
        return delayedExtension.promise;
      throw new Error(`Unexpected request: ${path}`);
    });

    render(<MyMachines />);
    expect(await screen.findByText("pc-one")).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "ต่อเวลา" }));
    expect(
      (screen.getByRole("button", { name: "ต่อเวลา" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "รีเฟรช" }));
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(requestOrder).toEqual([
      "/api/my-leases",
      "/api/my-leases/rental-1/extend",
      "/api/my-leases",
    ]);
    const showsPendingRevision = (_text: string, element: Element | null) =>
      element?.tagName === "P" &&
      element.textContent?.includes("Revision 1") === true &&
      !element.textContent.includes("รอ ACK");
    expect(screen.getByText(showsPendingRevision)).toBeDefined();

    delayedExtension.resolve(
      response({ ...LEASES[0], pendingRevision: 2 }),
    );
    expect(
      await screen.findByText("ต่อเวลาแล้ว รอเครื่องยืนยันการเปลี่ยนแปลง"),
    ).toBeDefined();
    await waitFor(() => expect(leaseListRequests).toBe(3));
    const showsAcknowledgedRevision = (
      _text: string,
      element: Element | null,
    ) =>
      element?.tagName === "P" &&
      element.textContent?.includes("Revision 2") === true &&
      !element.textContent.includes("รอ ACK");
    expect(screen.getByText(showsAcknowledgedRevision)).toBeDefined();
    expect(
      (screen.getByRole("button", { name: "ต่อเวลา" }) as HTMLButtonElement)
        .disabled,
    ).toBe(false);
  });

  function setVisibility(state: DocumentVisibilityState) {
    Object.defineProperty(document, "visibilityState", {
      configurable: true,
      value: state,
    });
  }
});
