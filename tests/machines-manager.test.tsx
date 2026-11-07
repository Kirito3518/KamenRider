import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MachinesManager } from "@/components/admin/machines-manager";

const MACHINES_URL = "/api/admin/machines";
const INSTALL_COMMAND_URL = "/api/admin/agent-install-command";
const ALLOWED_URLS = new Set([MACHINES_URL, INSTALL_COMMAND_URL]);
const TEST_NOW = "2026-11-07T12:00:00.000Z";
const FIRST_COMMAND = "first-test-command";
const FIRST_EXPIRY = "2026-11-07T12:34:56.789Z";
const SECOND_COMMAND = "second-test-command";
const SECOND_EXPIRY = "2026-11-07T13:34:56.789Z";

function jsonResponse(body: unknown) {
  return {
    ok: true,
    json: async () => body,
  } as Response;
}

describe("MachinesManager install command interactions", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  let unexpectedUrls: string[];
  let clipboardWriteText: ReturnType<typeof vi.fn>;
  let clipboardDescriptor: PropertyDescriptor | undefined;
  let machinesResponse: unknown[];

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(TEST_NOW));
    unexpectedUrls = [];
    machinesResponse = [];
    clipboardDescriptor = Object.getOwnPropertyDescriptor(
      navigator,
      "clipboard",
    );
    clipboardWriteText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText: clipboardWriteText },
    });

    let installResponseIndex = 0;
    const installResponses = [
      {
        available: true,
        command: FIRST_COMMAND,
        expiresAtUtc: FIRST_EXPIRY,
      },
      {
        available: true,
        command: SECOND_COMMAND,
        expiresAtUtc: SECOND_EXPIRY,
      },
    ];
    fetchSpy = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === MACHINES_URL) return jsonResponse(machinesResponse);
      if (url === INSTALL_COMMAND_URL) {
        const response = installResponses[installResponseIndex];
        installResponseIndex += 1;
        if (!response) {
          unexpectedUrls.push(url);
          throw new Error(`Unexpected extra install-command request: ${url}`);
        }
        return jsonResponse(response);
      }

      unexpectedUrls.push(url);
      throw new Error(`Unexpected fetch URL: ${url}`);
    });
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    if (clipboardDescriptor) {
      Object.defineProperty(navigator, "clipboard", clipboardDescriptor);
    } else {
      Reflect.deleteProperty(navigator, "clipboard");
    }
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("shows the literal UTC expiry and copies the command before its deadline", async () => {
    render(<MachinesManager />);

    const commandField = await screen.findByLabelText("คำสั่งติดตั้ง Agent");
    expect(commandField).toHaveProperty("value", FIRST_COMMAND);
    expect(screen.getByText(`หมดอายุเมื่อ (UTC): ${FIRST_EXPIRY}`)).toBeDefined();

    const copyButton = screen.getByRole("button", {
      name: "คัดลอกคำสั่ง PowerShell",
    });
    expect((copyButton as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(copyButton);

    expect(await screen.findByText("คัดลอกคำสั่งแล้ว")).toBeDefined();
    expect(clipboardWriteText).toHaveBeenCalledWith(FIRST_COMMAND);
    expectNetworkIsolation(1);
  });

  it("disables copy at the exact expiry deadline after focus", async () => {
    render(<MachinesManager />);

    const copyButton = (await screen.findByRole("button", {
      name: "คัดลอกคำสั่ง PowerShell",
    })) as HTMLButtonElement;
    expect(copyButton.disabled).toBe(false);

    act(() => {
      vi.setSystemTime(new Date(FIRST_EXPIRY));
      window.dispatchEvent(new Event("focus"));
    });

    await waitFor(() => expect(copyButton.disabled).toBe(true));
    expect(
      screen.getByText("คำสั่งติดตั้งหมดอายุแล้ว กรุณาขอคำสั่งใหม่"),
    ).toBeDefined();
    expectNetworkIsolation(1);
  });

  it("requests a replacement only on explicit refresh, not focus or visibility", async () => {
    render(<MachinesManager />);

    const commandField = (await screen.findByLabelText(
      "คำสั่งติดตั้ง Agent",
    )) as HTMLTextAreaElement;
    expect(commandField.value).toBe(FIRST_COMMAND);
    expectNetworkIsolation(1);

    act(() => {
      window.dispatchEvent(new Event("focus"));
      document.dispatchEvent(new Event("visibilitychange"));
    });
    expectNetworkIsolation(1);

    fireEvent.click(
      screen.getByRole("button", { name: "ขอคำสั่งติดตั้งใหม่" }),
    );

    await waitFor(() => expect(commandField.value).toBe(SECOND_COMMAND));
    expect(
      screen.getByText(`หมดอายุเมื่อ (UTC): ${SECOND_EXPIRY}`),
    ).toBeDefined();
    expectNetworkIsolation(2);
  });

  it("shows the localized Admin-only diagnostic checklist and status for each reported check", async () => {
    machinesResponse = [
      {
        machineId: "readiness-pc",
        approvalStatus: "approved",
        displayName: "Readiness PC",
        localIp: "192.168.1.12",
        vncLocalIp: null,
        hourlyPriceMinorUnits: 100,
        moonlightPort: 47989,
        sunshinePort: 47990,
        configRevision: 1,
        appliedConfigRevision: 1,
        readiness: "notReady",
        canRent: false,
        lastSeenAt: null,
        agentVersion: "test",
        activeAdminVncSessions: 0,
        readinessDiagnostics: [
          { checkId: "baseline", status: "passed" },
          { checkId: "bootChallenge", status: "unavailable" },
          { checkId: "restoration", status: "blocked" },
        ],
      },
    ];

    render(<MachinesManager />);

    expect(await screen.findByText("readiness-pc · approved")).toBeDefined();
    expect(screen.getByText("ผลตรวจวินิจฉัยจาก Agent เท่านั้น ไม่ใช่การอนุมัติให้เช่า"))
      .toBeDefined();
    expect(screen.getByText("การตรวจพื้นฐาน")).toBeDefined();
    expect(screen.getByText("ผ่าน")).toBeDefined();
    expect(screen.getByText("การทดสอบยืนยันการบูต")).toBeDefined();
    expect(screen.getByText("ไม่มีระบบตรวจสอบ")).toBeDefined();
    expect(screen.getByText("การตรวจการคืนค่าสภาพเครื่อง")).toBeDefined();
    expect(screen.getByText("ถูกบล็อก")).toBeDefined();
  });

  it("clearly says when the Agent has not reported diagnostics", async () => {
    machinesResponse = [
      {
        machineId: "no-report-pc",
        approvalStatus: "approved",
        displayName: "No report PC",
        localIp: "192.168.1.13",
        vncLocalIp: null,
        hourlyPriceMinorUnits: 100,
        moonlightPort: 47989,
        sunshinePort: 47990,
        configRevision: 1,
        appliedConfigRevision: 1,
        readiness: "notReady",
        canRent: false,
        lastSeenAt: null,
        agentVersion: "old-agent",
        activeAdminVncSessions: 0,
      },
    ];

    render(<MachinesManager />);

    expect(await screen.findByText("no-report-pc · approved")).toBeDefined();
    expect(screen.getByText("Agent ยังไม่รายงานผลตรวจวินิจฉัย"))
      .toBeDefined();
  });

  it("distinguishes an empty diagnostic report from a missing report", async () => {
    machinesResponse = [
      {
        machineId: "empty-report-pc",
        approvalStatus: "approved",
        displayName: "Empty report PC",
        localIp: "192.168.1.14",
        vncLocalIp: null,
        hourlyPriceMinorUnits: 100,
        moonlightPort: 47989,
        sunshinePort: 47990,
        configRevision: 1,
        appliedConfigRevision: 1,
        readiness: "notReady",
        canRent: false,
        lastSeenAt: null,
        agentVersion: "test-agent",
        activeAdminVncSessions: 0,
        readinessDiagnostics: [],
      },
    ];

    render(<MachinesManager />);

    expect(await screen.findByText("empty-report-pc · approved")).toBeDefined();
    expect(screen.getByText("รายงานนี้ไม่มีรายการตรวจวินิจฉัย"))
      .toBeDefined();
  });

  function expectNetworkIsolation(expectedInstallRequests: number) {
    const requestedUrls = fetchSpy.mock.calls.map(([input]) => String(input));
    expect(unexpectedUrls).toEqual([]);
    expect(requestedUrls.every((url) => ALLOWED_URLS.has(url))).toBe(true);
    expect(requestedUrls.filter((url) => url === MACHINES_URL)).toHaveLength(1);
    expect(
      requestedUrls.filter((url) => url === INSTALL_COMMAND_URL),
    ).toHaveLength(expectedInstallRequests);
  }
});
