"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import toast from "react-hot-toast";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { UserAvatar } from "@/components/auth/user-avatar";
import { formatBalance } from "@/lib/money";
import { getRoleLabel } from "@/lib/roles";
import type { ManagedMember } from "@/lib/admin-members-data";

export type MemberAction = "detail" | "credit" | "debit" | "role";
const actionLabels = {
  detail: "รายละเอียดสมาชิก",
  credit: "เพิ่มยอดเงิน",
  debit: "ลดยอดเงิน",
  role: "เปลี่ยนสิทธิ์สมาชิก",
};
const roleItems = [
  { value: "user", label: "สมาชิก" },
  { value: "admin", label: "ผู้ดูแลระบบ" },
];

export function MemberDialog({
  member,
  action,
  actorId,
  onClose,
}: {
  member: ManagedMember;
  action: MemberAction;
  actorId: string;
  onClose: () => void;
}) {
  const router = useRouter();
  const [pending, setPending] = useState(false);
  const [role, setRole] = useState(member.role);
  const [amount, setAmount] = useState("");
  const [reason, setReason] = useState("");
  const [retry, setRetry] = useState<{
    signature: string;
    requestId: string;
  } | null>(null);
  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending || action === "detail") return;
    const body = {
      action,
      ...(action === "role" ? { role } : { amount }),
      reason: reason.trim(),
    };
    const signature = JSON.stringify(body);
    const requestId =
      retry?.signature === signature ? retry.requestId : crypto.randomUUID();
    setRetry({ signature, requestId });
    setPending(true);
    try {
      const response = await fetch(
        `/api/admin/users/${encodeURIComponent(member.id)}`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ ...body, requestId }),
        },
      );
      const data = await response.json();
      if (!response.ok) {
        toast.error(data.error || "บันทึกไม่สำเร็จ");
        return;
      }
      toast.success(
        action === "role" ? "เปลี่ยนสิทธิ์สมาชิกแล้ว" : "ปรับยอดเงินสำเร็จ",
      );
      router.refresh();
      onClose();
    } catch {
      toast.error("เชื่อมต่อไม่สำเร็จ ลองอีกครั้งได้โดยไม่บันทึกยอดซ้ำ");
    } finally {
      setPending(false);
    }
  }
  const date = (timestamp: number) =>
    new Intl.DateTimeFormat("th-TH", {
      dateStyle: "medium",
      timeStyle: "short",
      timeZone: "Asia/Bangkok",
    }).format(timestamp);
  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !pending) onClose();
      }}
    >
      <DialogContent
        data-lenis-prevent
        className="max-h-[90dvh] overflow-y-auto sm:max-w-lg"
      >
        <DialogHeader>
          <DialogTitle>{actionLabels[action]}</DialogTitle>
          <DialogDescription>
            {member.name} · {member.email}
          </DialogDescription>
        </DialogHeader>
        {action === "detail" ? (
          <>
            <div className="flex items-center gap-3">
              <UserAvatar user={member} size="lg" />
              <div>
                <p className="font-medium">{member.name}</p>
                <p className="text-sm text-muted-foreground">
                  {member.username
                    ? `@${member.username}`
                    : "ไม่ได้ตั้งชื่อผู้ใช้"}
                </p>
              </div>
            </div>
            <dl className="grid grid-cols-[auto_1fr] gap-x-5 gap-y-3 text-sm">
              <dt className="text-muted-foreground">สิทธิ์</dt>
              <dd>{getRoleLabel(member.role)}</dd>
              <dt className="text-muted-foreground">ยอดเงิน</dt>
              <dd className="font-medium tabular-nums">
                {formatBalance(member.balanceSatang)}
              </dd>
              <dt className="text-muted-foreground">สมัครเมื่อ</dt>
              <dd>{date(member.createdAt)}</dd>
              <dt className="text-muted-foreground">อีเมล</dt>
              <dd>{member.emailVerified ? "ยืนยันแล้ว" : "ยังไม่ยืนยัน"}</dd>
            </dl>
            <div className="flex flex-col gap-2">
              <h3 className="text-sm font-medium">ช่องทางที่เชื่อมอยู่</h3>
              {member.providers.map((provider) => (
                <p
                  key={`${provider.providerId}:${provider.accountId}`}
                  className="break-all rounded-xl border p-3 text-xs"
                >
                  <span className="font-medium">
                    {provider.providerId === "credential"
                      ? "ชื่อผู้ใช้และรหัสผ่าน"
                      : provider.providerId === "google"
                        ? "Google"
                        : "Discord"}
                  </span>
                  {provider.providerId !== "credential" && (
                    <span className="mt-1 block text-muted-foreground">
                      ID: {provider.accountId}
                    </span>
                  )}
                </p>
              ))}
            </div>
            <div className="flex flex-col gap-2">
              <h3 className="text-sm font-medium">การจัดการล่าสุด</h3>
              {!member.history.length && (
                <p className="text-xs text-muted-foreground">
                  ยังไม่มีประวัติการปรับยอดเงินหรือสิทธิ์
                </p>
              )}
              {member.history.map((entry, index) => (
                <div key={index} className="rounded-xl border p-3 text-xs">
                  <div className="flex items-center justify-between gap-2">
                    <span className="font-medium">
                      {entry.action === "role"
                        ? `เปลี่ยนเป็น${getRoleLabel(entry.roleAfter)}`
                        : `${entry.action === "credit" ? "+" : "−"}${formatBalance(entry.amountSatang)}`}
                    </span>
                    <span className="text-muted-foreground">
                      {date(entry.createdAt)}
                    </span>
                  </div>
                  <p className="mt-1 break-words">{entry.reason}</p>
                  <p className="mt-1 text-muted-foreground">
                    โดย {entry.actorName}
                  </p>
                </div>
              ))}
            </div>
          </>
        ) : (
          <form onSubmit={submit}>
            <FieldGroup>
              <div className="flex items-center justify-between rounded-xl bg-muted p-3 text-sm">
                <span>ยอดเงินปัจจุบัน</span>
                <span className="font-semibold tabular-nums">
                  {formatBalance(member.balanceSatang)}
                </span>
              </div>
              {action === "role" ? (
                <Field>
                  <FieldLabel htmlFor="member-role">สิทธิ์ใหม่</FieldLabel>
                  <Select
                    items={roleItems}
                    value={role}
                    onValueChange={(value) => setRole(value || "user")}
                    disabled={pending || actorId === member.id}
                  >
                    <SelectTrigger id="member-role" className="w-full">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectGroup>
                        {roleItems.map((item) => (
                          <SelectItem key={item.value} value={item.value}>
                            {item.label}
                          </SelectItem>
                        ))}
                      </SelectGroup>
                    </SelectContent>
                  </Select>
                  {actorId === member.id && (
                    <p className="text-xs text-muted-foreground">
                      ไม่สามารถลดสิทธิ์ผู้ดูแลของตัวเองได้
                    </p>
                  )}
                </Field>
              ) : (
                <Field>
                  <FieldLabel htmlFor="adjust-amount">
                    จำนวนเงิน (บาท)
                  </FieldLabel>
                  <Input
                    id="adjust-amount"
                    value={amount}
                    onChange={(event) => setAmount(event.target.value)}
                    inputMode="decimal"
                    placeholder="0.00"
                    pattern="[0-9]{1,10}(\.[0-9]{1,2})?"
                    required
                    disabled={pending}
                    autoFocus
                  />
                </Field>
              )}
              <Field>
                <FieldLabel htmlFor="adjust-reason">
                  เหตุผลการเปลี่ยนแปลง
                </FieldLabel>
                <Textarea
                  id="adjust-reason"
                  value={reason}
                  onChange={(event) => setReason(event.target.value)}
                  minLength={3}
                  maxLength={300}
                  required
                  disabled={pending}
                  placeholder="ระบุเหตุผลเพื่อบันทึกประวัติ"
                />
              </Field>
              <p className="text-xs text-muted-foreground">
                รายการนี้จะเปลี่ยนข้อมูลจริงและบันทึกผู้ดำเนินการไว้ในประวัติ
              </p>
              <div className="flex justify-end gap-2">
                <Button
                  type="button"
                  variant="outline"
                  disabled={pending}
                  onClick={onClose}
                >
                  ยกเลิก
                </Button>
                <Button
                  type="submit"
                  variant={action === "debit" ? "destructive" : "default"}
                  disabled={
                    pending ||
                    (action === "role" &&
                      (role === member.role || actorId === member.id))
                  }
                >
                  {pending ? "กำลังบันทึก…" : "ยืนยันการเปลี่ยนแปลง"}
                </Button>
              </div>
            </FieldGroup>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}
