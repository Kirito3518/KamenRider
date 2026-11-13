import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AgentReleasesManager } from "@/components/admin/agent-releases-manager";
import { AgentUpdateControl } from "@/components/admin/agent-update-control";
import { updateStatusLabels } from "@/lib/agent-releases";

const now = "2026-11-08T12:00:00.000Z";
const release = (version: string, channel: string) => ({ version, channel, releaseNotes: `Notes ${version}`, packageSha256: "A".repeat(64), packageSizeBytes: 100, sourceCommit: "abc", sourceDirty: false, publishedAtUtc: now });
const machine = { machineId: "pc-1", approvalStatus: "approved", displayName: "PC 1", localIp: "192.168.1.2", vncLocalIp: null, hourlyPriceMinorUnits: 100, moonlightPort: 1, sunshinePort: 2, configRevision: 1, appliedConfigRevision: 1, readiness: "notReady", canRent: false, allocationHeld: false, lastSeenAt: now, agentVersion: "1.0.0", activeAdminVncSessions: 0, agentUpdateStatus: null, agentUpdateOperationId: null, agentUpdateTargetVersion: null };
const json = (body: unknown, ok = true, status = ok ? 200 : 409) => ({ ok, status, json: async () => body }) as Response;

describe("Agent releases dashboard", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(now));
    fetchSpy = vi.fn(async (input: RequestInfo | URL) => {
      if (String(input) === "/api/admin/machines") return json([machine, { ...machine, machineId: "pc-2", lastSeenAt: null, agentUpdateStatus: "failed" }]);
      if (String(input) === "/api/admin/agent-releases") return json([release("2.0.0", "stable"), release("1.0.0", "beta")]);
      throw new Error(`Unexpected request ${input}`);
    });
    vi.stubGlobal("fetch", fetchSpy);
  });
  afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

  it("shows actual fleet counts, release channels, hashes, and actionable warnings", async () => {
    render(<AgentReleasesManager />);
    expect(await screen.findByText("2 / 2")).toBeDefined();
    expect(screen.getByText("1 / 2")).toBeDefined();
    expect(screen.getByText("0 / 2")).toBeDefined();
    expect(screen.getAllByText("stable").length).toBeGreaterThan(0);
    expect(screen.getAllByText("beta").length).toBeGreaterThan(0);
    expect(screen.getAllByText(/AAAAAAAAAAAA/).length).toBeGreaterThan(0);
    expect(screen.getByText(/1 เครื่อง.*ติดต่อไม่ได้/)).toBeDefined();
    expect(screen.getByText(/1 เครื่อง.*ล้มเหลว/)).toBeDefined();
    expect(fetchSpy.mock.calls.some(([input]) => String(input).includes("agent-update-command"))).toBe(false);
  });

  it("rejects an incomplete package and submits the complete package with release fields", async () => {
    render(<AgentReleasesManager />);
    await screen.findByText("Notes 2.0.0");
    fireEvent.change(screen.getByLabelText("เวอร์ชัน release"), { target: { value: "3.0.0" } });
    fireEvent.change(screen.getByLabelText("Release notes"), { target: { value: "New release" } });
    fireEvent.change(screen.getByLabelText("AgentPackage.zip"), { target: { files: [new File(["bad"], "Agent.exe")] } });
    fireEvent.submit(screen.getByRole("button", { name: "เผยแพร่ release" }).closest("form")!);
    expect(await screen.findByText(/เลือกไฟล์ AgentPackage.zip/)).toBeDefined();
    expect(fetchSpy.mock.calls.filter(([, init]) => init?.method === "POST")).toHaveLength(0);

    fireEvent.change(screen.getByLabelText("ช่องทาง release"), { target: { value: "beta" } });
    fireEvent.change(screen.getByLabelText("AgentPackage.zip"), { target: { files: [new File(["zip"], "AgentPackage.zip", { type: "application/zip" })] } });
    fireEvent.submit(screen.getByRole("button", { name: "เผยแพร่ release" }).closest("form")!);
    await waitFor(() => expect(fetchSpy.mock.calls.filter(([, init]) => init?.method === "POST")).toHaveLength(1));
    const body = fetchSpy.mock.calls.find(([, init]) => init?.method === "POST")?.[1]?.body as FormData;
    expect(Array.from(body.keys())).toEqual(["version", "channel", "releaseNotes", "package"]);
    expect(body.get("version")).toBe("3.0.0");
    expect(body.get("channel")).toBe("beta");
    expect(body.get("releaseNotes")).toBe("New release");
    expect((body.get("package") as File).name).toBe("AgentPackage.zip");
  });

  it("shows an empty history and distinguishes a fleet loading error from zero machines", async () => {
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => String(input) === "/api/admin/machines" ? json({}, false, 503) : json([]));
    render(<AgentReleasesManager />);
    expect(await screen.findByText("ยังไม่มี release ที่เผยแพร่")).toBeDefined();
    expect(screen.getByRole("alert").textContent).toContain("โหลดข้อมูลเครื่องไม่ได้");
    expect(screen.getAllByText("โหลดไม่ได้").length).toBeGreaterThan(0);
    expect(screen.queryByText("0 / 0")).toBeNull();
  });

  it("counts each machine's latest update outcome and reported version without implying rental readiness", async () => {
    const statuses = Object.keys(updateStatusLabels);
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => String(input) === "/api/admin/machines"
      ? json(statuses.map((status, index) => ({ ...machine, machineId: `pc-${index}`, agentUpdateStatus: status, agentVersion: index < 3 ? "2.0.0" : "1.0.0" })))
      : json([release("2.0.0", "stable")]));
    render(<AgentReleasesManager />);
    const outcomes = await screen.findByRole("region", { name: "สถานะอัปเดตล่าสุดของแต่ละเครื่อง" });
    for (const label of Object.values(updateStatusLabels)) {
      expect(within(outcomes).getByLabelText(`${label}: 1`).textContent).toContain("1");
    }
    const versions = screen.getByRole("region", { name: "เวอร์ชันที่เครื่องรายงาน" });
    expect(within(versions).getByLabelText("2.0.0: 3 เครื่อง")).toBeDefined();
    expect(within(versions).getByLabelText("1.0.0: 5 เครื่อง")).toBeDefined();
    expect(within(outcomes).getByText(/แยกจากการอนุมัติและความพร้อมให้เช่า/)).toBeDefined();
    expect(fetchSpy.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);
  });

  it("reports a rejected package without claiming publication or requesting an update", async () => {
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => init?.method === "POST"
      ? json({}, false, 400)
      : String(input) === "/api/admin/machines" ? json([machine]) : json([]));
    render(<AgentReleasesManager />);
    await screen.findByText("ยังไม่มี release ที่เผยแพร่");
    fireEvent.change(screen.getByLabelText("เวอร์ชัน release"), { target: { value: "3.0.0" } });
    fireEvent.change(screen.getByLabelText("Release notes"), { target: { value: "Wrong manifest version" } });
    fireEvent.change(screen.getByLabelText("AgentPackage.zip"), { target: { files: [new File(["zip"], "AgentPackage.zip")] } });
    fireEvent.submit(screen.getByRole("button", { name: "เผยแพร่ release" }).closest("form")!);
    expect(await screen.findByText(/เผยแพร่ไม่สำเร็จ.*manifest/)).toBeDefined();
    expect(screen.queryByText(/เผยแพร่ release แล้ว Controller/)).toBeNull();
    expect(screen.getByLabelText("เวอร์ชัน release")).toHaveProperty("value", "3.0.0");
    expect(fetchSpy.mock.calls.filter(([, init]) => init?.method === "POST")).toHaveLength(1);
  });

  it("keeps the post-publication snapshot when an older scheduled poll finishes later", async () => {
    const intervalSpy = vi.spyOn(window, "setInterval");
    let resolveOldMachines!: (response: Response) => void;
    let resolveOldReleases!: (response: Response) => void;
    const oldMachines = new Promise<Response>((resolve) => { resolveOldMachines = resolve; });
    const oldReleases = new Promise<Response>((resolve) => { resolveOldReleases = resolve; });
    let polling = false;
    let published = false;
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === "POST") { published = true; return json(release("3.0.0", "stable")); }
      if (published) return String(input) === "/api/admin/machines"
        ? json([{ ...machine, agentVersion: "3.0.0" }]) : json([release("3.0.0", "stable")]);
      if (polling) return String(input) === "/api/admin/machines" ? oldMachines : oldReleases;
      return String(input) === "/api/admin/machines" ? json([machine]) : json([release("2.0.0", "stable")]);
    });
    render(<AgentReleasesManager />);
    await screen.findByText("Notes 2.0.0");
    polling = true;
    const poll = intervalSpy.mock.calls.find(([, delay]) => delay === 10_000)![0] as () => void;
    act(() => { poll(); });
    fireEvent.change(screen.getByLabelText("เวอร์ชัน release"), { target: { value: "3.0.0" } });
    fireEvent.change(screen.getByLabelText("Release notes"), { target: { value: "New release" } });
    fireEvent.change(screen.getByLabelText("AgentPackage.zip"), { target: { files: [new File(["zip"], "AgentPackage.zip")] } });
    fireEvent.submit(screen.getByRole("button", { name: "เผยแพร่ release" }).closest("form")!);
    await screen.findByText("Notes 3.0.0");
    await act(async () => {
      resolveOldMachines(json([machine, { ...machine, machineId: "pc-old", lastSeenAt: null }]));
      resolveOldReleases(json([release("2.0.0", "stable")]));
    });
    expect(screen.getByText("Notes 3.0.0")).toBeDefined();
    expect(screen.queryByText("Notes 2.0.0")).toBeNull();
    expect(screen.getByText("stable ล่าสุดที่เผยแพร่").closest("section")?.textContent).toContain("3.0.0");
    expect(screen.getByText("ใช้ stable ล่าสุด").closest("section")?.textContent).toContain("1 / 1");
    expect(screen.getByLabelText("3.0.0: 1 เครื่อง")).toBeDefined();
  });
});

