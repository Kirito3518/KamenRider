import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RentalHistory } from "@/components/my-rentals/rental-history";

vi.mock("@/components/ui/layout/Navbar", () => ({ Navbar: () => null }));
vi.mock("@/components/ui/layout/Footer", () => ({ Footer: () => null }));

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
    },
  ],
};

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("RentalHistory", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchSpy = vi.fn(async () => json(DATA));
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders the renter's rentals with money and state labels", async () => {
    render(<RentalHistory />);

    expect(await screen.findByText("PC ONE")).toBeDefined();
    expect(screen.getByText("PC TWO")).toBeDefined();
    expect(screen.getAllByText("กำลังเช่า").length).toBeGreaterThan(0);
    expect(screen.getAllByText("สิ้นสุดแล้ว").length).toBeGreaterThan(0);
    expect(screen.getByText("฿120.00")).toBeDefined();
    expect(screen.getAllByText("฿20.00").length).toBeGreaterThan(0);
    expect(screen.getByText("฿100.00")).toBeDefined();
    expect(fetchSpy).toHaveBeenCalledWith("/api/rentals", { cache: "no-store" });
  });

  it("shows the empty state when nothing has been rented yet", async () => {
    fetchSpy.mockResolvedValueOnce(
      json({
        balanceSatang: 0,
        controllerAvailable: true,
        summary: { totalRentals: 0, activeRentals: 0, paidSatang: 0, refundedSatang: 0, netSatang: 0 },
        entries: [],
      }),
    );
    render(<RentalHistory />);

    expect(await screen.findByText("ยังไม่มีประวัติการเช่า")).toBeDefined();
    expect(screen.getByText("ดูเครื่องที่เปิดให้เช่า").getAttribute("href")).toBe("/rent");
  });

  it("warns when only payment records are available", async () => {
    fetchSpy.mockResolvedValueOnce(
      json({ ...DATA, controllerAvailable: false }),
    );
    render(<RentalHistory />);

    expect(await screen.findByText("แสดงข้อมูลการชำระเงินเท่านั้น")).toBeDefined();
  });

  it("shows the server error message", async () => {
    fetchSpy.mockResolvedValueOnce(json({ error: "กรุณาเข้าสู่ระบบ" }, 401));
    render(<RentalHistory />);

    await waitFor(() => expect(screen.getByText("กรุณาเข้าสู่ระบบ")).toBeDefined());
  });
});
