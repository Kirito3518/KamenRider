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
import { MachinesManager } from "@/components/admin/machines-manager";
import type { Machine } from "@/lib/machine-contracts";

const MACHINES_URL = "/api/admin/machines";
const INSTALL_COMMAND_URL = "/api/admin/agent-install-command";
const VNC_TARGET_CHECK_URL = "/api/admin/vnc-target-check";
const AGENT_RELEASES_URL = "/api/admin/agent-releases";
const TEST_NOW = "2026-11-07T12:00:00.000Z";
const FIRST_COMMAND = "first-test-command";
const FIRST_EXPIRY = "2026-11-07T12:34:56.789Z";
const SECOND_COMMAND = "second-test-command";
const SECOND_EXPIRY = "2026-11-07T13:34:56.789Z";
const FINGERPRINT = "A".repeat(64);
/** The machines dashboard must never touch the PC Control surface. */
const PC_CONTROL_PATTERN =
  /\/api\/admin\/pc-control|\/api\/admin\/vnc-ticket|\/machines\/[^/]+\/wake|\/power-off|\/lease-time|\/lease-cancel|\/pairing/;

type InstallCommandResponse = {
  available: boolean;
  command?: string;
  oneLiner?: string;
  expiresAtUtc?: string;
  reason?: string;
};

const baseMachine: Omit<Machine, "machineId"> = {
  approvalStatus: "approved",
  displayName: "PC",
  hostname: null,
  cpu: null,
  gpu: null,
  ramBytes: 0,
  appliedSunshineCredentialRevision: 0,
  localIp: "192.168.1.10",
  vncLocalIp: null,
  hourlyPriceMinorUnits: 100,
  moonlightPort: 47989,
  sunshinePort: 47990,
  configRevision: 1,
  appliedConfigRevision: 1,
  readiness: "notReady",
  canRent: false,
  allocationHeld: false,
  lastSeenAt: TEST_NOW,
  agentVersion: "1.0.0",
  activeAdminVncSessions: 0,
  readinessDiagnostics: null,
};

function machineFixture(overrides: Partial<Machine> & Pick<Machine, "machineId">): Machine {
  return { ...baseMachine, ...overrides } as Machine;
}

function jsonResponse(body: unknown, ok = true, status = ok ? 200 : 500) {
  return { ok, status, json: async () => body } as Response;
}

function expectNoPcControlCalls(urls: string[]) {
  expect(urls.filter((url) => PC_CONTROL_PATTERN.test(url))).toEqual([]);
}

