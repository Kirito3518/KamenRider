"use client";

import { type FormEvent, useState } from "react";
import { Landmark } from "lucide-react";
import { BANK_PRESETS, getBankPreset } from "@/lib/topup/banks";
import type { BankAccountAdmin } from "@/lib/topup/types";
import {
  type BankAccountFormValues,
  type FieldErrors,
  type TopupMutationResult,
  formatAccountNumber,
  hasFieldErrors,
  normalizeDigits,
  topupErrorMessage,
  validateBankAccountForm,
} from "@/components/admin/settings/topup-settings-api";
import { BankLogoTile } from "@/components/admin/settings/topup-settings-ui";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Field,
  FieldDescription,
  FieldError,
  FieldGroup,
  FieldLabel,
} from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Spinner } from "@/components/ui/spinner";
import { Switch } from "@/components/ui/switch";

type BankSelection = {
  value: string;
  name: string;
  code: string;
  logoKey: string | null;
  logoDataUrl: string | null;
};

function presetSelection(preset: (typeof BANK_PRESETS)[number]): BankSelection {
  return {
    value: preset.code,
    name: preset.name,
    code: preset.code,
    logoKey: preset.key,
    logoDataUrl: null,
  };
}

/**
 * New accounts can start from any standard preset. Accounts created before the
 * preset-only form may use a custom bank code; that row stays selectable so the
 * account can still be edited without inventing separate code/name inputs.
 */
function bankSelections(account: BankAccountAdmin | null): BankSelection[] {
  const selections: BankSelection[] = BANK_PRESETS.map(presetSelection);
  if (
    account &&
    !getBankPreset(account.bankCode) &&
    !selections.some((selection) => selection.value === account.bankCode)
  ) {
    selections.push({
      value: account.bankCode,
      name: account.bankName,
      code: account.bankCode,
      logoKey: account.logoKey,
      logoDataUrl: account.logoDataUrl,
    });
  }
  return selections;
}

function defaultForm(): BankAccountFormValues {
  const preset = BANK_PRESETS[0];
  return {
    bankCode: preset.code,
    bankName: preset.name,
    accountNumber: "",
    accountName: "",
    logoKey: preset.key,
    logoDataUrl: null,
    isActive: true,
  };
}

function formFromAccount(account: BankAccountAdmin): BankAccountFormValues {
  return {
    bankCode: account.bankCode,
    bankName: account.bankName,
    accountNumber: account.accountNumber,
    accountName: account.accountName,
    logoKey: account.logoKey,
    logoDataUrl: account.logoDataUrl,
    isActive: account.isActive,
  };
}

/**
 * Dialog wrapper. The form is a keyed child so opening it for another account
 * starts from a fresh initial state without a state-syncing effect.
 */
export function BankAccountDialog({
  open,
  account,
  onOpenChange,
  onSubmit,
}: {
  open: boolean;
  account: BankAccountAdmin | null;
  onOpenChange: (open: boolean) => void;
  onSubmit: (
    values: BankAccountFormValues,
  ) => Promise<TopupMutationResult<BankAccountAdmin>>;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[88dvh] overflow-y-auto sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>
            {account ? "แก้ไขบัญชีธนาคาร" : "เพิ่มบัญชีธนาคาร"}
          </DialogTitle>
          <DialogDescription>
            บัญชีนี้จะแสดงให้ลูกค้าโอนเงินเข้าพร้อมเลขที่บัญชีและโลโก้ธนาคาร
          </DialogDescription>
        </DialogHeader>
        <BankAccountDialogForm
          key={account ? `${account.id}:${account.updatedAt}` : "new-account"}
          account={account}
          onCancel={() => onOpenChange(false)}
          onSubmit={onSubmit}
        />
      </DialogContent>
    </Dialog>
  );
}

