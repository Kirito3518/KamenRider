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
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(new Date(now));
    clipboard = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: clipboard } });
    fetchSpy = vi.fn(async (input: RequestInfo | URL) => String(input) === "/api/admin/agent-releases"
      ? json([release("2.0.0", "stable"), release("1.0.0", "beta")])
      : json({ available: true, command: "full command including ticket", operationId: "op-1", expiresAtUtc: "2026-11-08T12:10:00.000Z", targetVersion: "2.0.0" }));
    vi.stubGlobal("fetch", fetchSpy);
  });
  afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); Reflect.deleteProperty(navigator, "clipboard"); });

  it("disables a held lease but permits an unready machine without one", () => {
    const { rerender } = render(<AgentUpdateControl machine={{ ...machine, allocationHeld: true }} />);
    expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/มี lease/)).toBeDefined();
    rerender(<AgentUpdateControl machine={machine} />);
    expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled).toBe(false);
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it.each(Object.entries(updateStatusLabels))("renders %s and keeps active operation locks", (status, label) => {
    render(<AgentUpdateControl machine={{ ...machine, agentUpdateStatus: status, agentUpdateOperationId: "op-1" }} />);
    expect(screen.getByText(`สถานะอัปเดต: ${label}`)).toBeDefined();
    expect((screen.getByRole("button", { name: "Update Agent" }) as HTMLButtonElement).disabled)
      .toBe(!["completed", "rolledBack", "expired"].includes(status));
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("mints only after selecting a release, shows the whole command, and copies it", async () => {
    render(<AgentUpdateControl machine={machine} />);
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
    let finishRefresh!: () => void;
    const onUpdated = vi.fn(() => new Promise<void>((resolve) => { finishRefresh = resolve; }));
    const previous = { ...machine, agentUpdateStatus: status, agentUpdateOperationId: "previous-operation", agentUpdateTargetVersion: "1.0.0" };
    const { rerender } = render(<AgentUpdateControl machine={previous} onUpdated={onUpdated} />);
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
    await act(async () => { finishRefresh(); });
    expect(updateButton.disabled).toBe(true);
    rerender(<AgentUpdateControl machine={{ ...previous }} onUpdated={onUpdated} />);
    expect(updateButton.disabled).toBe(true);
    expect(screen.getByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1")).toHaveProperty("value", "full command including ticket");
    expect(screen.getByText(/สถานะอัปเดต: รอเรียกใช้ตัวติดตั้ง.*เป้าหมาย 2.0.0/)).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "คัดลอกคำสั่งอัปเดต" }));
    await waitFor(() => expect(clipboard).toHaveBeenCalledWith("full command including ticket"));
    rerender(<AgentUpdateControl machine={{ ...machine, agentUpdateStatus: "completed", agentUpdateOperationId: "op-1", agentVersion: "2.0.0" }} onUpdated={onUpdated} />);
    expect(updateButton.disabled).toBe(false);
  });

  it("keeps the issued command and local lock when the refresh callback rejects", async () => {
    const onUpdated = vi.fn().mockRejectedValue(new Error("Fixture refresh failed"));
    render(<AgentUpdateControl machine={{ ...machine, agentUpdateStatus: "completed", agentUpdateOperationId: "old-operation" }} onUpdated={onUpdated} />);
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
    const { rerender } = render(<AgentUpdateControl machine={machine} />);
    fireEvent.click(screen.getByRole("button", { name: "Update Agent" }));
    await screen.findByRole("option", { name: "2.0.0 · stable" });
    fireEvent.change(screen.getByLabelText("เลือก release สำหรับ pc-1"), { target: { value: "2.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้างคำสั่งอัปเดต" }));
    await screen.findByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1");
    rerender(<AgentUpdateControl machine={{ ...machine, agentUpdateStatus: "completed", agentUpdateOperationId: "op-1", agentVersion: "2.0.0", agentUpdateTargetVersion: "2.0.0" }} />);
    expect(screen.getByText(/สถานะอัปเดต: อัปเดตสำเร็จและยืนยันแล้ว/)).toBeDefined();
    const laterOperation = { ...machine, agentVersion: "2.0.0", agentUpdateStatus: status, agentUpdateOperationId: "op-2", agentUpdateTargetVersion: "3.0.0" };
    rerender(<AgentUpdateControl machine={laterOperation} />);
    expect(screen.getByText(`สถานะอัปเดต: ${updateStatusLabels[status]} · เป้าหมาย 3.0.0`)).toBeDefined();
    expect(screen.queryByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1")).toBeNull();
    const recovery = screen.getByRole("button", { name: "กู้คืนการอัปเดต" }) as HTMLButtonElement;
    expect(recovery.disabled).toBe(false);
    rerender(<AgentUpdateControl machine={{ ...laterOperation }} />);
    expect(screen.getByText(`สถานะอัปเดต: ${updateStatusLabels[status]} · เป้าหมาย 3.0.0`)).toBeDefined();
    fetchSpy.mockResolvedValueOnce(json({ available: true, command: "op-2 recovery command", operationId: "op-2", targetVersion: "3.0.0", expiresAtUtc: "2026-11-08T12:10:00.000Z" }));
    fireEvent.click(recovery);
    expect(await screen.findByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1")).toHaveProperty("value", "op-2 recovery command");
    expect(JSON.parse(String(fetchSpy.mock.calls.at(-1)?.[1]?.body))).toEqual({ operationId: "op-2" });
  });

  it("recovers the existing failed operation using its operation ID", async () => {
    render(<AgentUpdateControl machine={{ ...machine, agentUpdateStatus: "failed", agentUpdateOperationId: "op-1", agentUpdateTargetVersion: "2.0.0" }} />);
    expect(screen.getByText(/ล้มเหลว/)).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "กู้คืนการอัปเดต" }));
    await screen.findByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1");
    expect(JSON.parse(String(fetchSpy.mock.calls[0][1]?.body))).toEqual({ operationId: "op-1" });
  });

  it("disables copying at expiry without issuing a replacement ticket", async () => {
    render(<AgentUpdateControl machine={{ ...machine, agentUpdateStatus: "installing", agentUpdateOperationId: "op-1" }} />);
    fireEvent.click(screen.getByRole("button", { name: "กู้คืนการอัปเดต" }));
    await screen.findByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1");
    act(() => {
      vi.setSystemTime(new Date("2026-11-08T12:10:00.000Z"));
      fireEvent.focus(window);
    });
    await waitFor(() => expect((screen.getByRole("button", { name: "คัดลอกคำสั่งอัปเดต" }) as HTMLButtonElement).disabled).toBe(true));
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it("reports a reservation conflict without displaying a command", async () => {
    fetchSpy.mockImplementation(async (input: RequestInfo | URL) => String(input) === "/api/admin/agent-releases" ? json([release("2.0.0", "stable")]) : json({}, false, 409));
    render(<AgentUpdateControl machine={machine} />);
    fireEvent.click(screen.getByRole("button", { name: "Update Agent" }));
    await screen.findByRole("option", { name: "2.0.0 · stable" });
    fireEvent.change(screen.getByLabelText("เลือก release สำหรับ pc-1"), { target: { value: "2.0.0" } });
    fireEvent.click(screen.getByRole("button", { name: "สร้างคำสั่งอัปเดต" }));
    expect(await screen.findByText(/สถานะเครื่องหรือ lease อาจเปลี่ยน/)).toBeDefined();
    expect(screen.queryByLabelText("คำสั่งอัปเดต Agent สำหรับ pc-1")).toBeNull();
  });
});
