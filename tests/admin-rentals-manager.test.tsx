import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RentalsManager } from "@/components/admin/rentals-manager";

const ENTRY_1 = {
  rentalId: "r1",
  userId: "user-1",
  userName: "Alice",
  userEmail: "alice@example.com",
  machineId: "pc-1",
  machineName: "PC ONE",
  state: "active",
  startsAtUtc: "2026-11-18T03:00:00.000Z",
  expiresAtUtc: "2026-11-18T05:00:00.000Z",
  endedAtUtc: null,
  createdAtUtc: "2026-11-18T02:59:00.000Z",
  bookedSeconds: 7_200,
  extensionSeconds: 0,
  hourlyPriceSatang: 4_000,
  paidSatang: 8_000,
  refundedSatang: 0,
  netSatang: 8_000,
  refundNote: null,
  rejectReason: null,
};

const ENTRY_2 = {
  rentalId: "r2",
  userId: "user-2",
  userName: "Bob",
  userEmail: "bob@example.com",
  machineId: "pc-2",
  machineName: "PC TWO",
  state: "completed",
  startsAtUtc: "2026-11-17T03:00:00.000Z",
  expiresAtUtc: "2026-11-17T04:00:00.000Z",
  endedAtUtc: "2026-11-17T03:30:00.000Z",
  createdAtUtc: "2026-11-17T02:59:00.000Z",
  bookedSeconds: 3_600,
  extensionSeconds: 0,
  hourlyPriceSatang: 4_000,
  paidSatang: 4_000,
  refundedSatang: 2_000,
  netSatang: 2_000,
  refundNote: "คืนตามเวลาที่ไม่ได้ใช้",
  rejectReason: null,
};

const DATA = {
  controllerAvailable: true,
  summary: {
    totalRentals: 2,
    activeRentals: 1,
    paidSatang: 12_000,
    refundedSatang: 2_000,
    netSatang: 10_000,
  },
  stats: {
    totalRentals: 2,
    activeRentals: 1,
    completedRentals: 1,
    expiredRentals: 0,
    revenueSatang: 10_000,
    rentalCount: 2,
  },
  previous: {
    totalRentals: 1,
    activeRentals: 0,
    completedRentals: 1,
    expiredRentals: 0,
    revenueSatang: 5_000,
    rentalCount: 1,
  },
  chart: [
    { key: "2026-11-17", label: "17 พ.ย.", revenueSatang: 4_000, count: 1 },
    { key: "2026-11-18", label: "18 พ.ย.", revenueSatang: 6_000, count: 1 },
  ],
  chartPrevious: { revenueSatang: 5_000, count: 1 },
  entries: [ENTRY_1, ENTRY_2],
  pagination: { page: 1, pageSize: 20, total: 2, pageCount: 1 },
  filters: {
    q: "",
    state: "all",
    machineId: "all",
    period: "30d",
    from: "",
    to: "",
    sort: "newest",
  },
  rangeError: "",
  machines: [
    { machineId: "pc-1", displayName: "PC ONE" },
    { machineId: "pc-2", displayName: "PC TWO" },
  ],
};

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("RentalsManager", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchSpy = vi.fn(async () => json(DATA));
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("shows stats, charts and renter rows", async () => {
    render(<RentalsManager />);

    expect(await screen.findByText("Alice")).toBeDefined();
    expect(screen.getByText("Bob")).toBeDefined();
    expect(screen.getByText("alice@example.com")).toBeDefined();
    expect(screen.getByText("PC ONE")).toBeDefined();
    expect(screen.getAllByText("กำลังเช่า").length).toBeGreaterThan(0);
    expect(screen.getAllByText("เสร็จสิ้น").length).toBeGreaterThan(0);
    expect(screen.getByText("รายได้สุทธิในช่วง")).toBeDefined();
    expect(screen.getByText("รายได้จากการเช่าตามช่วงเวลา")).toBeDefined();
    expect(String(fetchSpy.mock.calls[0]?.[0])).toContain("/api/admin/rentals?");
    expect(String(fetchSpy.mock.calls[0]?.[0])).toContain("period=30d");
  });

  it("sends search and status filters to the API", async () => {
    render(<RentalsManager />);
    await screen.findByText("Alice");

    fireEvent.change(screen.getByLabelText("ค้นหาประวัติการเช่า"), {
      target: { value: "bob" },
    });
    fireEvent.click(screen.getByLabelText("ค้นหา"));

    await waitFor(() => {
      const urls = fetchSpy.mock.calls.map((call) => String(call[0]));
      expect(urls.some((url) => url.includes("q=bob"))).toBe(true);
    });
  });

  it("opens a rental detail dialog", async () => {
    render(<RentalsManager />);
    await screen.findByText("Alice");
    fireEvent.click(screen.getByLabelText("ดูรายละเอียด r1"));
    expect(await screen.findByText("รหัสรายการ r1")).toBeDefined();
    expect(screen.getAllByText("ผู้เช่า").length).toBeGreaterThan(0);
  });

  it("warns when the Controller is unavailable and still shows payment rows", async () => {
    fetchSpy.mockResolvedValueOnce(json({ ...DATA, controllerAvailable: false }));
    render(<RentalsManager />);

    expect(await screen.findByText("ติดต่อระบบเครื่องไม่ได้")).toBeDefined();
    expect(screen.getByText("Alice")).toBeDefined();
  });

  it("shows the empty state", async () => {
    fetchSpy.mockResolvedValueOnce(
      json({
        ...DATA,
        entries: [],
        pagination: { page: 1, pageSize: 20, total: 0, pageCount: 1 },
        stats: {
          totalRentals: 0,
          activeRentals: 0,
          completedRentals: 0,
          expiredRentals: 0,
          revenueSatang: 0,
          rentalCount: 0,
        },
      }),
    );
    render(<RentalsManager />);

    expect(await screen.findByText("ยังไม่มีประวัติการเช่า")).toBeDefined();
  });
});
