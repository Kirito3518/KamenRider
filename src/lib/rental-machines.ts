export type MachineStatus = "available" | "busy" | "maintenance";
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

// Sample inventory for the frontend preview; no machine availability or billing API is connected.
export const rentalMachines: RentalMachine[] = [
  { id: "PC-01", cpu: "Intel Core i5-13400F", gpu: "RTX 4060", ram: 16, storage: "SSD 500 GB", price: 25, status: "available", game: "Counter-Strike 2" },
  { id: "PC-02", cpu: "Intel Core i5-14400F", gpu: "RTX 4070", ram: 32, storage: "SSD 1 TB", price: 35, status: "available", game: "Cyberpunk 2077" },
  { id: "PC-03", cpu: "AMD Ryzen 5 5600", gpu: "RTX 3060", ram: 16, storage: "SSD 500 GB", price: 20, status: "busy", game: "PUBG: BATTLEGROUNDS" },
  { id: "PC-04", cpu: "AMD Ryzen 7 7800X3D", gpu: "RTX 4080", ram: 32, storage: "SSD 1 TB", price: 55, status: "available", game: "ELDEN RING" },
  { id: "PC-05", cpu: "Intel Core i7-12700F", gpu: "RTX 3070", ram: 32, storage: "SSD 1 TB", price: 30, status: "available", game: "Apex Legends" },
  { id: "PC-06", cpu: "Intel Core i9-13900K", gpu: "RTX 4090", ram: 64, storage: "SSD 2 TB", price: 75, status: "busy", game: "Grand Theft Auto V" },
  { id: "PC-07", cpu: "AMD Ryzen 5 7500F", gpu: "RTX 4060 Ti", ram: 32, storage: "SSD 1 TB", price: 28, status: "maintenance", game: "Dota 2" },
  { id: "PC-08", cpu: "Intel Core i7-12700F", gpu: "RTX 3080", ram: 32, storage: "SSD 1 TB", price: 32, status: "available", game: "Rust" },
  { id: "PC-09", cpu: "AMD Ryzen 7 7700", gpu: "RTX 4070 SUPER", ram: 32, storage: "SSD 1 TB", price: 40, status: "available", game: "Counter-Strike 2" },
  { id: "PC-10", cpu: "Intel Core i5-10400F", gpu: "RTX 2060", ram: 16, storage: "SSD 500 GB", price: 18, status: "busy", game: "Apex Legends" },
];

export const machineStatusLabels: Record<MachineStatus, string> = {
  available: "ว่าง",
  busy: "กำลังใช้งาน",
  maintenance: "ซ่อม",
};

export function filterMachines(machines: RentalMachine[], filter: MachineFilter, sort: MachineSort) {
  return machines.filter(machine => {
    const matchesStatus = filter === "all" || (filter === "available" ? machine.status === "available" : machine.status !== "available");
    return matchesStatus;
  }).sort((a, b) => sort === "price-asc" ? a.price - b.price : sort === "price-desc" ? b.price - a.price : a.id.localeCompare(b.id));
}
