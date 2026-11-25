import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { PcControlVnc, PcControlVncDialog } from "@/components/admin/pc-control-vnc";
import type { PcControlMachine } from "@/lib/pc-control";

const TICKET_URL = "/api/admin/vnc-ticket";
const NOW = "2026-11-25T00:00:00.000Z";

function jsonResponse(body: unknown, ok = true, status = 200) {
  return { ok, status, json: async () => body } as Response;
}

function issued(ticket = "T".repeat(43), expiresInMs = 60_000) {
  return {
    ticket,
    expiresAtUtc: new Date(Date.now() + expiresInMs).toISOString(),
    webSocketUrl: "wss://controller.example:7443/admin/vnc",
  };
}

/** Resolve pending promises inside act so React flushes the fetch continuations. */
async function settle() {
  await act(async () => {
    for (let index = 0; index < 10; index += 1) await Promise.resolve();
  });
}

/** Advance the fake clock/timers (start delay, ticket expiry) and flush React updates. */
async function advance(ms: number) {
  await act(async () => {
    vi.advanceTimersByTime(ms);
    for (let index = 0; index < 10; index += 1) await Promise.resolve();
  });
}

function renderViewer() {
  return render(<PcControlVnc machineId="pc-1" onClose={vi.fn()} />);
}

function frame() {
  return screen.getByTitle("หน้าจอ VNC ของ pc-1") as HTMLIFrameElement;
}

