import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AdminVncViewerButton } from "@/components/admin/admin-vnc-viewer";

const NOW = "2026-11-12T05:00:00.000Z";
const VNC_URL = "/api/admin/vnc-ticket";

function jsonResponse(body: unknown, ok = true) {
  return { ok, json: async () => body } as Response;
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function ticket(ticketValue: string, expiresAtUtc: string) {
  return {
    ticket: ticketValue,
    expiresAtUtc,
    webSocketUrl: "wss://controller.example/vnc",
  };
}

describe("Admin VNC viewer session lifecycle", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(NOW));
    fetchSpy = vi.fn(async () => jsonResponse({ error: "Unexpected VNC request" }, false));
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("ignores a ticket response from a closed session after the viewer is reopened", async () => {
    const requestA = deferred<Response>();
    const requestB = deferred<Response>();
    fetchSpy.mockReturnValueOnce(requestA.promise).mockReturnValueOnce(requestB.promise);
    render(<AdminVncViewerButton machineId="pc-1" />);

    fireEvent.click(screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }));
    fireEvent.click(screen.getByRole("button", { name: "ปิดการเชื่อมต่อ" }));

    await act(async () => {
      requestA.resolve(jsonResponse(ticket("ticket-A", "2026-11-12T05:10:00.000Z")));
      await requestA.promise;
    });

    expect(screen.queryByRole("dialog")).toBeNull();
    expect((screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }));

    expect(fetchSpy).toHaveBeenCalledTimes(2);
    expect(fetchSpy).toHaveBeenNthCalledWith(
      2,
      VNC_URL,
      expect.objectContaining({ method: "POST", body: JSON.stringify({ machineId: "pc-1" }) }),
    );
    expect(screen.queryByTitle("หน้าจอ VNC ของ pc-1")).toBeNull();
    expect(screen.getByRole("status").textContent).toBe("กำลังขอสิทธิ์ดูหน้าจอ…");
    expect((screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }) as HTMLButtonElement).disabled).toBe(true);

    await act(async () => {
      requestB.resolve(jsonResponse(ticket("ticket-B", "2026-11-12T05:10:00.000Z")));
      await requestB.promise;
    });
    const frame = (await screen.findByTitle("หน้าจอ VNC ของ pc-1")) as HTMLIFrameElement;
    const postMessage = vi.spyOn(frame.contentWindow!, "postMessage");
    fireEvent.load(frame);

    await waitFor(() => expect(postMessage).toHaveBeenCalledTimes(1));
    expect(postMessage).toHaveBeenCalledWith(
      {
        type: "kr-vnc-connect",
        ticket: "ticket-B",
        webSocketUrl: "wss://controller.example/vnc",
      },
      window.location.origin,
    );
  });

  it("keeps the reopened session busy while its ticket request is pending", async () => {
    const requestA = deferred<Response>();
    const requestB = deferred<Response>();
    fetchSpy.mockReturnValueOnce(requestA.promise).mockReturnValueOnce(requestB.promise);
    render(<AdminVncViewerButton machineId="pc-5" />);

    fireEvent.click(screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }));
    fireEvent.click(screen.getByRole("button", { name: "ปิดการเชื่อมต่อ" }));
    fireEvent.click(screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }));

    expect(fetchSpy).toHaveBeenCalledTimes(2);
    expect(screen.getByRole("status").textContent).toBe("กำลังขอสิทธิ์ดูหน้าจอ…");
    expect((screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }) as HTMLButtonElement).disabled).toBe(true);

    await act(async () => {
      requestA.resolve(jsonResponse(ticket("ticket-stale-A", "2026-11-12T05:10:00.000Z")));
      await requestA.promise;
    });

    expect(screen.queryByTitle("หน้าจอ VNC ของ pc-5")).toBeNull();
    expect(screen.getByRole("status").textContent).toBe("กำลังขอสิทธิ์ดูหน้าจอ…");
    expect((screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole("button", { name: "ขอ ticket ใหม่" })).toBeNull();

    await act(async () => {
      requestB.resolve(jsonResponse(ticket("ticket-current-B", "2026-11-12T05:10:00.000Z")));
      await requestB.promise;
    });
    const frame = (await screen.findByTitle("หน้าจอ VNC ของ pc-5")) as HTMLIFrameElement;
    const postMessage = vi.spyOn(frame.contentWindow!, "postMessage");
    fireEvent.load(frame);

    await waitFor(() => expect(postMessage).toHaveBeenCalledTimes(1));
    expect(postMessage).toHaveBeenCalledWith(
      {
        type: "kr-vnc-connect",
        ticket: "ticket-current-B",
        webSocketUrl: "wss://controller.example/vnc",
      },
      window.location.origin,
    );
  });

  it("does not let a stale Escape-closed request error or finally reset the new request", async () => {
    const requestA = deferred<Response>();
    const requestB = deferred<Response>();
    fetchSpy.mockReturnValueOnce(requestA.promise).mockReturnValueOnce(requestB.promise);
    render(<AdminVncViewerButton machineId="pc-2" />);

    fireEvent.click(screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }));
    fireEvent.keyDown(window, { key: "Escape" });
    fireEvent.click(screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }));

    await act(async () => {
      requestA.reject(new Error("stale session failed"));
      await requestA.promise.catch(() => undefined);
    });

    expect(screen.getByRole("status").textContent).toBe("กำลังขอสิทธิ์ดูหน้าจอ…");
    expect((screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole("button", { name: "ขอ ticket ใหม่" })).toBeNull();

    await act(async () => {
      requestB.resolve(jsonResponse(ticket("ticket-B", "2026-11-12T05:10:00.000Z")));
      await requestB.promise;
    });
    expect(screen.getByTitle("หน้าจอ VNC ของ pc-2")).toBeDefined();
  });

  it("rejects an expired ticket before connect and retries with a fresh iframe and ticket", async () => {
    const requestA = deferred<Response>();
    const requestB = deferred<Response>();
    fetchSpy.mockReturnValueOnce(requestA.promise).mockReturnValueOnce(requestB.promise);
    render(<AdminVncViewerButton machineId="pc-3" />);

    fireEvent.click(screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }));
    await act(async () => {
      requestA.resolve(jsonResponse(ticket("ticket-expiring", "2026-11-12T05:00:10.000Z")));
      await requestA.promise;
    });
    const firstFrame = (await screen.findByTitle("หน้าจอ VNC ของ pc-3")) as HTMLIFrameElement;
    const firstPostMessage = vi.spyOn(firstFrame.contentWindow!, "postMessage");

    act(() => {
      vi.setSystemTime(new Date("2026-11-12T05:00:11.000Z"));
      fireEvent.load(firstFrame);
    });

    expect(firstPostMessage).not.toHaveBeenCalled();
    expect(screen.getByRole("status").textContent).toMatch(/หมดอายุ/);
    expect(screen.getByRole("button", { name: "ขอ ticket ใหม่" })).toBeDefined();

    fireEvent.click(screen.getByRole("button", { name: "ขอ ticket ใหม่" }));
    expect(screen.queryByTitle("หน้าจอ VNC ของ pc-3")).toBeNull();
    await act(async () => {
      requestB.resolve(jsonResponse(ticket("ticket-fresh", "2026-11-12T05:01:00.000Z")));
      await requestB.promise;
    });

    const secondFrame = (await screen.findByTitle("หน้าจอ VNC ของ pc-3")) as HTMLIFrameElement;
    expect(secondFrame).not.toBe(firstFrame);
    const secondPostMessage = vi.spyOn(secondFrame.contentWindow!, "postMessage");
    fireEvent.load(secondFrame);
    await waitFor(() => expect(secondPostMessage).toHaveBeenCalledTimes(1));
    expect(secondPostMessage).toHaveBeenCalledWith(
      {
        type: "kr-vnc-connect",
        ticket: "ticket-fresh",
        webSocketUrl: "wss://controller.example/vnc",
      },
      window.location.origin,
    );
    expect(firstPostMessage).not.toHaveBeenCalled();
    expect(fetchSpy).toHaveBeenCalledTimes(2);
  });

  it("accepts viewer status messages only from the current frame and same origin", async () => {
    fetchSpy.mockResolvedValueOnce(jsonResponse(ticket("ticket-current", "2026-11-12T05:10:00.000Z")));
    render(<AdminVncViewerButton machineId="pc-4" />);

    fireEvent.click(screen.getByRole("button", { name: "ดูหน้าจอผ่าน VNC" }));
    const frame = (await screen.findByTitle("หน้าจอ VNC ของ pc-4")) as HTMLIFrameElement;
    fireEvent.load(frame);
    fireEvent(
      window,
      new MessageEvent("message", {
        origin: window.location.origin,
        source: frame.contentWindow,
        data: { type: "kr-vnc-ready" },
      }),
    );
    expect(screen.getByRole("status").textContent).toBe("กำลังเชื่อมต่อกับเครื่องลูก…");

    fireEvent(
      window,
      new MessageEvent("message", {
        origin: "https://untrusted.example",
        source: frame.contentWindow,
        data: { type: "kr-vnc-status", message: "untrusted origin" },
      }),
    );
    expect(screen.getByRole("status").textContent).toBe("กำลังเชื่อมต่อกับเครื่องลูก…");

    fireEvent(
      window,
      new MessageEvent("message", {
        origin: window.location.origin,
        source: window,
        data: { type: "kr-vnc-status", message: "untrusted frame" },
      }),
    );
    expect(screen.getByRole("status").textContent).toBe("กำลังเชื่อมต่อกับเครื่องลูก…");

    fireEvent(
      window,
      new MessageEvent("message", {
        origin: window.location.origin,
        source: frame.contentWindow,
        data: { type: "kr-vnc-status", message: "Connected" },
      }),
    );
    expect(screen.getByRole("status").textContent).toBe("Connected");
  });
});
