import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MachineCard } from "@/components/rent/machine-card";
import type { RentalMachine } from "@/lib/rental-machines";

const MACHINE: RentalMachine = {
  id: "machine-1",
  name: "DESKTOP-GAME",
  cpu: "AMD Ryzen 7 7800X3D",
  gpu: "NVIDIA GeForce RTX 4080",
  ram: 32,
  ramBytes: 34_359_738_368,
  storage: "—",
  price: 40,
  status: "available",
  game: "",
};

describe("customer machine card specs", () => {
  afterEach(cleanup);

  it("shows CPU, การ์ดจอ and แรม labels with the Agent-reported specs", () => {
    render(<MachineCard machine={MACHINE} onSelect={vi.fn()} />);

    expect(screen.getByText("DESKTOP-GAME")).toBeDefined();
    expect(screen.getByText("CPU")).toBeDefined();
    expect(screen.getByText("การ์ดจอ")).toBeDefined();
    expect(screen.getByText("แรม")).toBeDefined();
    expect(screen.getByText("AMD Ryzen 7 7800X3D")).toBeDefined();
    expect(screen.getByText("NVIDIA GeForce RTX 4080")).toBeDefined();
    expect(screen.getByText("32 GB")).toBeDefined();
    expect(
      screen.getByRole("button", { name: "เลือกเช่าเครื่อง DESKTOP-GAME" }),
    ).toBeDefined();
  });

  it("uses placeholders when the Agent has not reported hardware", () => {
    render(
      <MachineCard
        machine={{
          ...MACHINE,
          cpu: "",
          gpu: "",
          ramBytes: 0,
          ram: 0,
          status: "busy",
        }}
        onSelect={vi.fn()}
      />,
    );

    expect(screen.getAllByText("—")).toHaveLength(3);
  });
});
