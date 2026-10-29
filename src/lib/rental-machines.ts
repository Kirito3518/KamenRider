export type MachineStatus =
  | "available"
  | "busy"
  | "maintenance"
  | "notReady"
  | "unknown"
  | "unreachable"
  | "ready"
  | "preparing"
  | "pending"
  | "ending";
export type MachineFilter = "all" | "available" | "unavailable";
export type MachineSort = "name" | "price-asc" | "price-desc";

export type RentalMachine = {
  id: string;
  cpu: string;
  gpu: string;
  ram: number;
  storage: string;
  price: number;
  status: MachineStatus;
  game: string;
};

export const machineStatusLabels: Record<MachineStatus, string> = {
  notReady: "ยังไม่พร้อม",
  unknown: "ยังไม่ยืนยันความพร้อม",
  unreachable: "ติดต่อไม่ได้",
  ready: "พร้อม (ทดลอง)",
  preparing: "กำลังเตรียม",
  pending: "รออนุมัติ",
  ending: "กำลังสิ้นสุด",
  available: "ว่าง",
  busy: "กำลังใช้งาน",
  maintenance: "ซ่อม",
};

export function filterMachines(
  machines: RentalMachine[],
  filter: MachineFilter,
  sort: MachineSort,
) {
  return machines
    .filter((machine) => {
      const matchesStatus =
        filter === "all" ||
        (filter === "available"
          ? machine.status === "available"
          : machine.status !== "available");
      return matchesStatus;
    })
    .sort((a, b) =>
      sort === "price-asc"
        ? a.price - b.price
        : sort === "price-desc"
          ? b.price - a.price
          : a.id.localeCompare(b.id),
    );
}
