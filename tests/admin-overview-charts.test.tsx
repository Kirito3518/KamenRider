import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { MachineAvailabilityChart } from "@/components/admin/machine-availability-chart";
import { RentalCharts } from "@/components/admin/rental-charts";
import type { Machine } from "@/lib/machine-contracts";
import { summarizeMachines } from "@/lib/machine-summary";

const NOW = Date.parse("2026-11-22T10:00:00.000Z");

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
  readiness: "ready",
  canRent: false,
  allocationHeld: false,
  lastSeenAt: new Date(NOW - 5_000).toISOString(),
  agentVersion: "1.0.0",
  activeAdminVncSessions: 0,
  readinessDiagnostics: null,
};

function machine(overrides: Partial<Machine> & Pick<Machine, "machineId">): Machine {
  return { ...baseMachine, ...overrides } as Machine;
}

const CHART = [
  { key: "2026-11-20", label: "20 พ.ย.", revenueSatang: 12_000, count: 2 },
  { key: "2026-11-21", label: "21 พ.ย.", revenueSatang: 8_000, count: 1 },
];

const CURRENT = {
  revenueSatang: 20_000,
  rentalCount: 3,
  paidSatang: 24_000,
  refundedSatang: 4_000,
};
const PREVIOUS = {
  revenueSatang: 10_000,
  rentalCount: 1,
  paidSatang: 10_000,
  refundedSatang: 0,
};

afterEach(() => {
  cleanup();
});

describe("RentalCharts", () => {
  it("renders the revenue and count cards with their accessible chart regions", () => {
    render(
      <RentalCharts
        chart={CHART}
        chartPrevious={{ revenueSatang: 10_000, count: 1 }}
        current={CURRENT}
        previous={PREVIOUS}
      />,
    );
    expect(screen.getByText("รายได้จากการเช่าตามช่วงเวลา")).toBeDefined();
    expect(screen.getByText("จำนวนรายการเช่าตามช่วงเวลา")).toBeDefined();
    expect(screen.getByRole("region", { name: "กราฟรายได้จากการเช่า" })).toBeDefined();
    expect(screen.getByRole("region", { name: "กราฟจำนวนรายการเช่า" })).toBeDefined();
    expect(screen.getByText(/\+100\.0% เทียบช่วงก่อนหน้า/)).toBeDefined();
  });

  it("shows per-chart empty states when the range has no payments", () => {
    render(
      <RentalCharts
        chart={[
          { key: "2026-11-20", label: "20 พ.ย.", revenueSatang: 0, count: 0 },
          { key: "2026-11-21", label: "21 พ.ย.", revenueSatang: 0, count: 0 },
        ]}
        chartPrevious={{ revenueSatang: 0, count: 0 }}
        current={{ revenueSatang: 0, rentalCount: 0 }}
        previous={{ revenueSatang: 0, rentalCount: 0 }}
      />,
    );
    expect(screen.getByText("ยังไม่มีข้อมูลรายได้ในช่วงที่เลือก")).toBeDefined();
    expect(screen.getByText("ยังไม่มีข้อมูลรายการในช่วงที่เลือก")).toBeDefined();
    expect(screen.queryByRole("region", { name: "กราฟรายได้จากการเช่า" })).toBeNull();
  });
});

describe("MachineAvailabilityChart", () => {
  it("shows a Controller fallback instead of stale machine data", () => {
    render(
      <MachineAvailabilityChart summary={summarizeMachines([], NOW)} available={false} />,
    );
    expect(screen.getByText("ติดต่อระบบเครื่องไม่ได้")).toBeDefined();
    expect(screen.getByText(/ไม่สามารถแสดงสถานะเครื่องจาก Controller ได้ในขณะนี้/)).toBeDefined();
    expect(screen.queryByRole("region", { name: "กราฟสัดส่วนสถานะเครื่อง" })).toBeNull();
  });

  it("renders a donut with an sr-only summary that excludes pending approvals", () => {
    const summary = summarizeMachines(
      [
        machine({ machineId: "rentable", canRent: true, operationalState: "available" }),
        machine({ machineId: "busy", operationalState: "busy" }),
        machine({ machineId: "pending", approvalStatus: "pending" }),
      ],
      NOW,
    );
    render(<MachineAvailabilityChart summary={summary} available />);
    expect(screen.getByRole("region", { name: "กราฟสัดส่วนสถานะเครื่อง" })).toBeDefined();
    const srOnly = screen.getByText(/สรุปสถานะเครื่อง:/);
    expect(srOnly.textContent).toContain("พร้อมให้เช่า 1 เครื่อง");
    expect(srOnly.textContent).toContain("กำลังใช้งาน 1 เครื่อง");
    expect(srOnly.textContent).toContain("รวม 2 เครื่อง");
    expect(srOnly.textContent).toContain("รออนุมัติ 1 เครื่อง");
    expect(screen.getByText(/รออนุมัติ 1 เครื่อง \(ไม่รวมในสัดส่วน\)/)).toBeDefined();
  });

  it("shows an empty state when no approved machines exist yet", () => {
    const summary = summarizeMachines([machine({ machineId: "p", approvalStatus: "pending" })], NOW);
    render(<MachineAvailabilityChart summary={summary} available />);
    expect(screen.getByText("ยังไม่มีเครื่องที่อนุมัติให้เช่า")).toBeDefined();
    expect(screen.getByText(/สรุปสถานะเครื่อง:/).textContent).toContain("รวม 0 เครื่อง");
    expect(screen.queryByRole("region", { name: "กราฟสัดส่วนสถานะเครื่อง" })).toBeNull();
  });
});
