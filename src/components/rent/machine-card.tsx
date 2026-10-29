import { ArrowUpRight, Cpu, Gpu, MemoryStick, Monitor } from "lucide-react";
import {
  Card,
  CardContent,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { machineStatusLabels, type RentalMachine } from "@/lib/rental-machines";

export function MachineCard({
  machine,
  onSelect,
}: {
  machine: RentalMachine;
  onSelect: (machine: RentalMachine, trigger: HTMLButtonElement) => void;
}) {
  const available = machine.status === "available";

  return (
    <Card
      size="sm"
      className="gap-5 rounded-3xl shadow-none"
      data-machine-id={machine.id}
    >
      <CardHeader className="gap-4">
        <div className="flex items-center justify-between gap-2">
          <span className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-primary/10 text-primary">
            <Monitor className="size-5" strokeWidth={1.7} />
          </span>
          <Badge variant={available ? "available" : "secondary"}>
            <span className="size-1.5 rounded-full bg-current" />
            {machineStatusLabels[machine.status]}
          </Badge>
        </div>
        <CardTitle>
          <h2>{machine.id}</h2>
        </CardTitle>
      </CardHeader>
      <CardContent>
        <dl className="flex flex-col gap-2.5 text-xs sm:text-sm">
          <div className="flex items-center gap-2">
            <Cpu className="size-4 shrink-0 text-muted-foreground" />
            <dt className="sr-only">CPU</dt>
            <dd className="truncate">{machine.cpu}</dd>
          </div>
          <div className="flex items-center gap-2">
            <Gpu className="size-4 shrink-0 text-muted-foreground" />
            <dt className="sr-only">การ์ดจอ</dt>
            <dd>{machine.gpu}</dd>
          </div>
          <div className="flex items-center gap-2">
            <MemoryStick className="size-4 shrink-0 text-muted-foreground" />
            <dt className="sr-only">หน่วยความจำและพื้นที่จัดเก็บ</dt>
            <dd>
              {machine.ram} GB{" "}
              <span className="mx-1 text-muted-foreground">·</span>{" "}
              {machine.storage}
            </dd>
          </div>
        </dl>
      </CardContent>
      <CardFooter className="mt-auto flex flex-col items-stretch gap-3">
        <p>
          <span className="text-2xl font-bold tracking-tight text-primary">
            ฿{machine.price}
          </span>
          <span className="ml-1 text-xs text-muted-foreground">/ ชั่วโมง</span>
        </p>
        <Button
          className="w-full"
          variant="outline"
          disabled={!available}
          onClick={(event) => onSelect(machine, event.currentTarget)}
          aria-label={
            available
              ? `เลือกเช่าเครื่อง ${machine.id}`
              : `${machine.id} ${machineStatusLabels[machine.status]}`
          }
        >
          {available ? "เลือกเครื่องนี้" : machineStatusLabels[machine.status]}
          {available && <ArrowUpRight data-icon="inline-end" />}
        </Button>
      </CardFooter>
    </Card>
  );
}
