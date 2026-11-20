import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { PcControl } from "@/components/admin/pc-control";

const SNAPSHOT_URL = "/api/admin/pc-control";
const VNC_TICKET_URL = "/api/admin/vnc-ticket";
const POWER_OFF_URL = "/api/admin/pc-control/pc-1/power-off";
const LEASE_TIME_URL = "/api/admin/pc-control/pc-1/lease-time";
const LEASE_CANCEL_URL = "/api/admin/pc-control/pc-1/lease-cancel";
const PAIRING_URL = "/api/admin/pc-control/pc-1/pairing";
const WAKE_URL = "/api/admin/machines/pc-1/wake";
const SUNSHINE_RESTART_URL = "/api/admin/pc-control/pc-1/sunshine";
const SUNSHINE_STATUS_URL = "/api/admin/pc-control/pc-1/sunshine/sunshine-1";

function jsonResponse(body: unknown, ok = true) {
  return { ok, status: ok ? 200 : 409, json: async () => body } as Response;
}

const machine = {
  machineId: "pc-1",
  approvalStatus: "approved",
  displayName: "PC-01",
  localIp: "192.0.2.10",
  vncLocalIp: "192.0.2.10",
  hourlyPriceMinorUnits: 500,
  moonlightPort: 47989,
  sunshinePort: 47990,
  configRevision: 1,
  appliedConfigRevision: 1,
  readiness: "ready",
  canRent: false,
  allocationHeld: true,
  lastSeenAt: new Date().toISOString(),
  agentVersion: "1.2.3",
  publicKeyFingerprint: "A".repeat(64),
  activeAdminVncSessions: 0,
  readinessDiagnostics: [],
  agentUpdateOperationId: null,
  agentUpdateTargetVersion: null,
  agentUpdateStatus: null,
  lease: {
    rentalId: "rental-1",
    userId: "user-1",
    userName: "Alice",
    userEmail: "alice@example.com",
    state: "active",
    revision: 2,
    pendingRevision: null,
    startsAtUtc: new Date(Date.now() - 60_000).toISOString(),
    expiresAtUtc: new Date(Date.now() + 3_600_000).toISOString(),
    remainingSeconds: 3600,
  },
};

const snapshot = () => ({
  serverTimeUtc: new Date().toISOString(),
  machines: [machine],
});