describe("Per-machine update", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  let clipboard: ReturnType<typeof vi.fn>;
  beforeEach(() => {
    window.sessionStorage.clear();
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(new Date(now));
    clipboard = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: clipboard } });
    fetchSpy = vi.fn(async (input: RequestInfo | URL) => String(input) === "/api/admin/agent-releases"
      ? json([release("2.0.0", "stable"), release("1.0.0", "beta")])
      : json({ available: true, command: "full command including ticket", operationId: "op-1", expiresAtUtc: "2026-11-08T12:10:00.000Z", targetVersion: "2.0.0" }));
    vi.stubGlobal("fetch", fetchSpy);
  });
  afterEach(() => { cleanup(); window.sessionStorage.clear(); vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); Reflect.deleteProperty(navigator, "clipboard"); });

  it("permits a pending machine update while preserving the rental gate", () => {
    render(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, approvalStatus: "pending", canRent: false }} />);
    expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled).toBe(false);
    expect(screen.getByText(/แยกจากการอนุมัติและความพร้อมให้เช่า/)).toBeDefined();
  });

  it("explains recovery when the rolled-back Agent cannot confirm the outcome", () => {
    render(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, agentUpdateStatus: "rollingBack", agentUpdateOperationId: "op-1" }} />);
    expect(screen.getByText(/Agent รุ่นเดิม.*ยืนยัน/)).toBeDefined();
    expect((screen.getByRole("button", { name: "ขอ recovery intent" }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/เป้าหมายเดิม.*รองรับ/)).toBeDefined();
    expect(screen.getByText(/Recovery intent จะคง hold ไว้/)).toBeDefined();
  });

  it("disables a held lease but permits an unready machine without one", () => {
    const { rerender } = render(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, allocationHeld: true }} />);
    expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/มี lease/)).toBeDefined();
    rerender(<AgentUpdateControl actorId="test-admin" machine={machine} />);
    expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled).toBe(false);
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it.each(Object.entries(updateStatusLabels))("renders %s and keeps active operation locks", (status, label) => {
    render(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, agentUpdateStatus: status, agentUpdateOperationId: "op-1" }} />);
    expect(screen.getByText(`สถานะอัปเดต: ${label}`)).toBeDefined();
    expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled)
      .toBe(!["completed", "rolledBack", "expired"].includes(status));
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("mints only after selecting a release, shows the whole command, and copies it", async () => {
    render(<AgentUpdateControl actorId="test-admin" machine={machine} />);
    fireEvent.click(screen.getByRole("button", { name: "Update Agent" }));
    await screen.findByRole("option", { name: "2.0.0 · stable" });
    expect(screen.queryByRole("option", { name: "1.0.0 · beta" })).toBeNull();
    expect(fetchSpy.mock.calls).toHaveLength(1);
    fireEvent.change(screen.getByLabelText("เลือก release สำหรับ pc-1"), { target: { value: "2.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้างคำสั่งอัปเดต" }));
    expect(await screen.findByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1")).toHaveProperty("value", "full command including ticket");
    const request = fetchSpy.mock.calls.find(([input]) => String(input).includes("agent-update-command"));
    expect(JSON.parse(String(request?.[1]?.body))).toEqual({ releaseVersion: "2.0.0" });
    fireEvent.click(screen.getByRole("button", { name: "คัดลอกคำสั่งอัปเดต" }));
    await waitFor(() => expect(clipboard).toHaveBeenCalledWith("full command including ticket"));
  });

  it.each(["completed", "rolledBack", "expired"])("retains a fresh command after %s until a matching snapshot, including delayed and failed refresh", async (status) => {
    let finishRefresh!: (refreshed: boolean) => void;
    const onUpdated = vi.fn(() => new Promise<boolean>((resolve) => { finishRefresh = resolve; }));
    const previous = { ...machine, agentUpdateStatus: status, agentUpdateOperationId: "previous-operation", agentUpdateTargetVersion: "1.0.0" };
    const { rerender } = render(<AgentUpdateControl actorId="test-admin" machine={previous} onUpdated={onUpdated} />);
    fireEvent.click(screen.getByRole("button", { name: "Update Agent" }));
    await screen.findByRole("option", { name: "2.0.0 · stable" });
    fireEvent.change(screen.getByLabelText("เลือก release สำหรับ pc-1"), { target: { value: "2.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้างคำสั่งอัปเดต" }));
    const commandField = await screen.findByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1");
    const updateButton = screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement;
    expect(updateButton.disabled).toBe(true);
    fireEvent.click(updateButton);
    expect(commandField).toHaveProperty("value", "full command including ticket");
    expect(fetchSpy).toHaveBeenCalledTimes(2);
    // The parent catches a failed refresh, so its promise settles without new machine props.
    await act(async () => { finishRefresh(true); });
    expect(updateButton.disabled).toBe(true);
    rerender(<AgentUpdateControl actorId="test-admin" machine={{ ...previous }} onUpdated={onUpdated} />);
    expect(updateButton.disabled).toBe(true);
    expect(screen.getByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1")).toHaveProperty("value", "full command including ticket");
    expect(screen.getByText(/สถานะอัปเดต: รอเรียกใช้ตัวติดตั้ง.*เป้าหมาย 2.0.0/)).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "คัดลอกคำสั่งอัปเดต" }));
    await waitFor(() => expect(clipboard).toHaveBeenCalledWith("full command including ticket"));
    rerender(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, agentUpdateStatus: "completed", agentUpdateOperationId: "op-1", agentVersion: "2.0.0" }} onUpdated={onUpdated} />);
    expect(updateButton.disabled).toBe(false);
  });

  it("keeps the issued command and local lock when the refresh callback rejects", async () => {
    const onUpdated = vi.fn().mockRejectedValue(new Error("Fixture refresh failed"));
    render(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, agentUpdateStatus: "completed", agentUpdateOperationId: "old-operation" }} onUpdated={onUpdated} />);
    fireEvent.click(screen.getByRole("button", { name: "Update Agent" }));
    await screen.findByRole("option", { name: "2.0.0 · stable" });
    fireEvent.change(screen.getByLabelText("เลือก release สำหรับ pc-1"), { target: { value: "2.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้างคำสั่งอัปเดต" }));
    await screen.findByText(/โหลดสถานะเครื่องล่าสุดไม่ได้/);
    expect(screen.getByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1")).toHaveProperty("value", "full command including ticket");
    expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled).toBe(true);
    expect(fetchSpy).toHaveBeenCalledTimes(2);
  });

  it.each(["installing", "failed"])("follows a later operation in %s after observing the issued operation complete", async (status) => {
    const { rerender } = render(<AgentUpdateControl actorId="test-admin" machine={machine} />);
    fireEvent.click(screen.getByRole("button", { name: "Update Agent" }));
    await screen.findByRole("option", { name: "2.0.0 · stable" });
    fireEvent.change(screen.getByLabelText("เลือก release สำหรับ pc-1"), { target: { value: "2.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้างคำสั่งอัปเดต" }));
    await screen.findByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1");
    rerender(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, agentUpdateStatus: "completed", agentUpdateOperationId: "op-1", agentVersion: "2.0.0", agentUpdateTargetVersion: "2.0.0" }} />);
    expect(screen.getByText(/สถานะอัปเดต: อัปเดตสำเร็จและยืนยันแล้ว/)).toBeDefined();
    const updateCommandCount = fetchSpy.mock.calls.filter(([input]) => String(input).includes("agent-update-command")).length;
    const laterOperation = { ...machine, agentVersion: "2.0.0", agentUpdateStatus: status, agentUpdateOperationId: "op-2", agentUpdateTargetVersion: "3.0.0" };
    rerender(<AgentUpdateControl actorId="test-admin" machine={laterOperation} />);
    expect(screen.getByText(`สถานะอัปเดต: ${updateStatusLabels[status]} · เป้าหมาย 3.0.0`)).toBeDefined();
    expect(screen.queryByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1")).toBeNull();
    const recovery = screen.getByRole("button", { name: "ขอ recovery intent" }) as HTMLButtonElement;
    expect(recovery.disabled).toBe(false);
    rerender(<AgentUpdateControl actorId="test-admin" machine={{ ...laterOperation }} />);
    expect(screen.getByText(`สถานะอัปเดต: ${updateStatusLabels[status]} · เป้าหมาย 3.0.0`)).toBeDefined();
    expect(screen.queryByRole("button", { name: "กู้คืนการอัปเดต" })).toBeNull();
    expect(fetchSpy.mock.calls.filter(([input]) => String(input).includes("agent-update-command"))).toHaveLength(updateCommandCount);
    expect(recovery.disabled).toBe(false);
  });

  it("offers an intent request for an existing failed operation instead of issuing an update command", async () => {
    render(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "op-1", agentUpdateTargetVersion: "2.0.0" }} />);
    expect(screen.getByText(/ล้มเหลว/)).toBeDefined();
    expect(screen.getByRole("button", { name: "ขอ recovery intent" })).toBeDefined();
    expect(screen.queryByRole("button", { name: "กู้คืนการอัปเดต" })).toBeNull();
    expect(screen.getByText(/intent จะคง hold ไว้/)).toBeDefined();
    expect(fetchSpy.mock.calls.some(([input]) => String(input).includes("agent-update-command"))).toBe(false);
  });

  it("creates a held recovery intent from the exact Controller head and reconciles the same request ID after an ambiguous post", async () => {
    vi.spyOn(crypto, "randomUUID").mockReturnValue("request-1");
    const context = {
      machineId: "pc-1", chainId: "chain-1", currentHeadOperationId: "operation-4",
      currentGeneration: 4, currentStatus: "failed", currentTargetVersion: "2.0.0",
      held: true, allocationHeld: false,
    };
    const intent = {
      intentId: "intent-1", machineId: "pc-1", chainId: "chain-1",
      previousHeadOperationId: "operation-4", expectedGeneration: 4, generation: 5,
      targetReleaseVersion: "3.0.0", targetVersion: "3.0.0", targetRuntime: "win-x64",
      reason: "The current update failed and needs inspection.", requestId: "request-1",
      status: "awaitingInspection", held: true,
      createdAtUtc: now, expiresAtUtc: "2026-11-09T12:00:00.000Z",
    };
    let postBody: Record<string, unknown> | null = null;
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith("agent-update-recovery-context")) return json({ available: true, context });
      if (url === "/api/admin/agent-releases") return json([release("3.0.0", "stable")]);
      if (url.endsWith("agent-update-recovery-intents") && init?.method === "POST") {
        postBody = JSON.parse(String(init.body)) as Record<string, unknown>;
        return json({ available: false, pending: false, error: "Response was lost" }, false, 503);
      }
      if (url.includes("agent-update-recovery-intents?requestId=")) return json({ available: true, pending: true, intent });
      throw new Error(`Unexpected request ${url}`);
    });

    render(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4", agentUpdateTargetVersion: "2.0.0" }} />);
    fireEvent.click(screen.getByRole("button", { name: "ขอ recovery intent" }));
    await screen.findByLabelText("เลือก target release สำหรับ pc-1");
    fireEvent.change(screen.getByLabelText("เหตุผล recovery"), { target: { value: intent.reason } });
    fireEvent.change(screen.getByLabelText("เลือก target release สำหรับ pc-1"), { target: { value: "3.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้าง recovery intent" }));

    expect(await screen.findByText(/Recovery intent recorded and still held/)).toBeDefined();
    expect(postBody).toEqual({
      expectedChainId: "chain-1", expectedHeadId: "operation-4", expectedGeneration: 4,
      targetReleaseVersion: "3.0.0", reason: intent.reason, requestId: "request-1",
    });
    const reconcile = fetchSpy.mock.calls.find(([input]) => String(input).includes("agent-update-recovery-intents?requestId="));
    expect(reconcile?.[1]?.method).toBeUndefined();
    expect(String(reconcile?.[0])).toContain("request-1");
    expect(fetchSpy.mock.calls.some(([input]) => String(input).includes("agent-update-command"))).toBe(false);
    expect(screen.queryByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1")).toBeNull();
  });

  it("locks an ambiguous request after GET 404 and restores the same body across close, reopen, and remount", async () => {
    const uuid = vi.spyOn(crypto, "randomUUID").mockReturnValue("request-delayed");
    const context = {
      machineId: "pc-1", chainId: "chain-1", currentHeadOperationId: "operation-4",
      currentGeneration: 4, currentStatus: "failed", currentTargetVersion: "2.0.0",
      held: true, allocationHeld: false,
    };
    const intent = {
      intentId: "intent-delayed", machineId: "pc-1", chainId: "chain-1",
      previousHeadOperationId: "operation-4", expectedGeneration: 4, generation: 5,
      targetReleaseVersion: "3.0.0", targetVersion: "3.0.0", targetRuntime: "win-x64",
      reason: "The exact request must survive until its delayed commit is visible.", requestId: "request-delayed",
      status: "awaitingInspection", held: true,
      createdAtUtc: now, expiresAtUtc: "2026-11-09T12:00:00.000Z",
    };
    let lookupCount = 0;
    const postedBodies: Record<string, unknown>[] = [];
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith("agent-update-recovery-context")) return json({ available: true, context });
      if (url === "/api/admin/agent-releases") return json([release("3.0.0", "stable")]);
      if (url.endsWith("agent-update-recovery-intents") && init?.method === "POST") {
        postedBodies.push(JSON.parse(String(init.body)) as Record<string, unknown>);
        return json({ available: false, pending: false, error: "Response was lost" }, false, 503);
      }
      if (url.includes("agent-update-recovery-intents?requestId=")) {
        lookupCount++;
        return lookupCount < 3
          ? json({ available: false, pending: false }, false, 404)
          : json({ available: true, pending: true, intent });
      }
      throw new Error(`Unexpected request ${url}`);
    });

    const firstProps = { ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4", agentUpdateTargetVersion: "2.0.0" };
    const first = render(<AgentUpdateControl actorId="admin-a" machine={firstProps} />);
    fireEvent.click(screen.getByRole("button", { name: "ขอ recovery intent" }));
    await screen.findByLabelText("เลือก target release สำหรับ pc-1");
    fireEvent.change(screen.getByLabelText("เหตุผล recovery"), { target: { value: intent.reason } });
    fireEvent.change(screen.getByLabelText("เลือก target release สำหรับ pc-1"), { target: { value: "3.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้าง recovery intent" }));

    expect(await screen.findByText(/No intent was found for this request ID/)).toBeDefined();
    expect(screen.queryByRole("button", { name: "สร้าง recovery intent" })).toBeNull();
    expect(uuid).toHaveBeenCalledTimes(1);
    const exactBody = {
      expectedChainId: "chain-1", expectedHeadId: "operation-4", expectedGeneration: 4,
      targetReleaseVersion: "3.0.0", reason: intent.reason, requestId: "request-delayed",
    };
    expect(postedBodies).toEqual([exactBody]);
    expect(window.sessionStorage.getItem("agent-recovery-attempt:v1:admin-a:pc-1")).toBe(JSON.stringify(exactBody));

    fireEvent.click(screen.getByRole("button", { name: "ลอง request เดิมซ้ำ" }));
    expect(await screen.findByText(/No intent was found for this request ID/)).toBeDefined();
    expect(postedBodies).toEqual([exactBody, exactBody]);
    expect(uuid).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole("button", { name: "ปิด" }));
    expect(screen.queryByText("Request ID: request-delayed")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "ตรวจสอบ recovery request เดิม" }));
    expect(await screen.findByText("Request ID: request-delayed")).toBeDefined();
    first.unmount();

    render(<AgentUpdateControl actorId="admin-a" machine={firstProps} />);
    expect(await screen.findByText("Request ID: request-delayed")).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "ตรวจสอบ request ID" }));
    expect(await screen.findByText(/Recovery intent recorded and still held/)).toBeDefined();
    expect(screen.getByText(/Intent intent-delayed · request request-delayed · awaitingInspection/)).toBeDefined();
    expect(screen.getByText(`หมดอายุ (UTC): ${intent.expiresAtUtc}`)).toBeDefined();
    expect(screen.getByText(/Chain ยังคง held/)).toBeDefined();
    expect(lookupCount).toBe(3);
    expect(window.sessionStorage.getItem("agent-recovery-attempt:v1:admin-a:pc-1")).toBeNull();
    expect(fetchSpy.mock.calls.some(([input]) => String(input).includes("agent-update-command"))).toBe(false);
  });

  it("keeps a pending attempt private to its authenticated actor", async () => {
    const context = {
      machineId: "pc-1", chainId: "chain-1", currentHeadOperationId: "operation-4",
      currentGeneration: 4, currentStatus: "failed", currentTargetVersion: "2.0.0",
      held: true, allocationHeld: false,
    };
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith("agent-update-recovery-context")) return json({ available: true, context });
      if (url === "/api/admin/agent-releases") return json([release("3.0.0", "stable")]);
      if (url.endsWith("agent-update-recovery-intents") && init?.method === "POST")
        return json({ available: false, pending: false, error: "Response was lost" }, false, 503);
      if (url.includes("agent-update-recovery-intents?requestId="))
        return json({ available: false, pending: false }, false, 404);
      throw new Error(`Unexpected request ${url}`);
    });
    const props = { ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4" };
    const first = render(<AgentUpdateControl actorId="admin-a" machine={props} />);
    fireEvent.click(screen.getByRole("button", { name: "ขอ recovery intent" }));
    await screen.findByLabelText("เหตุผล recovery");
    fireEvent.change(screen.getByLabelText("เหตุผล recovery"), { target: { value: "private request" } });
    fireEvent.change(screen.getByLabelText("เลือก target release สำหรับ pc-1"), { target: { value: "3.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้าง recovery intent" }));
    expect(await screen.findByText(/No intent was found for this request ID/)).toBeDefined();
    const lookupCallsBeforeOtherActor = fetchSpy.mock.calls.filter(([input]) => String(input).includes("agent-update-recovery-intents?requestId=")).length;
    first.rerender(<AgentUpdateControl actorId="admin-b" machine={props} />);
    expect(screen.queryByText(/private request/)).toBeNull();
    expect(screen.queryByText(/Request ID:/)).toBeNull();
    expect(screen.getByRole("button", { name: "ขอ recovery intent" })).toBeDefined();
    await waitFor(() => expect(fetchSpy.mock.calls.filter(([input]) => String(input).includes("agent-update-recovery-intents?requestId="))).toHaveLength(lookupCallsBeforeOtherActor));
    expect(window.sessionStorage.getItem("agent-recovery-attempt:v1:admin-a:pc-1")).not.toBeNull();
  });

  it("preserves the exact pending attempt when request reconciliation returns 503", async () => {
    const uuid = vi.spyOn(crypto, "randomUUID").mockReturnValue("request-quarantined");
    const context = {
      machineId: "pc-1", chainId: "chain-1", currentHeadOperationId: "operation-4",
      currentGeneration: 4, currentStatus: "failed", currentTargetVersion: "2.0.0",
      held: true, allocationHeld: false,
    };
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith("agent-update-recovery-context")) return json({ available: true, context });
      if (url === "/api/admin/agent-releases") return json([release("3.0.0", "stable")]);
      if (url.endsWith("agent-update-recovery-intents") && init?.method === "POST")
        return json({ available: false, pending: false, error: "Response was lost" }, false, 503);
      if (url.includes("agent-update-recovery-intents?requestId="))
        return json({ available: false, pending: false, error: "Recovery intents are not available right now." }, false, 503);
      throw new Error(`Unexpected request ${url}`);
    });

    render(<AgentUpdateControl actorId="admin-503" machine={{ ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4" }} />);
    fireEvent.click(screen.getByRole("button", { name: "ขอ recovery intent" }));
    await screen.findByLabelText("เหตุผล recovery");
    fireEvent.change(screen.getByLabelText("เหตุผล recovery"), { target: { value: "Keep this request until quarantine ends" } });
    fireEvent.change(screen.getByLabelText("เลือก target release สำหรับ pc-1"), { target: { value: "3.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้าง recovery intent" }));

    expect(await screen.findByText("Request ID: request-quarantined")).toBeDefined();
    expect(await screen.findByText("Recovery intents are not available right now.")).toBeDefined();
    expect(screen.queryByRole("button", { name: "สร้าง recovery intent" })).toBeNull();
    expect(window.sessionStorage.getItem("agent-recovery-attempt:v1:admin-503:pc-1")).not.toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "ตรวจสอบ request ID" }));
    await waitFor(() => expect(fetchSpy.mock.calls.filter(([input]) => String(input).includes("agent-update-recovery-intents?requestId=")).length).toBe(2));
    expect(screen.getByText("Request ID: request-quarantined")).toBeDefined();
    expect(screen.queryByRole("button", { name: "สร้าง recovery intent" })).toBeNull();
    expect(window.sessionStorage.getItem("agent-recovery-attempt:v1:admin-503:pc-1")).not.toBeNull();
    expect(uuid).toHaveBeenCalledTimes(1);
    expect(fetchSpy.mock.calls.filter(([, init]) => init?.method === "POST")).toHaveLength(1);
  });

  it("keeps intent creation disabled when Controller says context is unavailable or allocation is still held", async () => {
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      if (String(input).endsWith("agent-update-recovery-context")) return json({ available: false, error: "Recovery context unavailable" }, false, 503);
      if (String(input) === "/api/admin/agent-releases") return json([release("3.0.0", "stable")]);
      throw new Error(`Unexpected request ${input}`);
    });
    render(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4" }} />);
    fireEvent.click(screen.getByRole("button", { name: "ขอ recovery intent" }));
    expect(await screen.findByText(/recovery context unavailable/i)).toBeDefined();
    expect(screen.queryByRole("button", { name: "สร้าง recovery intent" })).toBeNull();
    expect(fetchSpy.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);
  });

  it("does not create an intent while the Controller context still has an allocation hold", async () => {
    const context = {
      machineId: "pc-1", chainId: "chain-1", currentHeadOperationId: "operation-4",
      currentGeneration: 4, currentStatus: "failed", currentTargetVersion: "2.0.0",
      held: true, allocationHeld: true,
    };
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      if (String(input).endsWith("agent-update-recovery-context")) return json({ available: true, context });
      if (String(input) === "/api/admin/agent-releases") return json([release("3.0.0", "stable")]);
      throw new Error(`Unexpected request ${input}`);
    });
    render(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4" }} />);
    fireEvent.click(screen.getByRole("button", { name: "ขอ recovery intent" }));
    expect(await screen.findByText(/มี allocation hold จึงสร้าง recovery intent ไม่ได้/)).toBeDefined();
    expect(screen.queryByRole("button", { name: "สร้าง recovery intent" })).toBeNull();
    expect(fetchSpy.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);
  });

  it("shows a stale current head conflict without retrying or reconciling another request", async () => {
    const context = {
      machineId: "pc-1", chainId: "chain-1", currentHeadOperationId: "operation-4",
      currentGeneration: 4, currentStatus: "failed", currentTargetVersion: "2.0.0",
      held: true, allocationHeld: false,
    };
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith("agent-update-recovery-context")) return json({ available: true, context });
      if (url === "/api/admin/agent-releases") return json([release("3.0.0", "stable")]);
      if (url.endsWith("agent-update-recovery-intents") && init?.method === "POST") return json({
        available: false, pending: false,
        error: "The maintenance head changed. Refresh the current state before retrying.",
        currentHead: { currentChainId: "chain-2", currentHeadOperationId: "operation-5", currentGeneration: 8 },
      }, false, 409);
      throw new Error(`Unexpected request ${url}`);
    });
    const onUpdated = vi.fn().mockResolvedValue(true);
    render(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4" }} onUpdated={onUpdated} />);
    fireEvent.click(screen.getByRole("button", { name: "ขอ recovery intent" }));
    await screen.findByLabelText("เหตุผล recovery");
    fireEvent.change(screen.getByLabelText("เหตุผล recovery"), { target: { value: "Stale attempt" } });
    fireEvent.change(screen.getByLabelText("เลือก target release สำหรับ pc-1"), { target: { value: "3.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้าง recovery intent" }));
    expect(await screen.findByText(/chain chain-2, head operation-5, generation 8/)).toBeDefined();
    expect(screen.getByText(/No intent was accepted.*Latest machine data was refreshed/)).toBeDefined();
    expect(onUpdated).toHaveBeenCalledTimes(1);
    expect(fetchSpy.mock.calls.filter(([, init]) => init?.method === "POST")).toHaveLength(1);
    expect(fetchSpy.mock.calls.some(([input]) => String(input).includes("agent-update-recovery-intents?requestId="))).toBe(false);
    expect(screen.queryByRole("button", { name: "สร้าง recovery intent" })).toBeNull();
  });

  it("discards an open recovery form when refreshed machine data has a different operation head", async () => {
    const context = {
      machineId: "pc-1", chainId: "chain-1", currentHeadOperationId: "operation-4",
      currentGeneration: 4, currentStatus: "failed", currentTargetVersion: "2.0.0",
      held: true, allocationHeld: false,
    };
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      if (String(input).endsWith("agent-update-recovery-context")) return json({ available: true, context });
      if (String(input) === "/api/admin/agent-releases") return json([release("3.0.0", "stable")]);
      throw new Error(`Unexpected request ${input}`);
    });
    const first = { ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4" };
    const { rerender } = render(<AgentUpdateControl actorId="test-admin" machine={first} />);
    fireEvent.click(screen.getByRole("button", { name: "ขอ recovery intent" }));
    await screen.findByLabelText("เหตุผล recovery");
    rerender(<AgentUpdateControl actorId="test-admin" machine={{ ...first, agentUpdateOperationId: "operation-5", agentUpdateStatus: "installing" }} />);
    expect(await screen.findByText(/หัว operation เปลี่ยนแล้ว/)).toBeDefined();
    expect(screen.queryByRole("button", { name: "สร้าง recovery intent" })).toBeNull();
    expect(fetchSpy.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);
  });

  it("shows an expired intent as still held without issuing a successor or update", async () => {
    vi.spyOn(crypto, "randomUUID").mockReturnValue("expiry-request");
    const context = {
      machineId: "pc-1", chainId: "chain-1", currentHeadOperationId: "op-1",
      currentGeneration: 4, currentStatus: "installing", currentTargetVersion: "2.0.0",
      held: true, allocationHeld: false,
    };
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith("agent-update-recovery-context")) return json({ available: true, context });
      if (url === "/api/admin/agent-releases") return json([release("3.0.0", "stable")]);
      if (url.endsWith("agent-update-recovery-intents") && init?.method === "POST") return json({ available: true, pending: true, intent: {
        intentId: "intent-1", machineId: "pc-1", chainId: "chain-1", previousHeadOperationId: "op-1",
        expectedGeneration: 4, generation: 5, targetReleaseVersion: "3.0.0", targetVersion: "3.0.0",
        targetRuntime: "win-x64", reason: "Inspect recovery", requestId: "expiry-request", status: "awaitingInspection",
        held: true, createdAtUtc: now, expiresAtUtc: "2026-11-08T12:10:00.000Z",
      } });
      throw new Error(`Unexpected request ${url}`);
    });
    render(<AgentUpdateControl actorId="test-admin" machine={{ ...machine, agentUpdateStatus: "installing", agentUpdateOperationId: "op-1" }} />);
    fireEvent.click(screen.getByRole("button", { name: "ขอ recovery intent" }));
    await screen.findByLabelText("เลือก target release สำหรับ pc-1");
    fireEvent.change(screen.getByLabelText("เหตุผล recovery"), { target: { value: "Inspect recovery" } });
    fireEvent.change(screen.getByLabelText("เลือก target release สำหรับ pc-1"), { target: { value: "3.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้าง recovery intent" }));
    await screen.findByText(/Chain ยังคง held/);
    act(() => {
      vi.setSystemTime(new Date("2026-11-08T12:10:00.000Z"));
      fireEvent.focus(window);
    });
    expect(await screen.findByText(/Intent หมดอายุแล้ว แต่ chain ยัง held/)).toBeDefined();
    expect(fetchSpy.mock.calls.some(([input]) => String(input).includes("agent-update-command"))).toBe(false);
  });

  it("keeps a confirmed active intent visible and blocks a new request across remount until expiry", async () => {
    vi.spyOn(crypto, "randomUUID").mockReturnValue("active-request");
    const actorId = "admin-active-intent";
    const context = {
      machineId: "pc-1", chainId: "chain-1", currentHeadOperationId: "operation-4",
      currentGeneration: 4, currentStatus: "failed", currentTargetVersion: "2.0.0", held: true, allocationHeld: false,
    };
    const intent = {
      intentId: "intent-active", machineId: "pc-1", chainId: "chain-1", previousHeadOperationId: "operation-4",
      expectedGeneration: 4, generation: 5, targetReleaseVersion: "3.0.0", targetVersion: "3.0.0", targetRuntime: "win-x64",
      reason: "Keep the current chain held until the intent expires.", requestId: "active-request", status: "awaitingInspection",
      held: true, createdAtUtc: now, expiresAtUtc: "2026-11-08T12:00:10.000Z",
    };
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith("agent-update-recovery-context")) return json({ available: true, context });
      if (url === "/api/admin/agent-releases") return json([release("3.0.0", "stable")]);
      if (url.endsWith("agent-update-recovery-intents") && init?.method === "POST") return json({ available: true, pending: true, intent });
      throw new Error(`Unexpected request ${url}`);
    });
    const props = { ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4" };
    const first = render(<AgentUpdateControl actorId={actorId} machine={props} />);
    fireEvent.click(screen.getByRole("button", { name: "ขอ recovery intent" }));
    await screen.findByLabelText("เลือก target release สำหรับ pc-1");
    fireEvent.change(screen.getByLabelText("เหตุผล recovery"), { target: { value: intent.reason } });
    fireEvent.change(screen.getByLabelText("เลือก target release สำหรับ pc-1"), { target: { value: "3.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้าง recovery intent" }));

    expect(await screen.findByText("Intent intent-active · request active-request · awaitingInspection")).toBeDefined();
    const activeAction = screen.getByRole("button", { name: "Recovery intent ยัง active" }) as HTMLButtonElement;
    expect(activeAction.disabled).toBe(true);
    expect(window.sessionStorage.getItem(`agent-recovery-confirmation:v1:${actorId}:pc-1`)).not.toBeNull();
    expect(fetchSpy.mock.calls.filter(([, request]) => request?.method === "POST")).toHaveLength(1);

    first.unmount();
    render(<AgentUpdateControl actorId={actorId} machine={props} />);
    expect(await screen.findByText("Intent intent-active · request active-request · awaitingInspection")).toBeDefined();
    const remountedAction = screen.getByRole("button", { name: "Recovery intent ยัง active" }) as HTMLButtonElement;
    expect(remountedAction.disabled).toBe(true);
    expect(screen.queryByLabelText("เหตุผล recovery")).toBeNull();
    const callsWhileActive = fetchSpy.mock.calls.length;
    fireEvent.click(remountedAction);
    expect(fetchSpy.mock.calls).toHaveLength(callsWhileActive);

    act(() => {
      vi.setSystemTime(new Date(intent.expiresAtUtc));
      fireEvent.focus(window);
    });
    const refreshContext = await screen.findByRole("button", { name: "ตรวจสอบ held context ใหม่" });
    expect((refreshContext as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(refreshContext);
    expect(await screen.findByLabelText("เลือก target release สำหรับ pc-1")).toBeDefined();
    expect(screen.queryByText("Intent intent-active · request active-request · awaitingInspection")).toBeNull();
    expect(window.sessionStorage.getItem(`agent-recovery-confirmation:v1:${actorId}:pc-1`)).toBeNull();
    expect(fetchSpy.mock.calls.filter(([, request]) => request?.method === "POST")).toHaveLength(1);
    expect(fetchSpy.mock.calls.some(([input]) => String(input).includes("agent-update-command"))).toBe(false);
  });

  it("refreshes held context for a superseded intent even when its recorded expiry is still in the future", async () => {
    const actorId = "admin-superseded-intent";
    const confirmation = {
      intentId: "intent-superseded", machineId: "pc-1", requestId: "request-superseded",
      status: "superseded", held: true, expiresAtUtc: "2026-11-08T12:10:00.000Z",
    };
    window.sessionStorage.setItem(`agent-recovery-confirmation:v1:${actorId}:pc-1`, JSON.stringify(confirmation));
    const context = {
      machineId: "pc-1", chainId: "chain-1", currentHeadOperationId: "operation-4",
      currentGeneration: 4, currentStatus: "failed", currentTargetVersion: "2.0.0", held: true, allocationHeld: false,
    };
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith("agent-update-recovery-context")) return json({ available: true, context });
      if (url === "/api/admin/agent-releases") return json([release("3.0.0", "stable")]);
      throw new Error(`Unexpected request ${url}`);
    });

    render(<AgentUpdateControl actorId={actorId} machine={{ ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4" }} />);
    expect(await screen.findByText("Intent intent-superseded · request request-superseded · superseded")).toBeDefined();
    const refreshContext = screen.getByRole("button", { name: "ตรวจสอบ held context ใหม่" }) as HTMLButtonElement;
    expect(refreshContext.disabled).toBe(false);
    fireEvent.click(refreshContext);

    expect(await screen.findByLabelText("เลือก target release สำหรับ pc-1")).toBeDefined();
    expect(screen.queryByText("Intent intent-superseded · request request-superseded · superseded")).toBeNull();
    expect(window.sessionStorage.getItem(`agent-recovery-confirmation:v1:${actorId}:pc-1`)).toBeNull();
    expect(fetchSpy.mock.calls.filter(([, request]) => request?.method === "POST")).toHaveLength(0);
    expect(fetchSpy.mock.calls.some(([input]) => String(input).includes("agent-update-command"))).toBe(false);
  });

  it("preserves an unresolved exact request while a superseded confirmation is displayed", async () => {
    const actorId = "admin-unresolved-with-terminal-confirmation";
    const request = {
      expectedChainId: "chain-1", expectedHeadId: "operation-4", expectedGeneration: 4,
      targetReleaseVersion: "3.0.0", reason: "Keep the unresolved request intact.", requestId: "request-unresolved",
    };
    window.sessionStorage.setItem(`agent-recovery-attempt:v1:${actorId}:pc-1`, JSON.stringify(request));
    window.sessionStorage.setItem(`agent-recovery-confirmation:v1:${actorId}:pc-1`, JSON.stringify({
      intentId: "intent-superseded", machineId: "pc-1", requestId: "request-superseded",
      status: "superseded", held: true, expiresAtUtc: "2026-11-08T12:10:00.000Z",
    }));
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("agent-update-recovery-intents?requestId=")) return json({ available: false, pending: false }, false, 404);
      throw new Error(`Unexpected request ${url}`);
    });

    render(<AgentUpdateControl actorId={actorId} machine={{ ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "operation-4" }} />);
    expect(await screen.findByText("Request ID: request-unresolved")).toBeDefined();
    expect(screen.getByText("Intent intent-superseded · request request-superseded · superseded")).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "ตรวจสอบ request ID" }));
    expect(await screen.findByText(/No intent was found for this request ID/)).toBeDefined();

    expect(window.sessionStorage.getItem(`agent-recovery-attempt:v1:${actorId}:pc-1`)).toBe(JSON.stringify(request));
    expect(window.sessionStorage.getItem(`agent-recovery-confirmation:v1:${actorId}:pc-1`)).not.toBeNull();
    expect(fetchSpy.mock.calls.filter(([, init]) => init?.method === "POST")).toHaveLength(0);
    expect(screen.queryByLabelText("เหตุผล recovery")).toBeNull();
  });

  it("reports a reservation conflict without displaying a command", async () => {
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => String(input) === "/api/admin/agent-releases" ? json([release("2.0.0", "stable")]) : json({}, false, 409));
    render(<AgentUpdateControl actorId="test-admin" machine={machine} />);
    fireEvent.click(screen.getByRole("button", { name: "Update Agent" }));
    await screen.findByRole("option", { name: "2.0.0 · stable" });
    fireEvent.change(screen.getByLabelText("เลือก release สำหรับ pc-1"), { target: { value: "2.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้างคำสั่งอัปเดต" }));
    expect(await screen.findByText(/สถานะเครื่องหรือ lease อาจเปลี่ยน/)).toBeDefined();
    expect(screen.queryByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1")).toBeNull();
  });

  it("shows a safe retained-package rejection reason in the Admin update status", async () => {
    const reason = "Stored package does not match its published SHA-256.";
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => String(input) === "/api/admin/agent-releases"
      ? json([release("2.0.0", "stable")])
      : json({ available: false, reason }));
    render(<AgentUpdateControl actorId="test-admin" machine={machine} />);
    fireEvent.click(screen.getByRole("button", { name: "Update Agent" }));
    await screen.findByRole("option", { name: "2.0.0 · stable" });
    fireEvent.change(screen.getByLabelText("เลือก release สำหรับ pc-1"), { target: { value: "2.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้างคำสั่งอัปเดต" }));
    expect(await screen.findByText(reason)).toBeDefined();
    expect(screen.queryByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1")).toBeNull();
  });
});
