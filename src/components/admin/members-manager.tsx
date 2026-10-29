"use client";
import { useState, useTransition } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import {
  ArrowDownUp,
  ChevronLeft,
  ChevronRight,
  Eye,
  Link2,
  Minus,
  Plus,
  Search,
  ShieldCheck,
  UserPlus,
  UserRoundCog,
  Users,
  Wallet,
} from "lucide-react";
import {
  IconBrandDiscordFilled,
  IconBrandGoogleFilled,
} from "@tabler/icons-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import {
  InputGroup,
  InputGroupAddon,
  InputGroupButton,
  InputGroupInput,
} from "@/components/ui/input-group";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyTitle,
} from "@/components/ui/empty";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { UserAvatar } from "@/components/auth/user-avatar";
import { MembersChart } from "@/components/admin/members-chart";
import {
  MemberDialog,
  type MemberAction,
} from "@/components/admin/member-dialog";
import { formatBalance } from "@/lib/money";
import { getRoleLabel } from "@/lib/roles";
import type { AdminMembersData, ManagedMember } from "@/lib/admin-members-data";

const roleOptions = [
  { value: "all", label: "ทุกตำแหน่ง" },
  { value: "user", label: "สมาชิก" },
  { value: "admin", label: "ผู้ดูแลระบบ" },
];
const providerOptions = [
  { value: "all", label: "ทุกช่องทาง" },
  { value: "google", label: "Google" },
  { value: "discord", label: "Discord" },
  { value: "credential", label: "ชื่อผู้ใช้ / รหัสผ่าน" },
];
const linkedOptions = [
  { value: "all", label: "ทุกการเชื่อมต่อ" },
  { value: "linked", label: "เชื่อมโซเชียลแล้ว" },
  { value: "unlinked", label: "ยังไม่เชื่อมโซเชียล" },
];
const periods = [
  { value: "day", label: "รายวัน" },
  { value: "week", label: "รายสัปดาห์" },
  { value: "month", label: "รายเดือน" },
  { value: "year", label: "รายปี" },
  { value: "custom", label: "กำหนดเอง" },
];
const joinedDate = new Intl.DateTimeFormat("th-TH", {
  timeZone: "Asia/Bangkok",
  day: "numeric",
  month: "short",
  year: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
});

function FilterSelect({
  label,
  value,
  items,
  onChange,
  disabled,
}: {
  label: string;
  value: string;
  items: { value: string; label: string }[];
  onChange: (value: string) => void;
  disabled: boolean;
}) {
  return (
    <Select
      items={items}
      value={value}
      onValueChange={(value) => onChange(value || "all")}
      disabled={disabled}
    >
      <SelectTrigger aria-label={label} className="w-full rounded-xl sm:w-44">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectGroup>
          {items.map((item) => (
            <SelectItem key={item.value} value={item.value}>
              {item.label}
            </SelectItem>
          ))}
        </SelectGroup>
      </SelectContent>
    </Select>
  );
}

function ProviderBadge({ provider }: { provider: string }) {
  return (
    <Badge variant="secondary" className="gap-1.5 whitespace-nowrap">
      {provider === "google" ? (
        <IconBrandGoogleFilled />
      ) : provider === "discord" ? (
        <IconBrandDiscordFilled />
      ) : (
        <ShieldCheck />
      )}
      {provider === "google"
        ? "Google"
        : provider === "discord"
          ? "Discord"
          : "รหัสผ่าน"}
    </Badge>
  );
}