describe("PcControlVnc session lifecycle", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
    vi.setSystemTime(new Date(NOW));
    fetchSpy = vi.fn(async () => jsonResponse(issued()));
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("keeps the connected iframe mounted when the 60-second ticket expires", async () => {
    renderViewer();
    await advance(0);
    expect(fetchSpy).toHaveBeenCalledWith(
      TICKET_URL,
      expect.objectContaining({ method: "POST" }),
    );

    const firstFrame = frame();
    const postMessage = vi.spyOn(firstFrame.contentWindow!, "postMessage");
    fireEvent.load(firstFrame);
    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(postMessage).toHaveBeenCalledWith(
      {
        type: "kr-vnc-connect",
        ticket: "T".repeat(43),
        webSocketUrl: "wss://controller.example:7443/admin/vnc",
      },
      window.location.origin,
    );

    // The Controller keeps a started session for up to one hour: ticket expiry must not unmount
    // the viewer or drop the session.
    await advance(120_000);

    expect(screen.getByTitle("หน้าจอ VNC ของ pc-1")).toBe(firstFrame);
    expect(screen.queryByText(/ตั๋ว VNC หมดอายุ/)).toBeNull();
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(postMessage).toHaveBeenCalledTimes(1);
  });

  it("shows an actionable retry when the ticket expires before the handshake", async () => {
    renderViewer();
    await advance(0);

    // The iframe never loaded, so nothing was sent to the viewer.
    expect(frame()).toBeDefined();
    await advance(61_000);

    expect(screen.queryByTitle("หน้าจอ VNC ของ pc-1")).toBeNull();
    expect(screen.getByRole("status").textContent).toBe(
      "ตั๋ว VNC หมดอายุแล้ว กรุณาขอตั๋วใหม่",
    );

    await advance(0);
    fireEvent.click(screen.getByRole("button", { name: "ลองใหม่" }));
    await settle();

    expect(fetchSpy).toHaveBeenCalledTimes(2);
    const retried = frame();
    const postMessage = vi.spyOn(retried.contentWindow!, "postMessage");
    fireEvent.load(retried);
    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(postMessage).toHaveBeenCalledWith(
      expect.objectContaining({ type: "kr-vnc-connect" }),
      window.location.origin,
    );
    expect(screen.queryByText("ตั๋ว VNC หมดอายุแล้ว กรุณาขอตั๋วใหม่")).toBeNull();
  });

  it("surfaces the server Thai error text instead of a generic English fallback", async () => {
    const message =
      "ยังไม่ได้ตั้งค่า AGENT_CONTROLLER_URL สำหรับ VNC (ต้องเป็น https origin เท่านั้น)";
    fetchSpy.mockResolvedValueOnce(jsonResponse({ error: message }, false, 503));

    renderViewer();
    await advance(0);

    expect(screen.getByRole("alert").textContent).toContain(message);
    expect(screen.queryByTitle("หน้าจอ VNC ของ pc-1")).toBeNull();
    expect(screen.getByRole("button", { name: "ลองใหม่" })).toBeDefined();
  });

  it("retries with a fresh ticket and iframe after a ticket request failure", async () => {
    fetchSpy
      .mockResolvedValueOnce(jsonResponse({ error: "ยังไม่ได้ตั้งค่า AGENT_CONTROLLER_URL สำหรับ VNC (ต้องเป็น https origin เท่านั้น)" }, false, 503))
      .mockResolvedValueOnce(jsonResponse(issued()));

    renderViewer();
    await advance(0);
    expect(screen.getByRole("alert")).toBeDefined();

    fireEvent.click(screen.getByRole("button", { name: "ลองใหม่" }));
    await settle();

    expect(fetchSpy).toHaveBeenCalledTimes(2);
    const retried = frame();
    const postMessage = vi.spyOn(retried.contentWindow!, "postMessage");
    fireEvent.load(retried);
    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("warns with Thai text when the browser cannot reach the server", async () => {
    fetchSpy.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    renderViewer();
    await advance(0);

    expect(screen.getByRole("alert").textContent).toContain(
      "เชื่อมต่อเซิร์ฟเวอร์ไม่สำเร็จ",
    );
  });

  it("obtains a fresh ticket for the viewer when a password retry is requested", async () => {
    fetchSpy
      .mockResolvedValueOnce(jsonResponse(issued("A".repeat(43))))
      .mockResolvedValueOnce(jsonResponse(issued("B".repeat(43))));

    renderViewer();
    await advance(0);
    const mountedFrame = frame();
    const postMessage = vi.spyOn(mountedFrame.contentWindow!, "postMessage");
    fireEvent.load(mountedFrame);
    expect(postMessage).toHaveBeenCalledTimes(1);

    // The viewer keeps the typed password in its own frame; it only asks for replacement
    // ticket material because a consumed VNC ticket cannot be reused.
    fireEvent(
      window,
      new MessageEvent("message", {
        origin: window.location.origin,
        source: mountedFrame.contentWindow,
        data: { type: "kr-vnc-need-ticket" },
      }),
    );
    await settle();

    expect(fetchSpy).toHaveBeenCalledTimes(2);
    expect(screen.getByTitle("หน้าจอ VNC ของ pc-1")).toBe(mountedFrame);
    expect(postMessage).toHaveBeenCalledTimes(2);
    expect(postMessage).toHaveBeenNthCalledWith(
      2,
      {
        type: "kr-vnc-reconnect",
        ticket: "B".repeat(43),
        webSocketUrl: "wss://controller.example:7443/admin/vnc",
      },
      window.location.origin,
    );
  });

  it("tells the viewer when the replacement ticket request fails", async () => {
    fetchSpy
      .mockResolvedValueOnce(jsonResponse(issued("A".repeat(43))))
      .mockRejectedValueOnce(new Error("เครื่องนี้ยังไม่พร้อมสำหรับ VNC"));

    renderViewer();
    await advance(0);
    const mountedFrame = frame();
    const postMessage = vi.spyOn(mountedFrame.contentWindow!, "postMessage");
    fireEvent.load(mountedFrame);
    postMessage.mockClear();

    fireEvent(
      window,
      new MessageEvent("message", {
        origin: window.location.origin,
        source: mountedFrame.contentWindow,
        data: { type: "kr-vnc-need-ticket" },
      }),
    );
    await settle();

    expect(postMessage).toHaveBeenCalledWith(
      { type: "kr-vnc-ticket-error", message: "เครื่องนี้ยังไม่พร้อมสำหรับ VNC" },
      window.location.origin,
    );
    expect(screen.getByRole("status").textContent).toContain("ขอตั๋วใหม่ไม่สำเร็จ");
  });

  it("clears its timers on unmount", async () => {
    const view = renderViewer();
    await advance(0);
    expect(vi.getTimerCount()).toBe(1); // ticket expiry only

    view.unmount();
    expect(vi.getTimerCount()).toBe(0);
  });

  it("ignores viewer messages that do not come from the current iframe", async () => {
    renderViewer();
    await advance(0);
    const mountedFrame = frame();
    const postMessage = vi.spyOn(mountedFrame.contentWindow!, "postMessage");
    fireEvent.load(mountedFrame);
    postMessage.mockClear();

    fireEvent(
      window,
      new MessageEvent("message", {
        origin: window.location.origin,
        source: window,
        data: { type: "kr-vnc-need-ticket" },
      }),
    );
    await settle();

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(postMessage).not.toHaveBeenCalled();
  });

  it("explains an unusable VNC target inside the dialog and links to its settings", async () => {
    const machine = {
      machineId: "pc-9",
      displayName: "PC-09",
      approvalStatus: "approved",
      vncLocalIp: null,
      lastSeenAt: new Date().toISOString(),
    } as unknown as PcControlMachine;

    render(
      <PcControlVncDialog machine={machine} now={Date.now()} onClose={vi.fn()} />,
    );

    expect(screen.getByText(/ยังไม่ได้ตั้งค่า VNC target/)).toBeDefined();
    expect(
      screen
        .getByRole("link", { name: "ไปตั้งค่า VNC target ที่หน้าเครื่องและ Agent" })
        .getAttribute("href"),
    ).toBe("/admin/machines");
  });
});
