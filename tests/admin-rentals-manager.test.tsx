import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RentalsManager } from "@/components/admin/rentals-manager";

const DATA = {
  controllerAvailable: true,
  summary: {
    totalRentals: 2,
    activeRentals: 1,
    paidSatang: 12_000,
    refundedSatang: 2_000,
    netSatang: 10_000,
  },
  entries: [
    {
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
    },
    {
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
    },
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

  it("shows stats, renters and rental money in the admin history", async () => {
    render(<RentalsManager />);

    expect(await screen.findByText("Alice")).toBeDefined();
    expect(screen.getByText("Bob")).toBeDefined();
    expect(screen.getByText("alice@example.com")).toBeDefined();
    expect(screen.getByText("PC ONE")).toBeDefined();
    expect(screen.getAllByText("กำลังเช่า").length).toBeGreaterThan(0);
    expect(screen.getAllByText("สิ้นสุดแล้ว").length).toBeGreaterThan(0);
    expect(screen.getByText("฿100.00")).toBeDefined();
    expect(screen.getAllByText("฿80.00").length).toBeGreaterThan(0);
    expect(screen.getAllByText("฿20.00").length).toBeGreaterThan(0);
    expect(fetchSpy).toHaveBeenCalledWith("/api/admin/rentals", { cache: "no-store" });
  });

  it("filters rows by the search box", async () => {
    render(<RentalsManager />);
    await screen.findByText("Alice");

    fireEvent.change(screen.getByLabelText("ค้นหาประวัติการเช่า"), {
      target: { value: "bob" },
    });

    expect(screen.queryByText("Alice")).toBeNull();
    expect(screen.getByText("Bob")).toBeDefined();
    expect(screen.getByText("แสดง 1 จาก 2 รายการ")).toBeDefined();
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
        controllerAvailable: true,
        summary: { totalRentals: 0, activeRentals: 0, paidSatang: 0, refundedSatang: 0, netSatang: 0 },
        entries: [],
      }),
    );
    render(<RentalsManager />);

    expect(await screen.findByText("ยังไม่มีประวัติการเช่า")).toBeDefined();
  });
});