export function MembersManager({
  data,
}: {
  data: AdminMembersData & { actorId: string };
}) {
  const router = useRouter();
  const searchParams = useSearchParams();
  const [pending, startTransition] = useTransition();
  const [selected, setSelected] = useState<{
    member: ManagedMember;
    action: MemberAction;
  } | null>(null);
  const { stats, filters } = data;
  function update(values: Record<string, string>, resetPage = true) {
    const params = new URLSearchParams(searchParams.toString());
    for (const [key, value] of Object.entries(values)) {
      if (value && value !== "all") params.set(key, value);
      else params.delete(key);
    }
    if (resetPage) params.delete("page");
    startTransition(() =>
      router.replace(`/admin/users?${params}`, { scroll: false }),
    );
  }
  const growth = stats.previousMembers
    ? ((stats.newMembers - stats.previousMembers) / stats.previousMembers) * 100
    : null;
  const linkedPercent = stats.totalMembers
    ? Math.round((stats.linkedMembers / stats.totalMembers) * 100)
    : 0;
  const cards = [
    {
      label: "ผู้ใช้ทั้งหมด",
      value: stats.totalMembers.toLocaleString("th-TH"),
      hint: "นับทุกบัญชีในระบบ",
      icon: Users,
    },
    {
      label: "สมัครใหม่ในช่วงที่เลือก",
      value: stats.newMembers.toLocaleString("th-TH"),
      hint:
        growth === null
          ? "ยังไม่มีฐานเปรียบเทียบช่วงก่อนหน้า"
          : `${growth >= 0 ? "+" : ""}${growth.toFixed(1)}% เทียบช่วงก่อนหน้า`,
      icon: UserPlus,
    },
    {
      label: "ผู้ใช้ที่ลิงก์โซเชียล",
      value: stats.linkedMembers.toLocaleString("th-TH"),
      hint: `${linkedPercent}% ของสมาชิก · Discord ${stats.discord} · Google ${stats.google}`,
      icon: Link2,
    },
    {
      label: "ยอดเงินรวมในระบบ",
      value: formatBalance(stats.totalBalanceSatang),
      hint: "ยอดคงเหลือรวมของสมาชิก",
      icon: Wallet,
    },
  ];
  return (
    <div className="flex min-w-0 flex-col gap-4" aria-busy={pending}>
      <div className="flex flex-col justify-between gap-4 xl:flex-row xl:items-center">
        <div>
          <h1 className="flex items-center gap-2 text-xl font-semibold">
            <Users className="size-5 text-primary" />
            จัดการสมาชิก
          </h1>
          <p className="mt-1 text-sm text-muted-foreground">
            สมาชิกทั้งหมด {stats.totalMembers.toLocaleString("th-TH")} คน
          </p>
        </div>
        <div className="max-w-full overflow-x-auto">
          <ToggleGroup
            value={[filters.period]}
            onValueChange={(values) => {
              if (values[0]) update({ period: values[0] });
            }}
            aria-label="ช่วงเวลาสถิติ"
            disabled={pending}
            spacing={1}
            className="rounded-xl border bg-muted/50 p-1"
          >
            {periods.map((period) => (
              <ToggleGroupItem
                key={period.value}
                value={period.value}
                size="sm"
                className="rounded-lg px-3 text-xs data-pressed:bg-primary data-pressed:text-primary-foreground"
              >
                {period.label}
              </ToggleGroupItem>
            ))}
          </ToggleGroup>
        </div>
      </div>
      {filters.period === "custom" && (
        <form
          onSubmit={(event) => {
            event.preventDefault();
            const form = new FormData(event.currentTarget);
            update({
              from: String(form.get("from")),
              to: String(form.get("to")),
            });
          }}
        >
          <FieldGroup className="grid items-end gap-3 sm:grid-cols-[1fr_1fr_auto] sm:max-w-xl">
            <Field>
              <FieldLabel htmlFor="range-from">ตั้งแต่วันที่</FieldLabel>
              <Input
                id="range-from"
                name="from"
                type="date"
                required
                defaultValue={filters.from}
                disabled={pending}
              />
            </Field>
            <Field>
              <FieldLabel htmlFor="range-to">ถึงวันที่</FieldLabel>
              <Input
                id="range-to"
                name="to"
                type="date"
                required
                defaultValue={filters.to}
                disabled={pending}
              />
            </Field>
            <Button disabled={pending}>ใช้ช่วงเวลา</Button>
          </FieldGroup>
        </form>
      )}
      {data.rangeError && (
        <Alert variant="destructive">
          <AlertDescription>{data.rangeError}</AlertDescription>
        </Alert>
      )}
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {cards.map(({ label, value, hint, icon: Icon }) => (
          <Card key={label} className="rounded-2xl border py-4 shadow-none">
            <CardContent className="flex flex-col gap-3 px-4">
              <div className="flex items-center justify-between gap-3">
                <p className="text-sm text-muted-foreground">{label}</p>
                <span className="flex size-8 items-center justify-center rounded-xl bg-primary/10 text-primary">
                  <Icon className="size-4" />
                </span>
              </div>
              <div>
                <p className="text-2xl font-semibold tracking-tight tabular-nums">
                  {value}
                </p>
                <p className="mt-1 text-xs text-muted-foreground">{hint}</p>
              </div>
            </CardContent>
          </Card>
        ))}
      </div>
      <MembersChart data={data.chart} from={filters.from} to={filters.to} />
      <Card className="gap-4 rounded-2xl border py-4 shadow-none">
        <CardContent className="flex min-w-0 flex-col gap-4 px-4">
          <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
            <form
              role="search"
              className="w-full lg:max-w-sm"
              onSubmit={(event) => {
                event.preventDefault();
                update({
                  q: String(new FormData(event.currentTarget).get("q") || ""),
                });
              }}
            >
              <Field>
                <FieldLabel htmlFor="member-search" className="sr-only">
                  ค้นหาสมาชิก
                </FieldLabel>
                <InputGroup className="h-10">
                  <InputGroupAddon>
                    <Search />
                  </InputGroupAddon>
                  <InputGroupInput
                    key={filters.q}
                    id="member-search"
                    name="q"
                    defaultValue={filters.q}
                    maxLength={100}
                    placeholder="ค้นหาชื่อ / อีเมล / username / ID โซเชียล"
                    disabled={pending}
                  />
                  <InputGroupAddon align="inline-end">
                    <InputGroupButton type="submit" disabled={pending}>
                      ค้นหา
                    </InputGroupButton>
                  </InputGroupAddon>
                </InputGroup>
              </Field>
            </form>
            <div className="grid flex-1 gap-3 sm:grid-cols-3 lg:flex lg:flex-wrap">
              <FilterSelect
                label="ตำแหน่งสมาชิก"
                value={filters.role}
                items={roleOptions}
                onChange={(value) => update({ role: value })}
                disabled={pending}
              />
              <FilterSelect
                label="ช่องทางเข้าสู่ระบบ"
                value={filters.provider}
                items={providerOptions}
                onChange={(value) => update({ provider: value })}
                disabled={pending}
              />
              <FilterSelect
                label="สถานะการเชื่อมต่อ"
                value={filters.linked}
                items={linkedOptions}
                onChange={(value) => update({ linked: value })}
                disabled={pending}
              />
            </div>
            {(filters.q ||
              filters.role !== "all" ||
              filters.provider !== "all" ||
              filters.linked !== "all") && (
              <Button
                variant="ghost"
                size="sm"
                disabled={pending}
                onClick={() =>
                  update({ q: "", role: "all", provider: "all", linked: "all" })
                }
              >
                ล้างตัวกรอง
              </Button>
            )}
          </div>
          {data.members.length ? (
            <Table>
              <caption className="sr-only">
                รายชื่อสมาชิกและการจัดการบัญชี
              </caption>
              <TableHeader>
                <TableRow>
                  <TableHead>สมาชิก</TableHead>
                  <TableHead>บัญชีโซเชียล</TableHead>
                  <TableHead>ช่องทางเข้าสู่ระบบ</TableHead>
                  <TableHead>สิทธิ์</TableHead>
                  <TableHead className="text-right">ยอดเงิน</TableHead>
                  <TableHead>
                    <Button
                      variant="ghost"
                      size="sm"
                      className="-ml-3"
                      onClick={() =>
                        update({
                          sort: filters.sort === "newest" ? "oldest" : "newest",
                        })
                      }
                      disabled={pending}
                    >
                      วันที่สมัคร
                      <ArrowDownUp />
                    </Button>
                  </TableHead>
                  <TableHead className="text-right">จัดการ</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.members.map((member) => (
                  <TableRow key={member.id}>
                    <TableCell>
                      <span className="flex items-center gap-3">
                        <UserAvatar user={member} />
                        <span className="flex max-w-56 flex-col gap-0.5">
                          <span className="truncate font-medium">
                            {member.name}
                          </span>
                          <span className="truncate text-xs text-muted-foreground">
                            {member.email}
                          </span>
                          {member.username && (
                            <span className="truncate text-xs text-muted-foreground">
                              @{member.username}
                            </span>
                          )}
                        </span>
                      </span>
                    </TableCell>
                    <TableCell>
                      <div className="flex flex-col gap-1.5">
                        {member.providers
                          .filter((p) => p.providerId !== "credential")
                          .map((provider) => (
                            <span
                              key={`${provider.providerId}:${provider.accountId}`}
                              className="flex items-center gap-1.5 text-xs"
                            >
                              <span className="text-primary">
                                {provider.providerId === "google" ? (
                                  <IconBrandGoogleFilled className="size-3.5" />
                                ) : (
                                  <IconBrandDiscordFilled className="size-3.5" />
                                )}
                              </span>
                              <span
                                className="max-w-44 truncate font-mono"
                                title={provider.accountId}
                              >
                                {provider.accountId}
                              </span>
                            </span>
                          ))}
                        {!member.providers.some(
                          (p) => p.providerId !== "credential",
                        ) && (
                          <span className="text-xs text-muted-foreground">
                            ยังไม่เชื่อม
                          </span>
                        )}
                      </div>
                    </TableCell>
                    <TableCell>
                      <span className="flex flex-wrap gap-1">
                        {Array.from(
                          new Set(member.providers.map((p) => p.providerId)),
                        ).map((provider) => (
                          <ProviderBadge key={provider} provider={provider} />
                        ))}
                      </span>
                    </TableCell>
                    <TableCell>
                      <Badge
                        variant={
                          member.role === "admin" ? "default" : "secondary"
                        }
                      >
                        {getRoleLabel(member.role)}
                      </Badge>
                    </TableCell>
                    <TableCell className="text-right font-medium tabular-nums text-primary">
                      {formatBalance(member.balanceSatang)}
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-xs text-muted-foreground">
                      {joinedDate.format(member.createdAt)}
                    </TableCell>
                    <TableCell>
                      <div className="flex justify-end gap-1">
                        {(
                          [
                            {
                              action: "detail",
                              label: "ดูรายละเอียด",
                              icon: Eye,
                            },
                            {
                              action: "credit",
                              label: "เพิ่มยอดเงิน",
                              icon: Plus,
                            },
                            {
                              action: "debit",
                              label: "ลดยอดเงิน",
                              icon: Minus,
                            },
                            {
                              action: "role",
                              label: "เปลี่ยนสิทธิ์",
                              icon: UserRoundCog,
                            },
                          ] as const
                        ).map(({ action, label, icon: Icon }) => (
                          <Button
                            key={action}
                            variant="outline"
                            size="icon-sm"
                            aria-label={`${label} ${member.name}`}
                            title={label}
                            disabled={
                              pending ||
                              (action === "role" && member.id === data.actorId)
                            }
                            onClick={() => setSelected({ member, action })}
                          >
                            <Icon />
                          </Button>
                        ))}
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : (
            <Empty>
              <EmptyHeader>
                <EmptyTitle>ไม่พบสมาชิก</EmptyTitle>
                <EmptyDescription>
                  ลองเปลี่ยนคำค้นหาหรือเงื่อนไขการกรอง
                </EmptyDescription>
              </EmptyHeader>
            </Empty>
          )}
          <div className="flex flex-wrap items-center justify-between gap-3 border-t pt-3 text-xs text-muted-foreground">
            <p>
              พบ {data.total.toLocaleString("th-TH")} บัญชี · แสดง{" "}
              {data.total ? (data.page - 1) * 20 + 1 : 0}–
              {Math.min(data.page * 20, data.total)}
            </p>
            <div className="flex items-center gap-3">
              <span>
                หน้า {data.page} / {data.pageCount}
              </span>
              <Button
                variant="outline"
                size="icon-sm"
                aria-label="หน้าก่อนหน้า"
                disabled={pending || data.page <= 1}
                onClick={() => update({ page: String(data.page - 1) }, false)}
              >
                <ChevronLeft />
              </Button>
              <Button
                variant="outline"
                size="icon-sm"
                aria-label="หน้าถัดไป"
                disabled={pending || data.page >= data.pageCount}
                onClick={() => update({ page: String(data.page + 1) }, false)}
              >
                <ChevronRight />
              </Button>
            </div>
          </div>
        </CardContent>
      </Card>
      {selected && (
        <MemberDialog
          key={`${selected.member.id}:${selected.action}`}
          member={selected.member}
          action={selected.action}
          actorId={data.actorId}
          onClose={() => setSelected(null)}
        />
      )}
    </div>
  );
}
