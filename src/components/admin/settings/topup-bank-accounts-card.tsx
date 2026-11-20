"use client";

import { useState } from "react";
import { ArrowLeftRight, Landmark, Plus, Save } from "lucide-react";
import { AdminCard } from "@/components/admin/admin-page";
import { BankAccountDialog } from "@/components/admin/settings/topup-bank-account-dialog";
import {
  type BankAccountFormValues,
  type TopupMutationResult,
  activeBankRail,
  formatAccountNumber,
  railLabel,
  topupErrorMessage,
} from "@/components/admin/settings/topup-settings-api";
import { BankLogoTile } from "@/components/admin/settings/topup-settings-ui";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  CardAction,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty";
import { Field, FieldDescription, FieldError, FieldLabel } from "@/components/ui/field";
import { Spinner } from "@/components/ui/spinner";
import { Switch } from "@/components/ui/switch";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import type { BankAccountAdmin, TopupSettingsAdmin } from "@/lib/topup/types";

export function BankRailCard({
  settings,
  draftEnabled,
  activeAccounts,
  saving,
  onChange,
  onSave,
}: {
  settings: TopupSettingsAdmin;
  draftEnabled: boolean;
  activeAccounts: number;
  saving: boolean;
  onChange: (enabled: boolean) => void;
  onSave: () => Promise<TopupMutationResult<unknown>>;
}) {
  const [formError, setFormError] = useState("");
  const [savedMessage, setSavedMessage] = useState("");
  const rail = activeBankRail(settings);

  async function handleSave() {
    if (saving) return;
    if (draftEnabled && activeAccounts === 0) {
      setFormError("ต้องมีบัญชีที่เปิดใช้งานอย่างน้อยหนึ่งบัญชีก่อนเปิดช่องทางโอน");
      setSavedMessage("");
      return;
    }
    setFormError("");
    setSavedMessage("");
    const result = await onSave();
    if (result.ok) {
      setSavedMessage("บันทึกช่องทางโอนผ่านบัญชีแล้ว");
    } else {
      setFormError(topupErrorMessage(result.error));
    }
  }

  return (
    <AdminCard>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <ArrowLeftRight aria-hidden className="size-4 text-primary" />
          ช่องทางรับเงิน
        </CardTitle>
        <CardDescription>
          ระบบเปิดรับเงินได้ครั้งละหนึ่งรูปแบบ: PromptPay QR หรือโอนผ่านบัญชีธนาคาร
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-sm text-muted-foreground">ช่องทางที่ใช้อยู่:</span>
          <Badge variant={rail ? "available" : "outline"}>
            {rail ? railLabel(rail) : "ยังไม่เปิดช่องทาง"}
          </Badge>
        </div>

        <dl className="grid gap-3 text-sm">
          <div className="flex items-center justify-between gap-3">
            <dt className="text-muted-foreground">PromptPay QR</dt>
            <dd>
              <Badge variant={settings.promptpay.enabled ? "available" : "outline"}>
                {settings.promptpay.enabled ? "เปิด" : "ปิด"}
              </Badge>
            </dd>
          </div>
          <div className="flex items-center justify-between gap-3">
            <dt className="text-muted-foreground">โอนผ่านบัญชีธนาคาร</dt>
            <dd>
              <Badge variant={settings.bankTransfer.enabled ? "available" : "outline"}>
                {settings.bankTransfer.enabled ? "เปิด" : "ปิด"}
              </Badge>
            </dd>
          </div>
          <div className="flex items-center justify-between gap-3">
            <dt className="text-muted-foreground">บัญชีที่เปิดใช้งาน</dt>
            <dd className="tabular-nums">{activeAccounts} บัญชี</dd>
          </div>
        </dl>

        <Field orientation="horizontal" className="border-t pt-4">
          <FieldLabel htmlFor="topup-bank-transfer-enabled" className="flex-1">
            เปิดใช้งานโอนผ่านบัญชีธนาคาร
          </FieldLabel>
          <Switch
            id="topup-bank-transfer-enabled"
            aria-label="เปิดใช้งานโอนผ่านบัญชีธนาคาร"
            checked={draftEnabled}
            disabled={saving}
            onCheckedChange={(checked) => {
              onChange(checked);
              setFormError("");
              setSavedMessage("");
            }}
          />
        </Field>
        <FieldDescription>
          การเปิดช่องทางนี้จะปิด PromptPay QR อัตโนมัติ และการเปิด PromptPay QR
          จะปิดช่องทางนี้ให้เช่นกัน
        </FieldDescription>

        {draftEnabled && !settings.bankTransfer.enabled && activeAccounts === 0 ? (
          <FieldError>
            ต้องมีบัญชีที่เปิดใช้งานอย่างน้อยหนึ่งบัญชีก่อนเปิดช่องทางโอน
          </FieldError>
        ) : null}

        {formError ? (
          <Alert variant="destructive">
            <AlertTitle>บันทึกไม่สำเร็จ</AlertTitle>
            <AlertDescription>{formError}</AlertDescription>
          </Alert>
        ) : null}

        <div className="flex flex-wrap items-center gap-3">
          <Button type="button" disabled={saving} onClick={() => void handleSave()}>
            {saving ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <Save data-icon="inline-start" />
            )}
            {saving ? "กำลังบันทึก…" : "บันทึกช่องทางโอน"}
          </Button>
          <span role="status" aria-live="polite" className="text-sm text-muted-foreground">
            {savedMessage}
          </span>
        </div>
      </CardContent>
    </AdminCard>
  );
}