describe("PcControl", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  let calls: { url: string; init?: RequestInit }[];

  beforeEach(() => {
    calls = [];
    fetchSpy = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, init });
      if (url === SNAPSHOT_URL) return jsonResponse(snapshot());
      if (url === VNC_TICKET_URL)
        return jsonResponse({
          ticket: "T".repeat(43),
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
          webSocketUrl: "wss://localhost/admin/vnc",
        });
      if (url === PAIRING_URL && !init?.method)
        return jsonResponse({
          rentalId: "rental-1",
          leaseRevision: 2,
          validUntilUtc: new Date(Date.now() + 20_000).toISOString(),
          pairingIds: ["00000000000000000000000000000001"],
        });
      if (url === WAKE_URL || url === POWER_OFF_URL || url === LEASE_TIME_URL || url === LEASE_CANCEL_URL || url === PAIRING_URL)
        return jsonResponse({ status: "queued" });
      if (url === SUNSHINE_RESTART_URL)
        return jsonResponse({
          commandId: "sunshine-1",
          action: "restart",
          status: "requested",
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
        });
      if (url === SUNSHINE_STATUS_URL)
        return jsonResponse({
          commandId: "sunshine-1",
          action: "restart",
          status: "applied",
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
        });
      throw new Error(`Unexpected fetch URL: ${url}`);
    });
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("renders one card per machine and only requests VNC when the admin opens the screen", async () => {
    render(<PcControl />);
    await screen.findByText("PC-01");
    expect(screen.getByText("Alice")).toBeDefined();
    expect(screen.getByText(/01:00:00|00:59:5/)).toBeDefined();
    expect(calls.some((call) => call.url === VNC_TICKET_URL)).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "เปิดจอ" }));
    await waitFor(() => expect(calls.some((call) => call.url === VNC_TICKET_URL)).toBe(true));
    expect(await screen.findByTitle("หน้าจอ VNC ของ pc-1")).toBeDefined();

    fireEvent.click(screen.getByRole("button", { name: "ปิดจอ" }));
    await waitFor(() => expect(screen.queryByTitle("หน้าจอ VNC ของ pc-1")).toBeNull());
  });

  it("forces power off through the confirmation dialog", async () => {
    render(<PcControl />);
    await screen.findByText("PC-01");
    fireEvent.click(screen.getByRole("button", { name: "ปิดเครื่อง" }));
    expect(screen.getByText("ยืนยันปิดเครื่อง")).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "ปิดเครื่องทันที" }));

    await waitFor(() => expect(calls.some((call) => call.url === POWER_OFF_URL)).toBe(true));
    const request = calls.find((call) => call.url === POWER_OFF_URL)!;
    const body = JSON.parse(String(request.init?.body)) as { requestId: string };
    expect(body.requestId.length).toBeGreaterThan(10);
  });

  it("adjusts time with the preset dialog and cancels the rental", async () => {
    render(<PcControl />);
    await screen.findByText("PC-01");

    fireEvent.click(screen.getByRole("button", { name: "กำหนดเวลา" }));
    expect(screen.getByText("กำหนดเวลา PC-01")).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "ยืนยันเวลา" }));
    await waitFor(() => expect(calls.some((call) => call.url === LEASE_TIME_URL)).toBe(true));
    const adjust = calls.find((call) => call.url === LEASE_TIME_URL)!;
    expect(JSON.parse(String(adjust.init?.body))).toMatchObject({ deltaSeconds: 3600, expectedRevision: 2 });

    fireEvent.click(screen.getByRole("button", { name: "ยกเลิกการเช่า" }));
    expect(screen.getByText("ยืนยันยกเลิกการเช่า")).toBeDefined();
    const confirmButtons = screen.getAllByRole("button", { name: "ยกเลิกการเช่า" });
    fireEvent.click(confirmButtons[confirmButtons.length - 1]);
    await waitFor(() => expect(calls.some((call) => call.url === LEASE_CANCEL_URL)).toBe(true));
  });

  it("opens the admin pairing dialog for the current rental", async () => {
    render(<PcControl />);
    await screen.findByText("PC-01");
    fireEvent.click(screen.getByRole("button", { name: "เชื่อมต่อพิน" }));
    expect(await screen.findByText("เชื่อมต่อพิน PC-01")).toBeDefined();
    await waitFor(() => expect(calls.some((call) => call.url === PAIRING_URL && !call.init?.method)).toBe(true));
    expect(screen.getByLabelText("PIN 4 หลัก")).toBeDefined();
  });

  it("requests wake when the machine is offline", async () => {
    const offlineMachine = {
      ...machine,
      lastSeenAt: new Date(Date.now() - 60_000).toISOString(),
      lease: null,
      canRent: false,
      allocationHeld: false,
    };
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, init });
      if (url === SNAPSHOT_URL) return jsonResponse({ serverTimeUtc: new Date().toISOString(), machines: [offlineMachine] });
      if (url === WAKE_URL) return jsonResponse({ status: "requested" });
      throw new Error(`Unexpected fetch URL: ${url}`);
    });

    render(<PcControl />);
    await screen.findByText("PC-01");
    fireEvent.click(screen.getByRole("button", { name: "เปิดเครื่อง" }));
    await waitFor(() => expect(calls.some((call) => call.url === WAKE_URL)).toBe(true));
  });

  it("queues Sunshine maintenance commands through the confirm dialogs and surfaces the result", async () => {
    render(<PcControl />);
    await screen.findByText("PC-01");

    fireEvent.click(screen.getByRole("button", { name: "รีสตาร์ท Sunshine" }));
    expect(screen.getByText("ยืนยันรีสตาร์ท Sunshine")).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "รีสตาร์ท Sunshine" }));

    await waitFor(() => expect(calls.some((call) => call.url === SUNSHINE_RESTART_URL)).toBe(true));
    const restart = calls.find((call) => call.url === SUNSHINE_RESTART_URL)!;
    expect(restart.init?.method).toBe("POST");
    // The machine has an active rental, so the command must be forced.
    expect(JSON.parse(String(restart.init?.body))).toMatchObject({ action: "restart", force: true });
    expect(await screen.findByText("รีสตาร์ท Sunshine PC-01 สำเร็จแล้ว")).toBeDefined();

    fireEvent.click(screen.getByRole("button", { name: "ยกเลิกการจับคู่ Sunshine" }));
    expect(screen.getByText("ยืนยันยกเลิกการจับคู่ Sunshine")).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "ยกเลิกการจับคู่" }));

    await waitFor(() => expect(calls.some((call) => call.url === SUNSHINE_RESTART_URL && JSON.parse(String(call.init?.body)).action === "unpair")).toBe(true));
    const unpair = calls.find((call) => call.url === SUNSHINE_RESTART_URL && JSON.parse(String(call.init?.body)).action === "unpair")!;
    expect(JSON.parse(String(unpair.init?.body))).toMatchObject({ action: "unpair", force: true });
  });
});
