import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RentalHistoryPanel } from "@/components/history/rental-history-panel";

const DATA = {
  balanceSatang: 986_666,
  controllerAvailable: true,
  summary: {
    totalRentals: 2,
    activeRentals: 1,
    paidSatang: 12_000,
    refundedSatang: 2_000,
    netSatang: 10_000,
  },
  pagination: { page: 1, pageSize: 20, total: 2, pageCount: 1 },
  filters: {
    q: "",
    state: "all",
    machineId: "all",
    period: "all",
    from: "",
    to: "",
    sort: "newest",
  },
  rangeError: "",
  machines: [
    { machineId: "pc-1", displayName: "PC ONE" },
    { machineId: "pc-2", displayName: "PC TWO" },
  ],
  entries: [
    {
      rentalId: "r1",
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
    },
    {
      rentalId: "r2",
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
    },
  ],
};

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("RentalHistoryPanel", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchSpy = vi.fn(async () => json(DATA));
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders rentals in a table with money and derived states", async () => {
    render(<RentalHistoryPanel />);

    expect(await screen.findByText("PC ONE")).toBeDefined();
    expect(screen.getByText("PC TWO")).toBeDefined();
    expect(screen.getAllByText("กำลังเช่า").length).toBeGreaterThan(0);
    expect(screen.getAllByText("เสร็จสิ้น").length).toBeGreaterThan(0);
    expect(screen.getByText("฿120.00")).toBeDefined();
    expect(screen.getAllByText("฿80.00").length).toBeGreaterThan(0);
    expect(screen.getAllByText("฿20.00").length).toBeGreaterThan(0);
    expect(screen.getByText(/พบ 2 รายการ/)).toBeDefined();
    expect(String(fetchSpy.mock.calls[0]?.[0])).toContain("/api/rentals?");
    expect(String(fetchSpy.mock.calls[0]?.[0])).toContain("period=all");
  });

  it("sends the search query to the API", async () => {
    render(<RentalHistoryPanel />);
    await screen.findByText("PC ONE");

    fireEvent.change(screen.getByLabelText("ค้นหาประวัติการเช่า"), {
      target: { value: "PC TWO" },
    });
    fireEvent.click(screen.getByLabelText("ค้นหา"));

    await waitFor(() => {
      const urls = fetchSpy.mock.calls.map((call) => String(call[0]));
      expect(urls.some((url) => url.includes("q=PC+TWO") || url.includes("q=PC%20TWO"))).toBe(
        true,
      );
    });
  });

  it("opens the rental detail dialog", async () => {
    render(<RentalHistoryPanel />);
    await screen.findByText("PC ONE");
    fireEvent.click(screen.getByLabelText("ดูรายละเอียด PC ONE"));
    expect(await screen.findByText("รหัสรายการ r1")).toBeDefined();
  });

  it("shows the empty state when nothing has been rented", async () => {
    fetchSpy.mockResolvedValueOnce(
      json({
        ...DATA,
        entries: [],
        pagination: { page: 1, pageSize: 20, total: 0, pageCount: 1 },
        summary: {
          totalRentals: 0,
          activeRentals: 0,
          paidSatang: 0,
          refundedSatang: 0,
          netSatang: 0,
        },
      }),
    );
    render(<RentalHistoryPanel />);

    expect(await screen.findByText("ยังไม่มีประวัติการเช่า")).toBeDefined();
    expect(screen.getByText("ดูเครื่องที่เปิดให้เช่า").getAttribute("href")).toBe("/rent");
  });

  it("warns when only payment records are available", async () => {
    fetchSpy.mockResolvedValueOnce(json({ ...DATA, controllerAvailable: false }));
    render(<RentalHistoryPanel />);

    expect(await screen.findByText("ติดต่อระบบเครื่องไม่ได้")).toBeDefined();
  });

  it("shows the server error message", async () => {
    fetchSpy.mockResolvedValueOnce(json({ error: "กรุณาเข้าสู่ระบบ" }, 401));
    render(<RentalHistoryPanel />);

    expect(await screen.findByText("กรุณาเข้าสู่ระบบ")).toBeDefined();
  });
});
