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
const VNC_TARGET_CHECK_URL = "/api/admin/vnc-target-check";
const ALLOWED_URLS = new Set([MACHINES_URL, INSTALL_COMMAND_URL, VNC_TARGET_CHECK_URL]);
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
      if (url === VNC_TARGET_CHECK_URL) {
        return jsonResponse({ supported: true });
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

  function requestInitialInstallCommand() {
    fireEvent.click(screen.getByRole("button", { name: "ขอคำสั่งติดตั้ง" }));
  }

  it("offers an update on an approved, unready machine when its lease is clear", async () => {
    machinesResponse = [{
      machineId: "update-pc", approvalStatus: "approved", displayName: "Update PC", localIp: "192.168.1.12", vncLocalIp: null,
      hourlyPriceMinorUnits: 100, moonlightPort: 47989, sunshinePort: 47990, configRevision: 1, appliedConfigRevision: 1,
      readiness: "notReady", canRent: false, allocationHeld: false, lastSeenAt: TEST_NOW, agentVersion: "1.0.0", activeAdminVncSessions: 0,
    }];
    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("update-pc · approved");
    await waitFor(() => expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled).toBe(false));
    expectNetworkIsolation(0);
  });

  it("reports a stale recovery conflict truthfully when refreshing machines returns 503", async () => {
    vi.spyOn(crypto, "randomUUID").mockReturnValue("stale-refresh-request");
    const recoveryMachine = {
      machineId: "recovery-pc", approvalStatus: "approved", displayName: "Recovery PC", localIp: "192.168.1.14", vncLocalIp: null,
      hourlyPriceMinorUnits: 100, moonlightPort: 47989, sunshinePort: 47990, configRevision: 1, appliedConfigRevision: 1,
      readiness: "notReady", canRent: false, allocationHeld: false, lastSeenAt: TEST_NOW, agentVersion: "1.0.0", activeAdminVncSessions: 0,
      agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4", agentUpdateTargetVersion: "2.0.0",
    };
    const context = {
      machineId: "recovery-pc", chainId: "chain-1", currentHeadOperationId: "operation-4",
      currentGeneration: 4, currentStatus: "failed", currentTargetVersion: "2.0.0", held: true, allocationHeld: false,
    };
    let machineReads = 0;
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === MACHINES_URL) {
        machineReads++;
        return machineReads === 1
          ? jsonResponse([recoveryMachine])
          : { ok: false, status: 503, json: async () => ({ error: "temporarily unavailable" }) } as Response;
      }
      if (url.endsWith("agent-update-recovery-context")) return jsonResponse({ available: true, context });
      if (url === "/api/admin/agent-releases") return jsonResponse([{ version: "3.0.0", channel: "stable" }]);
      if (url.endsWith("agent-update-recovery-intents") && init?.method === "POST") return {
        ok: false,
        status: 409,
        json: async () => ({
          available: false, pending: false, error: "The maintenance head changed.",
          currentHead: { currentChainId: "chain-2", currentHeadOperationId: "operation-5", currentGeneration: 8 },
        }),
      } as Response;
      throw new Error(`Unexpected request ${url}`);
    });

    render(<MachinesManager actorId="refresh-admin" />);
    await screen.findByText("recovery-pc · approved");
    fireEvent.click(screen.getByRole("button", { name: "ขอ recovery intent" }));
    await screen.findByLabelText("เหตุผล recovery");
    fireEvent.change(screen.getByLabelText("เหตุผล recovery"), { target: { value: "Read current state after the conflict." } });
    fireEvent.change(screen.getByLabelText("เลือก target release สำหรับ recovery-pc"), { target: { value: "3.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้าง recovery intent" }));

    expect(await screen.findByText(/chain chain-2, head operation-5, generation 8/)).toBeDefined();
    expect(screen.getByText(/Refresh the machine data and read the held context before creating another request/)).toBeDefined();
    expect(screen.queryByText(/Latest machine data was refreshed/)).toBeNull();
    expect(machineReads).toBe(2);
    expect(fetchSpy.mock.calls.filter(([input, request]) => String(input).includes("agent-update-recovery-intents") && request?.method === "POST")).toHaveLength(1);
    expect(fetchSpy.mock.calls.some(([input]) => String(input).includes("agent-update-recovery-intents?requestId="))).toBe(false);
  });

  it("keeps the post-command machine snapshot when an older scheduled poll finishes later", async () => {
    const intervalSpy = vi.spyOn(window, "setInterval");
    const original = {
      machineId: "update-pc", approvalStatus: "approved", displayName: "Update PC", localIp: "192.168.1.12", vncLocalIp: null,
      hourlyPriceMinorUnits: 100, moonlightPort: 47989, sunshinePort: 47990, configRevision: 1, appliedConfigRevision: 1,
      readiness: "notReady", canRent: false, allocationHeld: false, lastSeenAt: TEST_NOW, agentVersion: "1.0.0", activeAdminVncSessions: 0,
      agentUpdateStatus: "completed", agentUpdateOperationId: "previous-operation",
    };
    let resolveOldPoll!: (response: Response) => void;
    const oldPoll = new Promise<Response>((resolve) => { resolveOldPoll = resolve; });
    let machineReads = 0;
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      if (String(input) === MACHINES_URL) {
        machineReads++;
        if (machineReads === 2) return oldPoll;
        return jsonResponse([{ ...original, ...(machineReads > 2 ? { agentVersion: "2.0.0", agentUpdateOperationId: "new-operation" } : {}) }]);
      }
      if (String(input) === "/api/admin/agent-releases") return jsonResponse([{ version: "2.0.0", channel: "stable" }]);
      if (String(input) === "/api/admin/machines/update-pc/agent-update-command") return jsonResponse({
        available: true, command: "new update command", operationId: "new-operation", targetVersion: "2.0.0", expiresAtUtc: FIRST_EXPIRY,
      });
      throw new Error(`Unexpected request ${input}`);
    });
    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("update-pc · approved");
    await waitFor(() => expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled).toBe(false));
    const poll = intervalSpy.mock.calls.find(([, delay]) => delay === 5000)![0] as () => void;
    act(() => { poll(); });
    fireEvent.click(screen.getByRole("button", { name: "Update Agent" }));
    await screen.findByRole("option", { name: "2.0.0 · stable" });
    fireEvent.change(screen.getByLabelText("เลือก release สำหรับ update-pc"), { target: { value: "2.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้างคำสั่งอัปเดต" }));
    await screen.findByText(/Agent: 2.0.0/);
    await act(async () => { resolveOldPoll(jsonResponse([original])); });
    expect(screen.getByText(/Agent: 2.0.0/)).toBeDefined();
    expect(screen.queryByText(/Agent: 1.0.0/)).toBeNull();
    expect(screen.getByLabelText("คำสั่งอัปเดต Agent สำหรับ update-pc")).toHaveProperty("value", "new update command");
  });

  it("does not request an install command until the Admin clicks the install button", () => {
    render(<MachinesManager actorId="test-admin" />);

    expect(
      fetchSpy.mock.calls.filter(([input]) => String(input) === INSTALL_COMMAND_URL),
    ).toHaveLength(0);
    expect(
      screen.getByRole("button", { name: "ขอคำสั่งติดตั้ง" }),
    ).toBeDefined();
  });

  it("requests the install command with POST after an explicit click", async () => {
    render(<MachinesManager actorId="test-admin" />);

    fireEvent.click(screen.getByRole("button", { name: "ขอคำสั่งติดตั้ง" }));

    const commandField = await screen.findByLabelText("คำสั่งติดตั้ง Agent");
    expect(commandField).toHaveProperty("value", FIRST_COMMAND);
    const installRequest = fetchSpy.mock.calls.find(
      ([input]) => String(input) === INSTALL_COMMAND_URL,
    );
    expect(installRequest?.[1]?.method).toBe("POST");
  });

  it("shows the literal UTC expiry and copies the command before its deadline", async () => {
    render(<MachinesManager actorId="test-admin" />);
    requestInitialInstallCommand();

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

  it("keeps an explicit install-command request busy until the command arrives", async () => {
    let resolveInitialRequest: ((response: Response) => void) | undefined;
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === MACHINES_URL) return jsonResponse(machinesResponse);
      if (url === INSTALL_COMMAND_URL) {
        return new Promise<Response>((resolve) => {
          resolveInitialRequest = resolve;
        });
      }
      unexpectedUrls.push(url);
      throw new Error(`Unexpected fetch URL: ${url}`);
    });

    render(<MachinesManager actorId="test-admin" />);
    requestInitialInstallCommand();

    const refreshButton = screen.getByRole("button", {
      name: "กำลังขอคำสั่ง…",
    }) as HTMLButtonElement;
    expect(refreshButton.disabled).toBe(true);
    expect(screen.queryByLabelText("คำสั่งติดตั้ง Agent")).toBeNull();

    await act(async () => {
      resolveInitialRequest?.(
        jsonResponse({
          available: true,
          command: FIRST_COMMAND,
          expiresAtUtc: FIRST_EXPIRY,
        }),
      );
    });

    expect(refreshButton.disabled).toBe(false);
    expect(screen.getByLabelText("คำสั่งติดตั้ง Agent")).toHaveProperty(
      "value",
      FIRST_COMMAND,
    );
    expectNetworkIsolation(1);
  });

  it("keeps an explicit install-command refresh busy until the replacement arrives", async () => {
    render(<MachinesManager actorId="test-admin" />);
    requestInitialInstallCommand();
    const commandField = (await screen.findByLabelText(
      "คำสั่งติดตั้ง Agent",
    )) as HTMLTextAreaElement;
    expect(commandField.value).toBe(FIRST_COMMAND);

    let resolveReplacement: ((response: Response) => void) | undefined;
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === MACHINES_URL) return jsonResponse(machinesResponse);
      if (url === INSTALL_COMMAND_URL) {
        return new Promise<Response>((resolve) => {
          resolveReplacement = resolve;
        });
      }
      unexpectedUrls.push(url);
      throw new Error(`Unexpected fetch URL: ${url}`);
    });

    fireEvent.click(
      screen.getByRole("button", { name: "ขอคำสั่งติดตั้งใหม่" }),
    );
    const busyButton = screen.getByRole("button", {
      name: "กำลังขอคำสั่ง…",
    }) as HTMLButtonElement;
    expect(busyButton.disabled).toBe(true);

    await act(async () => {
      resolveReplacement?.(
        jsonResponse({
          available: true,
          command: SECOND_COMMAND,
          expiresAtUtc: SECOND_EXPIRY,
        }),
      );
    });

    expect(commandField.value).toBe(SECOND_COMMAND);
    expect(
      (screen.getByRole("button", {
        name: "ขอคำสั่งติดตั้งใหม่",
      }) as HTMLButtonElement).disabled,
    ).toBe(false);
    expectNetworkIsolation(2);
  });

  it("disables copy at the exact expiry deadline after focus", async () => {
    render(<MachinesManager actorId="test-admin" />);
    requestInitialInstallCommand();

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
    render(<MachinesManager actorId="test-admin" />);
    requestInitialInstallCommand();

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

    render(<MachinesManager actorId="test-admin" />);

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

    render(<MachinesManager actorId="test-admin" />);

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

    render(<MachinesManager actorId="test-admin" />);

    expect(await screen.findByText("empty-report-pc · approved")).toBeDefined();
    expect(screen.getByText("รายงานนี้ไม่มีรายการตรวจวินิจฉัย"))
      .toBeDefined();
  });

  it("verifies the entered private VNC target before saving and clears stale verification", async () => {
    machinesResponse = [
      {
        machineId: "vnc-target-pc",
        approvalStatus: "approved",
        displayName: "VNC target PC",
        localIp: "192.168.1.14",
        vncLocalIp: null,
        hourlyPriceMinorUnits: 100,
        moonlightPort: 47989,
        sunshinePort: 47990,
        configRevision: 1,
        appliedConfigRevision: 1,
        readiness: "notReady",
        canRent: false,
        lastSeenAt: "2026-11-08T12:00:00.000Z",
        agentVersion: "test-agent",
        activeAdminVncSessions: 0,
      },
    ];

    render(<MachinesManager actorId="test-admin" />);

    const targetInput = (await screen.findByLabelText(
      "IP เป้าหมาย VNC (ตรวจสอบกับเครื่องจริงก่อนบันทึก)",
    )) as HTMLInputElement;
    fireEvent.change(targetInput, { target: { value: " 192.168.1.18 " } });
    fireEvent.click(screen.getByRole("button", { name: "ตรวจสอบ VNC" }));

    const successStatus = `พบ VNC ที่รองรับบน 192.168.1.18:5900 · ตรวจเมื่อ ${new Date(TEST_NOW).toLocaleString()} ตามเวลาท้องถิ่น (ยังไม่ได้ยืนยันรหัสผ่าน)`;
    expect(await screen.findByText(successStatus))
      .toBeDefined();
    const checkRequest = fetchSpy.mock.calls.find(
      ([input]) => String(input) === VNC_TARGET_CHECK_URL,
    );
    expect(checkRequest).toBeDefined();
    expect(JSON.parse(String(checkRequest?.[1]?.body))).toEqual({
      machineId: "vnc-target-pc",
      targetAddress: "192.168.1.18",
    });
    expect(
      fetchSpy.mock.calls.some(([input]) => String(input).startsWith("/api/admin/machines/")),
    ).toBe(false);

    fireEvent.change(targetInput, { target: { value: "192.168.1.19" } });
    expect(screen.queryByText(successStatus))
      .toBeNull();
    expectNetworkIsolation(0);
  });

  it("does not probe a public VNC target", async () => {
    machinesResponse = [
      {
        machineId: "unsafe-vnc-pc",
        approvalStatus: "approved",
        displayName: "Unsafe VNC PC",
        localIp: "192.168.1.14",
        vncLocalIp: null,
        hourlyPriceMinorUnits: 100,
        moonlightPort: 47989,
        sunshinePort: 47990,
        configRevision: 1,
        appliedConfigRevision: 1,
        readiness: "notReady",
        canRent: false,
        lastSeenAt: "2026-11-08T12:00:00.000Z",
        agentVersion: "test-agent",
        activeAdminVncSessions: 0,
      },
    ];

    render(<MachinesManager actorId="test-admin" />);

    const targetInput = (await screen.findByLabelText(
      "IP เป้าหมาย VNC (ตรวจสอบกับเครื่องจริงก่อนบันทึก)",
    )) as HTMLInputElement;
    fireEvent.change(targetInput, { target: { value: "203.0.113.8" } });
    fireEvent.click(screen.getByRole("button", { name: "ตรวจสอบ VNC" }));

    expect(await screen.findByText(/กรอก IPv4 LAN ส่วนตัวหรือ IPv6 ULA/))
      .toBeDefined();
    expect(
      fetchSpy.mock.calls.some(([input]) => String(input) === VNC_TARGET_CHECK_URL),
    ).toBe(false);
    expectNetworkIsolation(0);
  });

  it("shows Agent-reported hostname, CPU, GPU, RAM and applied Sunshine revision", async () => {
    machinesResponse = [
      {
        machineId: "spec-pc",
        approvalStatus: "approved",
        displayName: "DESKTOP-SPEC",
        hostname: "DESKTOP-SPEC",
        cpu: "AMD Ryzen 7 7800X3D",
        gpu: "NVIDIA GeForce RTX 4080",
        ramBytes: 34_359_738_368,
        appliedSunshineCredentialRevision: 4,
        localIp: "192.168.1.20",
        vncLocalIp: null,
        hourlyPriceMinorUnits: 100,
        moonlightPort: 47989,
        sunshinePort: 47990,
        configRevision: 1,
        appliedConfigRevision: 1,
        readiness: "notReady",
        canRent: false,
        lastSeenAt: null,
        agentVersion: "1.0.0",
        activeAdminVncSessions: 0,
      },
      {
        machineId: "no-spec-pc",
        approvalStatus: "approved",
        displayName: "No spec PC",
        localIp: "192.168.1.21",
        vncLocalIp: null,
        hourlyPriceMinorUnits: 100,
        moonlightPort: 47989,
        sunshinePort: 47990,
        configRevision: 1,
        appliedConfigRevision: 1,
        readiness: "notReady",
        canRent: false,
        lastSeenAt: null,
        agentVersion: "1.0.0",
        activeAdminVncSessions: 0,
      },
    ];

    render(<MachinesManager actorId="test-admin" />);

    expect(await screen.findByText("spec-pc · approved")).toBeDefined();
    const paragraphWith = (fragment: string) =>
      screen.getByText(
        (_text, element) =>
          element?.tagName === "P" &&
          element.textContent?.includes(fragment) === true,
      );
    expect(paragraphWith("Hostname: DESKTOP-SPEC")).toBeDefined();
    expect(paragraphWith("CPU: AMD Ryzen 7 7800X3D")).toBeDefined();
    expect(paragraphWith("GPU: NVIDIA GeForce RTX 4080")).toBeDefined();
    expect(paragraphWith("RAM: 32 GB")).toBeDefined();
    expect(
      paragraphWith("Sunshine credential revision ที่ Agent ใช้: 4"),
    ).toBeDefined();
    expect(paragraphWith("Hostname: — · CPU: — · GPU: — · RAM: —")).toBeDefined();
    expect(
      paragraphWith("Sunshine credential revision ที่ Agent ใช้: 0"),
    ).toBeDefined();
    expectNetworkIsolation(0);
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