describe("MachinesManager dashboard", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  let unexpectedUrls: string[];
  let clipboardWriteText: ReturnType<typeof vi.fn>;
  let clipboardDescriptor: PropertyDescriptor | undefined;
  let machinesResponse: Machine[];
  let installResponses: InstallCommandResponse[];

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(TEST_NOW));
    unexpectedUrls = [];
    machinesResponse = [];
    clipboardDescriptor = Object.getOwnPropertyDescriptor(navigator, "clipboard");
    clipboardWriteText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText: clipboardWriteText },
    });
    installResponses = [
      { available: true, command: FIRST_COMMAND, oneLiner: `${FIRST_COMMAND}-one`, expiresAtUtc: FIRST_EXPIRY },
      { available: true, command: SECOND_COMMAND, oneLiner: `${SECOND_COMMAND}-one`, expiresAtUtc: SECOND_EXPIRY },
    ];
    fetchSpy = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === MACHINES_URL) return jsonResponse(machinesResponse);
      if (url === INSTALL_COMMAND_URL) {
        const response = installResponses.shift();
        if (!response) {
          unexpectedUrls.push(url);
          throw new Error(`Unexpected extra install-command request: ${url}`);
        }
        return jsonResponse(response);
      }
      if (url === VNC_TARGET_CHECK_URL) return jsonResponse({ supported: true });
      if (url === AGENT_RELEASES_URL) return jsonResponse([]);
      unexpectedUrls.push(url);
      throw new Error(`Unexpected fetch URL: ${url}`);
    });
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    expectNoPcControlCalls(fetchSpy.mock.calls.map(([input]) => String(input)));
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

  async function openSettings(machineId: string) {
    fireEvent.click(await screen.findByRole("button", { name: `ตั้งค่าเครื่อง ${machineId}` }));
    return screen.findByRole("dialog");
  }

  async function activateTab(name: string) {
    fireEvent.click(screen.getByRole("tab", { name }));
  }

  async function openInstallDialog() {
    fireEvent.click(screen.getByRole("button", { name: "ติดตั้ง Agent" }));
    return screen.findByRole("dialog");
  }

  function expectNetworkIsolation(expectedInstallRequests: number) {
    const requestedUrls = fetchSpy.mock.calls.map(([input]) => String(input));
    expect(unexpectedUrls).toEqual([]);
    expect(requestedUrls.filter((url) => url === MACHINES_URL)).toHaveLength(1);
    expect(requestedUrls.filter((url) => url === INSTALL_COMMAND_URL)).toHaveLength(expectedInstallRequests);
    expectNoPcControlCalls(requestedUrls);
  }

  it("opens Agent settings for an approved, unready machine when its lease is clear", async () => {
    machinesResponse = [machineFixture({ machineId: "update-pc", displayName: "Update PC" })];
    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("update-pc");
    await openSettings("update-pc");
    await activateTab("Agent");
    const updateButton = await screen.findByRole("button", { name: "Update Agent" });
    await waitFor(() => expect((updateButton as HTMLButtonElement).disabled).toBe(false));
    expectNetworkIsolation(0);
  });

  it("reports a stale recovery conflict truthfully when refreshing machines returns 503", async () => {
    vi.spyOn(crypto, "randomUUID").mockReturnValue("stale-refresh-request");
    const recoveryMachine = machineFixture({
      machineId: "recovery-pc",
      displayName: "Recovery PC",
      localIp: "192.168.1.14",
      allocationHeld: false,
      agentUpdateStatus: "failed",
      agentUpdateOperationId: "operation-4",
      agentUpdateTargetVersion: "2.0.0",
    });
    const context = {
      machineId: "recovery-pc",
      chainId: "chain-1",
      currentHeadOperationId: "operation-4",
      currentGeneration: 4,
      currentStatus: "failed",
      currentTargetVersion: "2.0.0",
      held: true,
      allocationHeld: false,
    };
    let machineReads = 0;
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === MACHINES_URL) {
        machineReads++;
        return machineReads === 1
          ? jsonResponse([recoveryMachine])
          : (jsonResponse({ error: "temporarily unavailable" }, false, 503) as Response);
      }
      if (url.endsWith("agent-update-recovery-context")) return jsonResponse({ available: true, context });
      if (url === AGENT_RELEASES_URL) return jsonResponse([{ version: "3.0.0", channel: "stable" }]);
      if (url.endsWith("agent-update-recovery-intents") && init?.method === "POST") {
        return jsonResponse(
          {
            available: false,
            pending: false,
            error: "The maintenance head changed.",
            currentHead: { currentChainId: "chain-2", currentHeadOperationId: "operation-5", currentGeneration: 8 },
          },
          false,
          409,
        );
      }
      unexpectedUrls.push(url);
      throw new Error(`Unexpected request ${url}`);
    });

    render(<MachinesManager actorId="refresh-admin" />);
    await screen.findByText("recovery-pc");
    await openSettings("recovery-pc");
    await activateTab("Agent");
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
    const original = machineFixture({
      machineId: "update-pc",
      displayName: "Update PC",
      agentUpdateStatus: "completed",
      agentUpdateOperationId: "previous-operation",
    });
    let resolveOldPoll!: (response: Response) => void;
    const oldPoll = new Promise<Response>((resolve) => { resolveOldPoll = resolve; });
    let machineReads = 0;
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === MACHINES_URL) {
        machineReads++;
        if (machineReads === 2) return oldPoll;
        return jsonResponse([
          {
            ...original,
            ...(machineReads > 2 ? { agentVersion: "2.0.0", agentUpdateOperationId: "new-operation" } : {}),
          },
        ]);
      }
      if (url === AGENT_RELEASES_URL) return jsonResponse([{ version: "2.0.0", channel: "stable" }]);
      if (url === "/api/admin/machines/update-pc/agent-update-command") {
        return jsonResponse({
          available: true,
          command: "new update command",
          operationId: "new-operation",
          targetVersion: "2.0.0",
          expiresAtUtc: FIRST_EXPIRY,
        });
      }
      unexpectedUrls.push(url);
      throw new Error(`Unexpected request ${url}`);
    });
    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("update-pc");
    await openSettings("update-pc");
    await activateTab("Agent");
    await waitFor(() => expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled).toBe(false));
    const poll = intervalSpy.mock.calls.find(([, delay]) => delay === 5000)![0] as () => void;
    act(() => { poll(); });
    fireEvent.click(screen.getByRole("button", { name: "Update Agent" }));
    await screen.findByRole("option", { name: "2.0.0 · stable" });
    fireEvent.change(screen.getByLabelText("เลือก release สำหรับ update-pc"), { target: { value: "2.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้างคำสั่งอัปเดต" }));
    await screen.findByText(/เวอร์ชัน Agent: 2\.0\.0/);
    await act(async () => { resolveOldPoll(jsonResponse([original])); });
    expect(screen.getByText(/เวอร์ชัน Agent: 2\.0\.0/)).toBeDefined();
    expect(screen.queryByText(/เวอร์ชัน Agent: 1\.0\.0/)).toBeNull();
    expect(screen.getByLabelText("คำสั่งอัปเดต Agent สำหรับ update-pc")).toHaveProperty("value", "new update command");
  });

  it("does not request an install command until the Admin clicks the install button", async () => {
    render(<MachinesManager actorId="test-admin" />);

    await openInstallDialog();
    expect(fetchSpy.mock.calls.filter(([input]) => String(input) === INSTALL_COMMAND_URL)).toHaveLength(0);
    expect(screen.getByRole("button", { name: "ขอคำสั่งติดตั้ง" })).toBeDefined();
  });

  it("requests the install command with POST after an explicit click", async () => {
    render(<MachinesManager actorId="test-admin" />);
    await openInstallDialog();

    fireEvent.click(screen.getByRole("button", { name: "ขอคำสั่งติดตั้ง" }));

    const commandField = await screen.findByLabelText("คำสั่งติดตั้ง Agent");
    expect(commandField).toHaveProperty("value", FIRST_COMMAND);
    const installRequest = fetchSpy.mock.calls.find(([input]) => String(input) === INSTALL_COMMAND_URL);
    expect(installRequest?.[1]?.method).toBe("POST");
  });

  it("shows the literal UTC expiry and copies the command before its deadline", async () => {
    render(<MachinesManager actorId="test-admin" />);
    await openInstallDialog();
    fireEvent.click(screen.getByRole("button", { name: "ขอคำสั่งติดตั้ง" }));

    const commandField = await screen.findByLabelText("คำสั่งติดตั้ง Agent");
    expect(commandField).toHaveProperty("value", FIRST_COMMAND);
    expect(screen.getByText(`หมดอายุเมื่อ (UTC): ${FIRST_EXPIRY}`)).toBeDefined();

    const copyButton = screen.getByRole("button", { name: "คัดลอกคำสั่ง PowerShell" });
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
        return new Promise<Response>((resolve) => { resolveInitialRequest = resolve; });
      }
      unexpectedUrls.push(url);
      throw new Error(`Unexpected fetch URL: ${url}`);
    });

    render(<MachinesManager actorId="test-admin" />);
    await openInstallDialog();
    fireEvent.click(screen.getByRole("button", { name: "ขอคำสั่งติดตั้ง" }));

    const refreshButton = screen.getByRole("button", { name: /กำลังขอคำสั่ง…/ }) as HTMLButtonElement;
    expect(refreshButton.disabled).toBe(true);
    expect(screen.queryByLabelText("คำสั่งติดตั้ง Agent")).toBeNull();

    await act(async () => {
      resolveInitialRequest?.(
        jsonResponse({ available: true, command: FIRST_COMMAND, oneLiner: `${FIRST_COMMAND}-one`, expiresAtUtc: FIRST_EXPIRY }),
      );
    });

    expect(screen.getByRole("button", { name: "ขอคำสั่งติดตั้งใหม่" })).toHaveProperty("disabled", false);
    expect(screen.getByLabelText("คำสั่งติดตั้ง Agent")).toHaveProperty("value", FIRST_COMMAND);
    expectNetworkIsolation(1);
  });

  it("keeps an explicit install-command refresh busy until the replacement arrives", async () => {
    render(<MachinesManager actorId="test-admin" />);
    await openInstallDialog();
    fireEvent.click(screen.getByRole("button", { name: "ขอคำสั่งติดตั้ง" }));
    const commandField = (await screen.findByLabelText("คำสั่งติดตั้ง Agent")) as HTMLTextAreaElement;
    expect(commandField.value).toBe(FIRST_COMMAND);

    let resolveReplacement: ((response: Response) => void) | undefined;
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === MACHINES_URL) return jsonResponse(machinesResponse);
      if (url === INSTALL_COMMAND_URL) {
        return new Promise<Response>((resolve) => { resolveReplacement = resolve; });
      }
      unexpectedUrls.push(url);
      throw new Error(`Unexpected fetch URL: ${url}`);
    });

    fireEvent.click(screen.getByRole("button", { name: "ขอคำสั่งติดตั้งใหม่" }));
    const busyButton = screen.getByRole("button", { name: /กำลังขอคำสั่ง…/ }) as HTMLButtonElement;
    expect(busyButton.disabled).toBe(true);

    await act(async () => {
      resolveReplacement?.(
        jsonResponse({ available: true, command: SECOND_COMMAND, oneLiner: `${SECOND_COMMAND}-one`, expiresAtUtc: SECOND_EXPIRY }),
      );
    });

    expect(commandField.value).toBe(SECOND_COMMAND);
    expect((screen.getByRole("button", { name: "ขอคำสั่งติดตั้งใหม่" }) as HTMLButtonElement).disabled).toBe(false);
    expectNetworkIsolation(2);
  });

  it("disables copy at the exact expiry deadline after focus", async () => {
    render(<MachinesManager actorId="test-admin" />);
    await openInstallDialog();
    fireEvent.click(screen.getByRole("button", { name: "ขอคำสั่งติดตั้ง" }));

    const copyButton = (await screen.findByRole("button", { name: "คัดลอกคำสั่ง PowerShell" })) as HTMLButtonElement;
    expect(copyButton.disabled).toBe(false);

    act(() => {
      vi.setSystemTime(new Date(FIRST_EXPIRY));
      window.dispatchEvent(new Event("focus"));
    });

    await waitFor(() => expect(copyButton.disabled).toBe(true));
    expect(screen.getByText("คำสั่งติดตั้งหมดอายุแล้ว กรุณาขอคำสั่งใหม่")).toBeDefined();
    expectNetworkIsolation(1);
  });

  it("requests a replacement only on explicit refresh, not focus or visibility", async () => {
    render(<MachinesManager actorId="test-admin" />);
    await openInstallDialog();
    fireEvent.click(screen.getByRole("button", { name: "ขอคำสั่งติดตั้ง" }));

    const commandField = (await screen.findByLabelText("คำสั่งติดตั้ง Agent")) as HTMLTextAreaElement;
    expect(commandField.value).toBe(FIRST_COMMAND);
    expectNetworkIsolation(1);

    act(() => {
      window.dispatchEvent(new Event("focus"));
      document.dispatchEvent(new Event("visibilitychange"));
    });
    expectNetworkIsolation(1);

    fireEvent.click(screen.getByRole("button", { name: "ขอคำสั่งติดตั้งใหม่" }));

    await waitFor(() => expect(commandField.value).toBe(SECOND_COMMAND));
    expect(screen.getByText(`หมดอายุเมื่อ (UTC): ${SECOND_EXPIRY}`)).toBeDefined();
    expectNetworkIsolation(2);
  });

  it("shows the localized Admin-only diagnostic checklist and status for each reported check", async () => {
    machinesResponse = [
      machineFixture({
        machineId: "readiness-pc",
        displayName: "Readiness PC",
        readinessDiagnostics: [
          { checkId: "baseline", status: "passed" },
          { checkId: "bootChallenge", status: "unavailable" },
          { checkId: "restoration", status: "blocked" },
        ],
      }),
    ];

    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("readiness-pc");
    await openSettings("readiness-pc");
    await activateTab("ผลตรวจ");

    expect(screen.getByText("ผลตรวจวินิจฉัยจาก Agent เท่านั้น ไม่ใช่การอนุมัติให้เช่า")).toBeDefined();
    expect(screen.getByText("การตรวจพื้นฐาน")).toBeDefined();
    expect(screen.getByText("ผ่าน")).toBeDefined();
    expect(screen.getByText("การทดสอบยืนยันการบูต")).toBeDefined();
    expect(screen.getByText("ไม่มีระบบตรวจสอบ")).toBeDefined();
    expect(screen.getByText("การตรวจการคืนค่าสภาพเครื่อง")).toBeDefined();
    expect(screen.getByText("ถูกบล็อก")).toBeDefined();
  });

  it("clearly says when the Agent has not reported diagnostics", async () => {
    machinesResponse = [machineFixture({ machineId: "no-report-pc", displayName: "No report PC", agentVersion: "old-agent" })];

    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("no-report-pc");
    await openSettings("no-report-pc");
    await activateTab("ผลตรวจ");

    expect(screen.getByText("Agent ยังไม่รายงานผลตรวจวินิจฉัย")).toBeDefined();
  });

  it("distinguishes an empty diagnostic report from a missing report", async () => {
    machinesResponse = [machineFixture({ machineId: "empty-report-pc", displayName: "Empty report PC", readinessDiagnostics: [] })];

    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("empty-report-pc");
    await openSettings("empty-report-pc");
    await activateTab("ผลตรวจ");

    expect(screen.getByText("รายงานนี้ไม่มีรายการตรวจวินิจฉัย")).toBeDefined();
  });

  it("verifies the entered private VNC target before saving and clears stale verification", async () => {
    machinesResponse = [
      machineFixture({ machineId: "vnc-target-pc", displayName: "VNC target PC", lastSeenAt: "2026-11-08T12:00:00.000Z" }),
    ];

    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("vnc-target-pc");
    await openSettings("vnc-target-pc");
    await activateTab("เครือข่าย");

    const targetInput = (await screen.findByLabelText(
      "IP เป้าหมาย VNC",
    )) as HTMLInputElement;
    fireEvent.change(targetInput, { target: { value: " 192.168.1.18 " } });
    fireEvent.click(screen.getByRole("button", { name: "ตรวจสอบ VNC" }));

    const successStatus = `พบ VNC ที่รองรับบน 192.168.1.18:5900 · ตรวจเมื่อ ${new Date(TEST_NOW).toLocaleString()} ตามเวลาท้องถิ่น (ยังไม่ได้ยืนยันรหัสผ่าน)`;
    expect(await screen.findByText(successStatus)).toBeDefined();
    const checkRequest = fetchSpy.mock.calls.find(([input]) => String(input) === VNC_TARGET_CHECK_URL);
    expect(checkRequest).toBeDefined();
    expect(JSON.parse(String(checkRequest?.[1]?.body))).toEqual({
      machineId: "vnc-target-pc",
      targetAddress: "192.168.1.18",
    });
    expect(fetchSpy.mock.calls.some(([input]) => String(input).startsWith("/api/admin/machines/"))).toBe(false);

    fireEvent.change(targetInput, { target: { value: "192.168.1.19" } });
    expect(screen.queryByText(successStatus)).toBeNull();
    expectNetworkIsolation(0);
  });

  it("does not probe a public VNC target", async () => {
    machinesResponse = [machineFixture({ machineId: "unsafe-vnc-pc", displayName: "Unsafe VNC PC" })];

    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("unsafe-vnc-pc");
    await openSettings("unsafe-vnc-pc");
    await activateTab("เครือข่าย");

    const targetInput = (await screen.findByLabelText(
      "IP เป้าหมาย VNC",
    )) as HTMLInputElement;
    fireEvent.change(targetInput, { target: { value: "203.0.113.8" } });
    fireEvent.click(screen.getByRole("button", { name: "ตรวจสอบ VNC" }));

    expect(await screen.findByText(/กรอก IPv4 LAN ส่วนตัวหรือ IPv6 ULA/)).toBeDefined();
    expect(fetchSpy.mock.calls.some(([input]) => String(input) === VNC_TARGET_CHECK_URL)).toBe(false);
    expectNetworkIsolation(0);
  });

  it("shows Agent-reported specs and applied Sunshine revision in the table and settings dialog", async () => {
    machinesResponse = [
      machineFixture({
        machineId: "spec-pc",
        displayName: "DESKTOP-SPEC",
        hostname: "DESKTOP-SPEC",
        cpu: "AMD Ryzen 7 7800X3D",
        gpu: "NVIDIA GeForce RTX 4080",
        ramBytes: 34_359_738_368,
        appliedSunshineCredentialRevision: 4,
      }),
      machineFixture({ machineId: "no-spec-pc", displayName: "No spec PC" }),
    ];

    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("spec-pc");
    expect(screen.getByText("CPU: AMD Ryzen 7 7800X3D")).toBeDefined();
    expect(screen.getByText("GPU: NVIDIA GeForce RTX 4080")).toBeDefined();
    expect(screen.getByText("RAM: 32 GB")).toBeDefined();
    expect(screen.getByText("CPU: —")).toBeDefined();

    await openSettings("spec-pc");
    expect(screen.getByText("32 GB")).toBeDefined();
    await activateTab("Agent");
    expect(screen.getByText(/เวอร์ชัน Agent: 1\.0\.0/)).toBeDefined();
    const sunshineRow = screen.getByText("Sunshine credential revision ที่ Agent ใช้").closest("div");
    expect(sunshineRow?.textContent).toContain("4");

    fireEvent.click(screen.getByRole("button", { name: "ปิด" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    await openSettings("no-spec-pc");
    await activateTab("Agent");
    const noSunshineRow = screen.getByText("Sunshine credential revision ที่ Agent ใช้").closest("div");
    expect(noSunshineRow?.textContent).toContain("0");
    expectNetworkIsolation(0);
  });

  it("keeps the pending list collapsible and approves after setting the machine up", async () => {
    const pendingMachine = machineFixture({
      machineId: "permission-pc",
      displayName: "Permission PC",
      approvalStatus: "pending",
      publicKeyFingerprint: FINGERPRINT,
    });
    let approved = false;
    let patchBody: unknown = null;
    let approveBody: unknown = null;
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === MACHINES_URL) {
        return jsonResponse([approved ? { ...pendingMachine, approvalStatus: "approved" } : pendingMachine]);
      }
      if (url === "/api/admin/machines/permission-pc" && init?.method === "PATCH") {
        patchBody = JSON.parse(String(init.body));
        return jsonResponse(pendingMachine);
      }
      if (url === "/api/admin/machines/permission-pc/approve" && init?.method === "POST") {
        approved = true;
        approveBody = JSON.parse(String(init.body));
        return jsonResponse({ status: "approved" });
      }
      unexpectedUrls.push(url);
      throw new Error(`Unexpected request ${url}`);
    });

    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("permission-pc");
    const trigger = screen.getByRole("button", { name: "เครื่องที่รออนุมัติ 1 เครื่อง" });
    expect(trigger.getAttribute("aria-expanded")).toBe("true");
    fireEvent.click(trigger);
    expect(trigger.getAttribute("aria-expanded")).toBe("false");
    fireEvent.click(trigger);
    expect(trigger.getAttribute("aria-expanded")).toBe("true");

    fireEvent.click(screen.getByRole("button", { name: "อนุมัติเครื่อง permission-pc" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("ตั้งค่าและอนุมัติเครื่อง Permission PC")).toBeDefined();
    expect(within(dialog).queryByLabelText(/fingerprint/i)).toBeNull();
    expect(within(dialog).queryByRole("button", { name: "บันทึกข้อมูลทั่วไป" })).toBeNull();
    expect(within(dialog).queryByRole("button", { name: "บันทึกการตั้งค่าเครือข่าย" })).toBeNull();

    fireEvent.click(within(dialog).getByRole("button", { name: "ยืนยัน" }));

    await waitFor(() =>
      expect(patchBody).toEqual({
        displayName: "Permission PC",
        hourlyPriceMinorUnits: 100,
        moonlightPort: 47989,
        sunshinePort: 47990,
        vncLocalIp: null,
        parsecEnabled: false,
        expectedRevision: 1,
      }),
    );
    const alert = await screen.findByRole("alertdialog");
    expect(within(alert).getByText("ยืนยันอนุมัติเครื่อง Permission PC")).toBeDefined();
    expect(within(alert).queryByLabelText(/fingerprint/i)).toBeNull();

    fireEvent.click(within(alert).getByRole("button", { name: "อนุมัติ" }));

    await waitFor(() => expect(approveBody).toEqual({ fingerprint: FINGERPRINT }));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(await screen.findByText("อนุมัติเครื่อง Permission PC แล้ว")).toBeDefined();
    expect(screen.queryByRole("button", { name: "อนุมัติเครื่อง permission-pc" })).toBeNull();
  });

  it("saves all settings with one save button and the expected revision", async () => {
    let patchBody: unknown = null;
    let machineReads = 0;
    const savedMachine = machineFixture({
      machineId: "edit-pc",
      displayName: "Edit PC",
      vncLocalIp: "192.168.1.60",
    });
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === MACHINES_URL) {
        machineReads++;
        return jsonResponse([savedMachine]);
      }
      if (url === "/api/admin/machines/edit-pc" && init?.method === "PATCH") {
        patchBody = JSON.parse(String(init.body));
        return jsonResponse(savedMachine);
      }
      unexpectedUrls.push(url);
      throw new Error(`Unexpected request ${url}`);
    });

    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("edit-pc");
    await openSettings("edit-pc");

    expect(screen.getByRole("button", { name: "บันทึกการตั้งค่า" })).toBeDefined();
    expect(screen.queryByRole("button", { name: "บันทึกข้อมูลทั่วไป" })).toBeNull();
    expect(screen.queryByRole("button", { name: "บันทึกการตั้งค่าเครือข่าย" })).toBeNull();

    fireEvent.change(screen.getByLabelText("ชื่อที่แสดง"), { target: { value: "Edit PC 2" } });
    fireEvent.change(screen.getByLabelText("ราคา (บาท/ชั่วโมง)"), { target: { value: "250" } });
    await activateTab("เครือข่าย");
    fireEvent.change(screen.getByLabelText("Moonlight port"), { target: { value: "48000" } });
    fireEvent.change(screen.getByLabelText("Management port"), { target: { value: "48001" } });
    fireEvent.click(screen.getByRole("button", { name: "บันทึกการตั้งค่า" }));

    await waitFor(() =>
      expect(patchBody).toEqual({
        displayName: "Edit PC 2",
        hourlyPriceMinorUnits: 25000,
        moonlightPort: 48000,
        sunshinePort: 48001,
        vncLocalIp: "192.168.1.60",
        parsecEnabled: false,
        expectedRevision: 1,
      }),
    );
    expect(await screen.findByText("บันทึกการตั้งค่า Edit PC แล้ว")).toBeDefined();
    expect(machineReads).toBe(2);
  });

  it("exposes the four settings tabs and links PC Control without calling its endpoints", async () => {
    machinesResponse = [machineFixture({ machineId: "boundary-pc", displayName: "Boundary PC" })];

    render(<MachinesManager actorId="test-admin" />);
    await screen.findByText("boundary-pc");

    const controlLink = screen.getByRole("link", { name: "ควบคุมเครื่อง boundary-pc" });
    expect(controlLink.getAttribute("href")).toBe("/admin/pc-control");

    await openSettings("boundary-pc");
    expect(screen.getAllByRole("tab").map((tab) => tab.textContent)).toEqual([
      "ทั่วไป",
      "เครือข่าย",
      "Agent",
      "ผลตรวจ",
    ]);
    await activateTab("Agent");
    expect(await screen.findByText(/เวอร์ชัน Agent: 1\.0\.0/)).toBeDefined();
    expect(screen.getByRole("button", { name: "Update Agent" })).toBeDefined();
    expectNetworkIsolation(0);
  });
});
