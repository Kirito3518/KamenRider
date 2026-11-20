import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RentalDialog } from "@/components/rent/rental-dialog";
import type { RentalMachine } from "@/lib/rental-machines";

const h = vi.hoisted(() => ({
  session: null as null | { user: { id: string; balanceSatang: number } },
  push: vi.fn(),
  success: vi.fn(),
  refetch: vi.fn(async () => ({ data: null })),
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: h.push, refresh: vi.fn() }),
}));

vi.mock("@/lib/auth-client", () => ({
  authClient: {
    useSession: () => ({ data: h.session, isPending: false, refetch: h.refetch }),
  },
}));

vi.mock("react-hot-toast", () => ({
  default: { success: h.success, error: vi.fn() },
}));

const MACHINE: RentalMachine = {
  id: "pc-1",
  name: "PC ONE",
  cpu: "AMD Ryzen 7",
  gpu: "NVIDIA RTX 4070",
  ram: 32,
  ramBytes: 34_359_738_368,
  storage: "—",
  priceSatang: 1_000,
  status: "available",
  game: "",
};

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function renderDialog(onClose = vi.fn()) {
  return render(
    <>
      <RentalDialog
        machine={MACHINE}
        onClose={onClose}
        returnFocusRef={{ current: null }}
      />
    </>,
  );
}

describe("RentalDialog", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    h.session = null;
    h.push.mockReset();
    h.success.mockReset();
    fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("asks guests to sign in instead of showing the payment form", () => {
    renderDialog();

    expect(screen.getByText("กรุณาเข้าสู่ระบบก่อนทำการเช่า")).toBeDefined();
    expect(screen.getByText("เข้าสู่ระบบ / สมัครสมาชิก").getAttribute("href")).toBe("/login");
    expect(screen.queryByRole("button", { name: "ยืนยันการเช่า" })).toBeNull();
  });

  it("shows the whole-baht rate, balance and the total for the selected hours", async () => {
    h.session = { user: { id: "renter-1", balanceSatang: 100_000 } };
    renderDialog();

    expect(screen.getByText("฿10.00 / ชั่วโมง")).toBeDefined();
    expect(screen.getByText("฿1,000.00")).toBeDefined();

    fireEvent.change(screen.getByLabelText("ระยะเวลาเช่า (ชั่วโมง)"), {
      target: { value: "3" },
    });
    expect(await screen.findByText("฿30.00")).toBeDefined();
  });

  it("blocks the purchase when the balance is insufficient", () => {
    h.session = { user: { id: "renter-1", balanceSatang: 500 } };
    renderDialog();

    expect(screen.getByText("ยอดเงินไม่เพียงพอ")).toBeDefined();
    const submit = screen.getByRole("button", { name: "ยืนยันการเช่า" }) as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
  });

  it("creates the rental and navigates to my machines", async () => {
    h.session = { user: { id: "renter-1", balanceSatang: 100_000 } };
    fetchSpy.mockResolvedValueOnce(json({ rentalId: "rental-1", state: "preparing", balanceSatang: 98_000 }));
    const onClose = vi.fn();
    renderDialog(onClose);

    fireEvent.click(screen.getByRole("button", { name: "ยืนยันการเช่า" }));

    await waitFor(() => expect(h.push).toHaveBeenCalledWith("/my-machines"));
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/rentals");
    const body = JSON.parse(String(init?.body));
    expect(body.machineId).toBe("pc-1");
    expect(body.hours).toBe(1);
    expect(body.requestId).toMatch(/^[0-9a-f-]{36}$/);
    expect(onClose).toHaveBeenCalled();
    expect(h.success).toHaveBeenCalledWith("ส่งคำขอเช่าแล้ว กำลังเตรียมเครื่องให้คุณ");
  });

  it("shows the server's refusal message and stays on the dialog", async () => {
    h.session = { user: { id: "renter-1", balanceSatang: 100_000 } };
    fetchSpy.mockResolvedValueOnce(json({ error: "เครื่องนี้ไม่พร้อมให้เช่าในขณะนี้" }, 409));
    renderDialog();

    fireEvent.click(screen.getByRole("button", { name: "ยืนยันการเช่า" }));

    expect(await screen.findByText("เครื่องนี้ไม่พร้อมให้เช่าในขณะนี้")).toBeDefined();
    expect(h.push).not.toHaveBeenCalled();
  });
});