function BankAccountDialogForm({
  account,
  onCancel,
  onSubmit,
}: {
  account: BankAccountAdmin | null;
  onCancel: () => void;
  onSubmit: (
    values: BankAccountFormValues,
  ) => Promise<TopupMutationResult<BankAccountAdmin>>;
}) {
  const [values, setValues] = useState<BankAccountFormValues>(() =>
    account ? formFromAccount(account) : defaultForm(),
  );
  const [errors, setErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const selections = bankSelections(account);

  function update(patch: Partial<BankAccountFormValues>) {
    setValues((current) => ({ ...current, ...patch }));
  }

  function handleBankChange(next: string | null) {
    const selection = selections.find((item) => item.value === next);
    if (!selection) return;
    update({
      bankCode: selection.code,
      bankName: selection.name,
      logoKey: selection.logoKey,
      logoDataUrl: selection.logoDataUrl,
    });
    setErrors((current) => ({
      ...current,
      bankCode: undefined,
      bankName: undefined,
    }));
    setFormError("");
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (submitting) return;
    const nextValues: BankAccountFormValues = {
      ...values,
      bankCode: values.bankCode.trim(),
      bankName: values.bankName.trim(),
      accountNumber: normalizeDigits(values.accountNumber),
      accountName: values.accountName.trim(),
    };
    const nextErrors = validateBankAccountForm(nextValues);
    if (hasFieldErrors(nextErrors)) {
      setErrors(nextErrors);
      setFormError(
        Object.values(nextErrors).find((message) => Boolean(message)) ??
          "ข้อมูลบัญชีไม่ถูกต้อง",
      );
      return;
    }
    setSubmitting(true);
    setErrors({});
    setFormError("");
    const result = await onSubmit(nextValues);
    setSubmitting(false);
    if (result.ok) {
      onCancel();
      return;
    }
    setFormError(topupErrorMessage(result.error));
  }

  return (
    <form
      onSubmit={(event) => void handleSubmit(event)}
      noValidate
      className="flex flex-col gap-5"
    >
      <FieldGroup className="gap-5">
        <Field>
          <FieldLabel htmlFor="topup-bank-select">ธนาคาร</FieldLabel>
          <Select
            items={selections.map(({ value, name, code }) => ({
              value,
              label: `${name} (${code})`,
            }))}
            value={values.bankCode}
            onValueChange={handleBankChange}
          >
            <SelectTrigger
              id="topup-bank-select"
              className="w-full rounded-xl"
              aria-label="เลือกธนาคาร"
            >
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectGroup>
                {selections.map((selection) => (
                  <SelectItem key={selection.value} value={selection.value}>
                    <span className="flex items-center gap-2">
                      <BankLogoTile
                        bankCode={selection.code}
                        bankName={selection.name}
                        logoKey={selection.logoKey}
                        logoDataUrl={selection.logoDataUrl}
                        className="size-6 rounded-md text-[8px]"
                      />
                      <span>
                        {selection.name} ({selection.code})
                      </span>
                    </span>
                  </SelectItem>
                ))}
              </SelectGroup>
            </SelectContent>
          </Select>
          <FieldDescription>
            เลือกธนาคารเพื่อใช้รหัส ชื่อ และโลโก้มาตรฐานโดยอัตโนมัติ
          </FieldDescription>
        </Field>

        <div className="grid gap-4 sm:grid-cols-2">
          <Field data-invalid={Boolean(errors.accountNumber)}>
            <FieldLabel htmlFor="topup-bank-account-number">
              เลขที่บัญชี
            </FieldLabel>
            <Input
              id="topup-bank-account-number"
              name="accountNumber"
              inputMode="numeric"
              autoComplete="off"
              maxLength={15}
              placeholder="1234567890"
              value={values.accountNumber}
              aria-invalid={Boolean(errors.accountNumber)}
              onChange={(event) => {
                update({
                  accountNumber: event.currentTarget.value
                    .replace(/\D+/g, "")
                    .slice(0, 15),
                });
                setErrors((current) => ({
                  ...current,
                  accountNumber: undefined,
                }));
              }}
            />
            {errors.accountNumber ? (
              <FieldError>{errors.accountNumber}</FieldError>
            ) : (
              <FieldDescription>
                ตัวเลข 8-15 หลัก · แสดงผล:{" "}
                {formatAccountNumber(values.accountNumber)}
              </FieldDescription>
            )}
          </Field>
          <Field data-invalid={Boolean(errors.accountName)}>
            <FieldLabel htmlFor="topup-bank-account-name">ชื่อบัญชี</FieldLabel>
            <Input
              id="topup-bank-account-name"
              name="accountName"
              autoComplete="off"
              maxLength={120}
              placeholder="ชื่อ-นามสกุลเจ้าของบัญชี"
              value={values.accountName}
              aria-invalid={Boolean(errors.accountName)}
              onChange={(event) => {
                update({ accountName: event.currentTarget.value });
                setErrors((current) => ({
                  ...current,
                  accountName: undefined,
                }));
              }}
            />
            {errors.accountName ? (
              <FieldError>{errors.accountName}</FieldError>
            ) : null}
          </Field>
        </div>

        <Field orientation="horizontal">
          <FieldLabel htmlFor="topup-bank-active">เปิดใช้งานบัญชีนี้</FieldLabel>
          <Switch
            id="topup-bank-active"
            aria-label="เปิดใช้งานบัญชีนี้"
            checked={values.isActive}
            onCheckedChange={(checked) => update({ isActive: checked })}
          />
        </Field>

        {formError ? (
          <Alert variant="destructive">
            <AlertTitle>บันทึกบัญชีไม่สำเร็จ</AlertTitle>
            <AlertDescription>{formError}</AlertDescription>
          </Alert>
        ) : null}
      </FieldGroup>

      <DialogFooter>
        <Button
          type="button"
          variant="outline"
          disabled={submitting}
          onClick={onCancel}
        >
          ยกเลิก
        </Button>
        <Button type="submit" disabled={submitting}>
          {submitting ? (
            <Spinner data-icon="inline-start" />
          ) : (
            <Landmark data-icon="inline-start" />
          )}
          {submitting ? "กำลังบันทึก…" : "บันทึกบัญชีธนาคาร"}
        </Button>
      </DialogFooter>
    </form>
  );
}