export function BankAccountsCard({
  accounts,
  busy,
  onCreate,
  onUpdate,
  onToggleActive,
  onArchive,
}: {
  accounts: BankAccountAdmin[];
  busy: boolean;
  onCreate: (
    values: BankAccountFormValues,
  ) => Promise<TopupMutationResult<BankAccountAdmin>>;
  onUpdate: (
    account: BankAccountAdmin,
    values: BankAccountFormValues,
  ) => Promise<TopupMutationResult<BankAccountAdmin>>;
  onToggleActive: (
    account: BankAccountAdmin,
    isActive: boolean,
  ) => Promise<TopupMutationResult<BankAccountAdmin>>;
  onArchive: (
    account: BankAccountAdmin,
  ) => Promise<TopupMutationResult<BankAccountAdmin>>;
}) {
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editing, setEditing] = useState<BankAccountAdmin | null>(null);
  const [deleting, setDeleting] = useState<BankAccountAdmin | null>(null);
  const [archiving, setArchiving] = useState(false);
  const [listError, setListError] = useState("");

  function openCreate() {
    setEditing(null);
    setDialogOpen(true);
    setListError("");
  }

  function openEdit(account: BankAccountAdmin) {
    setEditing(account);
    setDialogOpen(true);
    setListError("");
  }

  async function handleDialogSubmit(values: BankAccountFormValues) {
    const result = editing
      ? await onUpdate(editing, values)
      : await onCreate(values);
    return result;
  }

  async function handleToggle(account: BankAccountAdmin, isActive: boolean) {
    setListError("");
    const result = await onToggleActive(account, isActive);
    if (!result.ok) setListError(topupErrorMessage(result.error));
  }

  async function handleArchive() {
    if (!deleting || archiving) return;
    setArchiving(true);
    setListError("");
    const result = await onArchive(deleting);
    setArchiving(false);
    if (result.ok) {
      setDeleting(null);
      return;
    }
    setDeleting(null);
    setListError(topupErrorMessage(result.error));
  }

  return (
    <AdminCard>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Landmark aria-hidden className="size-4 text-primary" />
          บัญชีธนาคารสำหรับโอนเงิน
        </CardTitle>
        <CardDescription>
          รายการบัญชีที่แสดงให้ลูกค้าโอนเงิน · บัญชีที่เก็บถาวรแล้วจะไม่แสดงให้ลูกค้า
        </CardDescription>
        <CardAction>
          <Button type="button" size="sm" onClick={openCreate} disabled={busy}>
            <Plus data-icon="inline-start" />
            เพิ่มบัญชีธนาคาร
          </Button>
        </CardAction>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {listError ? (
          <Alert variant="destructive">
            <AlertTitle>ดำเนินการไม่สำเร็จ</AlertTitle>
            <AlertDescription>{listError}</AlertDescription>
          </Alert>
        ) : null}

        {accounts.length === 0 ? (
          <Empty>
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <Landmark aria-hidden />
              </EmptyMedia>
              <EmptyTitle>ยังไม่มีบัญชีธนาคาร</EmptyTitle>
              <EmptyDescription>
                เพิ่มบัญชีธนาคารอย่างน้อยหนึ่งบัญชีก่อนเปิดช่องทางโอนเงิน
              </EmptyDescription>
            </EmptyHeader>
            <EmptyContent>
              <Button type="button" size="sm" onClick={openCreate}>
                <Plus data-icon="inline-start" />
                เพิ่มบัญชีธนาคาร
              </Button>
            </EmptyContent>
          </Empty>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>ธนาคาร</TableHead>
                <TableHead>เลขที่บัญชี</TableHead>
                <TableHead>ชื่อบัญชี</TableHead>
                <TableHead>สถานะ</TableHead>
                <TableHead className="text-right">จัดการ</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {accounts.map((account) => {
                const archived = account.archivedAt !== null;
                const label = `${account.bankName} ${formatAccountNumber(account.accountNumber)}`;
                return (
                  <TableRow
                    key={account.id}
                    className={archived ? "opacity-60" : undefined}
                  >
                    <TableCell>
                      <div className="flex items-center gap-2">
                        <BankLogoTile
                          bankCode={account.bankCode}
                          bankName={account.bankName}
                          logoKey={account.logoKey}
                          logoDataUrl={account.logoDataUrl}
                        />
                        <div className="min-w-0">
                          <div className="font-medium">{account.bankName}</div>
                          <div className="text-xs text-muted-foreground">
                            {account.bankCode} ·{" "}
                            {account.logoDataUrl ? "โลโก้อัปโหลด" : "โลโก้สำเร็จรูป"}
                          </div>
                        </div>
                      </div>
                    </TableCell>
                    <TableCell className="font-mono">
                      {formatAccountNumber(account.accountNumber)}
                    </TableCell>
                    <TableCell>{account.accountName}</TableCell>
                    <TableCell>
                      {archived ? (
                        <Badge variant="secondary">เก็บถาวรแล้ว</Badge>
                      ) : (
                        <div className="flex items-center gap-2">
                          <Badge variant={account.isActive ? "available" : "outline"}>
                            {account.isActive ? "เปิดใช้งาน" : "ปิดใช้งาน"}
                          </Badge>
                          <Switch
                            aria-label={`เปิดใช้งานบัญชี ${label}`}
                            checked={account.isActive}
                            disabled={busy}
                            onCheckedChange={(checked) =>
                              void handleToggle(account, checked)
                            }
                          />
                        </div>
                      )}
                    </TableCell>
                    <TableCell className="text-right">
                      {archived ? (
                        <span className="text-xs text-muted-foreground">—</span>
                      ) : (
                        <div className="flex justify-end gap-2">
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            disabled={busy}
                            onClick={() => openEdit(account)}
                          >
                            แก้ไข
                          </Button>
                          <Button
                            type="button"
                            variant="destructive"
                            size="sm"
                            disabled={busy}
                            onClick={() => {
                              setDeleting(account);
                              setListError("");
                            }}
                          >
                            เก็บถาวร
                          </Button>
                        </div>
                      )}
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
      </CardContent>

      <BankAccountDialog
        open={dialogOpen}
        account={editing}
        onOpenChange={(open) => {
          setDialogOpen(open);
          if (!open) setEditing(null);
        }}
        onSubmit={handleDialogSubmit}
      />

      <AlertDialog
        open={Boolean(deleting)}
        onOpenChange={(open) => {
          if (!open && !archiving) setDeleting(null);
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>เก็บถาวรบัญชีธนาคาร?</AlertDialogTitle>
            <AlertDialogDescription>
              {deleting
                ? `${deleting.bankName} เลขที่ ${formatAccountNumber(deleting.accountNumber)} จะถูกเก็บถาวรและไม่แสดงให้ลูกค้าโอนเงินอีก`
                : "บัญชีนี้จะถูกเก็บถาวรและไม่แสดงให้ลูกค้าโอนเงินอีก"}
              {" "}ข้อมูลประวัติจะยังอยู่และตรวจสอบย้อนหลังได้
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={archiving}>ยกเลิก</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              disabled={archiving}
              onClick={(event) => {
                event.preventDefault();
                void handleArchive();
              }}
            >
              {archiving ? "กำลังเก็บถาวร…" : "เก็บถาวร"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </AdminCard>
  );
}
