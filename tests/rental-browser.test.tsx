import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { RentalMachine } from "@/lib/rental-machines";

const mocks = vi.hoisted(() => ({
  toast: { success: vi.fn(), error: vi.fn() },
  session: null as null | {
    user: {
      id: string;
      name: string;
      email: string;
      image: null;
      balanceSatang: number;
      role: string;
    };
  },
}));

vi.mock("@/components/ui/layout/Navbar", () => ({ Navbar: () => null }));
vi.mock("@/components/ui/layout/Footer", () => ({ Footer: () => null }));
vi.mock("@/hooks/use-landing-motion", () => ({
  useLandingMotion: () => ({ current: null }),
}));
vi.mock("react-hot-toast", () => ({ default: mocks.toast }));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn() }),
}));
vi.mock("@/lib/auth-client", () => ({
  authClient: {
    useSession: () => ({
      data: mocks.session,
      isPending: false,
      refetch: vi.fn(async () => ({ data: null })),
    }),
  },
}));

import { RentalBrowser } from "@/components/rent/rental-browser";

type EventListener = (event: Event) => void;

class MockEventSource {
  static instances: MockEventSource[] = [];

  static reset() {
    MockEventSource.instances = [];
  }

  readonly url: string;
  closed = false;
  private readonly listeners = new Map<string, Set<EventListener>>();

  constructor(url: string) {
    this.url = url;
    MockEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: EventListener) {
    const listeners = this.listeners.get(type) ?? new Set<EventListener>();
    listeners.add(listener);
    this.listeners.set(type, listeners);
  }

  removeEventListener(type: string, listener: EventListener) {
    this.listeners.get(type)?.delete(listener);
  }

  close() {
    this.closed = true;
  }

  emit(type: string, data?: unknown) {
    const event =
      type === "open" || type === "error"
        ? new Event(type)
        : new MessageEvent(type, { data: JSON.stringify(data) });
    for (const listener of [...(this.listeners.get(type) ?? [])]) listener(event);
  }

  static latest() {
    const source = MockEventSource.instances.at(-1);
    if (!source) throw new Error("EventSource was never opened");
    return source;
  }
}

const BASE_MACHINE: RentalMachine = {
  id: "pc-1",
  name: "PC ONE",
  cpu: "AMD Ryzen 7 7800X3D",
  gpu: "NVIDIA GeForce RTX 4080",
  ram: 32,
  ramBytes: 34_359_738_368,
  storage: "—",
  priceSatang: 4_000,
  status: "available",
  game: "",
};

function machine(overrides: Partial<RentalMachine> = {}): RentalMachine {
  return { ...BASE_MACHINE, ...overrides };
}

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function machinesEvent(machines: RentalMachine[], version: string) {
  return { machines, version, serverTimeUtc: new Date().toISOString() };
}

describe("RentalBrowser realtime machine status", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    mocks.toast.success.mockReset();
    mocks.toast.error.mockReset();
    mocks.session = {
      user: {
        id: "renter-1",
        name: "Renter",
        email: "renter@example.com",
        image: null,
        balanceSatang: 100_000,
        role: "user",
      },
    };
    fetchSpy = vi.fn(async () => jsonResponse([machine()]));
    vi.stubGlobal("fetch", fetchSpy);
    MockEventSource.reset();
    vi.stubGlobal("EventSource", MockEventSource);
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    MockEventSource.reset();
  });

  it("hydrates the grid from /api/machines and opens the SSE stream", async () => {
    render(<RentalBrowser />);

    expect(await screen.findByTestId("machine-grid")).toBeDefined();
    expect(screen.getByText("PC ONE")).toBeDefined();
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(fetchSpy.mock.calls[0][0]).toBe("/api/machines");
    expect(fetchSpy.mock.calls[0][1]).toMatchObject({ cache: "no-store" });

    const source = MockEventSource.latest();
    expect(source.url).toBe("/api/machines/stream");
    expect(source.closed).toBe(false);
  });

  it("applies live `machines` events and shows the realtime status line", async () => {
    render(<RentalBrowser />);
    await screen.findByText("PC ONE");
    const source = MockEventSource.latest();

    act(() => {
      source.emit("open");
      source.emit("status", { live: true, serverTimeUtc: new Date().toISOString() });
      source.emit(
        "machines",
        machinesEvent([machine({ status: "busy" })], "version-2"),
      );
    });

    expect(
      await screen.findByRole("button", { name: "PC ONE กำลังใช้งาน" }),
    ).toBeDefined();
    expect(screen.getByText(/เชื่อมต่อแบบเรียลไทม์/)).toBeDefined();

    // A replayed snapshot with the same version must not be re-applied.
    act(() => {
      source.emit(
        "machines",
        machinesEvent([machine({ status: "available" })], "version-2"),
      );
    });
    expect(screen.getByRole("button", { name: "PC ONE กำลังใช้งาน" })).toBeDefined();
  });

  it("keeps the dialog open with an alert when the selected machine becomes busy", async () => {
    render(<RentalBrowser />);
    await screen.findByText("PC ONE");
    const source = MockEventSource.latest();

    fireEvent.click(
      screen.getByRole("button", { name: "เลือกเช่าเครื่อง PC ONE" }),
    );
    expect(await screen.findByText("เช่าเครื่อง PC ONE")).toBeDefined();

    act(() => {
      source.emit(
        "machines",
        machinesEvent([machine({ status: "busy" })], "version-2"),
      );
    });

    expect(
      await screen.findByText("เครื่องนี้ไม่พร้อมให้เช่าแล้ว กรุณาเลือกเครื่องอื่น"),
    ).toBeDefined();
    expect(screen.getByText("เช่าเครื่อง PC ONE")).toBeDefined();
    const submit = screen.getByRole("button", { name: "ยืนยันการเช่า" });
    expect((submit as HTMLButtonElement).disabled).toBe(true);
    expect(mocks.toast.error).not.toHaveBeenCalled();
  });

  it("closes the dialog and warns when the selected machine disappears", async () => {
    fetchSpy.mockImplementation(async () =>
      jsonResponse([machine(), machine({ id: "pc-2", name: "PC TWO" })]),
    );
    render(<RentalBrowser />);
    await screen.findByText("PC ONE");
    const source = MockEventSource.latest();

    fireEvent.click(
      screen.getByRole("button", { name: "เลือกเช่าเครื่อง PC ONE" }),
    );
    expect(await screen.findByText("เช่าเครื่อง PC ONE")).toBeDefined();

    act(() => {
      source.emit(
        "machines",
        machinesEvent([machine({ id: "pc-2", name: "PC TWO" })], "version-2"),
      );
    });

    await waitFor(() =>
      expect(mocks.toast.error).toHaveBeenCalledWith(
        "เครื่องที่เลือกไม่พร้อมให้เช่าแล้ว กรุณาเลือกเครื่องอื่น",
      ),
    );
    await waitFor(() => expect(screen.queryByText("เช่าเครื่อง PC ONE")).toBeNull());
  });

  it("falls back to visible 3s polling when EventSource is unavailable", async () => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
    vi.stubGlobal("EventSource", undefined);
    render(<RentalBrowser />);

    await screen.findByText("PC ONE");
    expect(fetchSpy).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_000);
    });
    expect(fetchSpy).toHaveBeenCalledTimes(2);
    expect(screen.getByText(/โหมดสำรอง/)).toBeDefined();
  });

  it("keeps the previous grid while a fallback poll fails (stale-while-error)", async () => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
    vi.stubGlobal("EventSource", undefined);
    let calls = 0;
    fetchSpy.mockImplementation(async () => {
      calls += 1;
      if (calls === 1) return jsonResponse([machine()]);
      throw new Error("offline");
    });
    render(<RentalBrowser />);

    await screen.findByText("PC ONE");
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_000);
    });

    expect(
      await screen.findByText("ติดต่อระบบเครื่องไม่ได้ กรุณาลองใหม่"),
    ).toBeDefined();
    expect(screen.getByText("PC ONE")).toBeDefined();
  });

  it("clears every timer, listener and EventSource on unmount", async () => {
    vi.useFakeTimers();
    fetchSpy.mockImplementation(
      () => new Promise<Response>(() => {
        /* never settles: the test only cares about teardown */
      }),
    );
    const view = render(<RentalBrowser />);
    const source = MockEventSource.latest();
    expect(vi.getTimerCount()).toBeGreaterThan(0);

    view.unmount();

    expect(source.closed).toBe(true);
    // No app-owned timer survives: a cleared stall interval plus no polling or
    // reconnect timer means advancing time neither refetches nor leaves timers.
    const callsAfterUnmount = fetchSpy.mock.calls.length;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetchSpy.mock.calls.length).toBe(callsAfterUnmount);
    expect(vi.getTimerCount()).toBe(0);
  });
});
