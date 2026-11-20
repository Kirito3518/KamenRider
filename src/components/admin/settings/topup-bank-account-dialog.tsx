"use client";

import { type ChangeEvent, type FormEvent, useState } from "react";
import { Landmark, Upload } from "lucide-react";
import { BANK_PRESETS, getBankPreset } from "@/lib/topup/banks";
import type { BankAccountAdmin } from "@/lib/topup/types";
import {
  type BankAccountFormValues,
  type FieldErrors,
  type TopupMutationResult,
  LOGO_FILE_ACCEPT,
  MAX_LOGO_BYTES,
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
  FieldTitle,
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

const CUSTOM_BANK_VALUE = "custom";

const BANK_SELECT_ITEMS = [
  ...BANK_PRESETS.map((preset) => ({
    value: preset.code,
    label: `${preset.shortName} · ${preset.name} (${preset.code})`,
  })),
  { value: CUSTOM_BANK_VALUE, label: "ธนาคารอื่น (กำหนดรหัสเอง)" },
];

function defaultForm(): BankAccountFormValues {
  const preset = BANK_PRESETS[0];
  return {
    bankCode: preset.code,
    bankName: preset.name,
    accountNumber: "",
    accountName: "",
    logoKey: preset.logoKey,
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

function initialBankSelection(account: BankAccountAdmin | null): string {
  if (!account) return BANK_PRESETS[0].code;
  return getBankPreset(account.bankCode) ? account.bankCode : CUSTOM_BANK_VALUE;
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
      <DialogContent className="max-h-[88dvh] overflow-y-auto sm:max-w-2xl">
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
  const [bankSelection, setBankSelection] = useState(() =>
    initialBankSelection(account),
  );
  const [errors, setErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState("");
  const [logoError, setLogoError] = useState("");
  const [submitting, setSubmitting] = useState(false);

  function update(patch: Partial<BankAccountFormValues>) {
    setValues((current) => ({ ...current, ...patch }));
  }

  function handleBankChange(next: string | null) {
    const selection = next ?? CUSTOM_BANK_VALUE;
    setBankSelection(selection);
    const preset = getBankPreset(selection);
    if (preset) {
      update({
        bankCode: preset.code,
        bankName: preset.name,
        logoKey: preset.logoKey,
        logoDataUrl: null,
      });
    }
    setErrors((current) => ({
      ...current,
      bankCode: undefined,
      bankName: undefined,
    }));
    setFormError("");
  }

  function handleFileChange(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0];
    event.currentTarget.value = "";
    if (!file) return;
    setLogoError("");
    if (!["image/png", "image/jpeg", "image/webp"].includes(file.type)) {
      setLogoError("โลโก้ต้องเป็นรูป PNG, JPEG หรือ WebP");
      return;
    }
    if (file.size > MAX_LOGO_BYTES) {
      setLogoError("โลโก้ต้องมีขนาดไม่เกิน 32 KB");
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      const result = typeof reader.result === "string" ? reader.result : "";
      if (!result.startsWith("data:image/")) {
        setLogoError("อ่านไฟล์โลโก้ไม่สำเร็จ กรุณาเลือกไฟล์ใหม่");
        return;
      }
      update({ logoKey: null, logoDataUrl: result });
      setErrors((current) => ({ ...current, logo: undefined }));
    };
    reader.onerror = () => {
      setLogoError("อ่านไฟล์โลโก้ไม่สำเร็จ กรุณาเลือกไฟล์ใหม่");
    };
    reader.readAsDataURL(file);
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
      logoKey: values.logoKey,
      logoDataUrl: values.logoDataUrl,
    };
    const nextErrors = validateBankAccountForm(nextValues);
    if (hasFieldErrors(nextErrors)) {
      setErrors(nextErrors);
      setFormError("");
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

  const presetLocked = bankSelection !== CUSTOM_BANK_VALUE;

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
            items={BANK_SELECT_ITEMS}
            value={bankSelection}
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
                {BANK_PRESETS.map((preset) => (
                  <SelectItem key={preset.code} value={preset.code}>
                    <span className="flex items-center gap-2">
                      <BankLogoTile
                        bankCode={preset.code}
                        bankName={preset.name}
                        logoKey={preset.logoKey}
                        className="size-6 rounded-md text-[8px]"
                      />
                      <span>
                        {preset.shortName} · {preset.name} ({preset.code})
                      </span>
                    </span>
                  </SelectItem>
                ))}
                <SelectItem value={CUSTOM_BANK_VALUE}>
                  ธนาคารอื่น (กำหนดรหัสเอง)
                </SelectItem>
              </SelectGroup>
            </SelectContent>
          </Select>
          <FieldDescription>
            เลือกธนาคารเพื่อใช้รหัสและโลโก้มาตรฐาน หรือเลือก &quot;ธนาคารอื่น&quot;
            เพื่อกรอกเอง
          </FieldDescription>
        </Field>

        <div className="grid gap-4 sm:grid-cols-2">
          <Field data-invalid={Boolean(errors.bankCode)}>
            <FieldLabel htmlFor="topup-bank-code">รหัสธนาคาร (3 หลัก)</FieldLabel>
            <Input
              id="topup-bank-code"
              name="bankCode"
              inputMode="numeric"
              maxLength={3}
              value={values.bankCode}
              readOnly={presetLocked}
              aria-invalid={Boolean(errors.bankCode)}
              onChange={(event) => {
                update({
                  bankCode: event.currentTarget.value
                    .replace(/\D+/g, "")
                    .slice(0, 3),
                });
                setErrors((current) => ({ ...current, bankCode: undefined }));
              }}
            />
            {errors.bankCode ? <FieldError>{errors.bankCode}</FieldError> : null}
          </Field>
          <Field data-invalid={Boolean(errors.bankName)}>
            <FieldLabel htmlFor="topup-bank-name">ชื่อธนาคาร</FieldLabel>
            <Input
              id="topup-bank-name"
              name="bankName"
              maxLength={100}
              value={values.bankName}
              readOnly={presetLocked}
              aria-invalid={Boolean(errors.bankName)}
              onChange={(event) => {
                update({ bankName: event.currentTarget.value });
                setErrors((current) => ({ ...current, bankName: undefined }));
              }}
            />
            {errors.bankName ? <FieldError>{errors.bankName}</FieldError> : null}
          </Field>
        </div>

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

        <Field data-invalid={Boolean(errors.logo || logoError)}>
          <FieldTitle>โลโก้ธนาคาร</FieldTitle>
          <FieldDescription>
            เลือกโลโก้สำเร็จรูป หรืออัปโหลด PNG/JPEG/WebP ไม่เกิน 32 KB
          </FieldDescription>
          <div
            role="group"
            aria-label="เลือกโลโก้ธนาคารสำเร็จรูป"
            className="grid grid-cols-4 gap-2 sm:grid-cols-6"
          >
            {BANK_PRESETS.map((preset) => {
              const selected =
                !values.logoDataUrl && values.logoKey === preset.logoKey;
              return (
                <button
                  key={preset.logoKey}
                  type="button"
                  aria-label={`ใช้โลโก้ ${preset.name}`}
                  aria-pressed={selected}
                  onClick={() => {
                    update({ logoKey: preset.logoKey, logoDataUrl: null });
                    setErrors((current) => ({ ...current, logo: undefined }));
                    setLogoError("");
                  }}
                  className={
                    selected
                      ? "flex h-16 flex-col items-center justify-center gap-1 rounded-xl border-2 border-primary bg-primary/5 p-1"
                      : "flex h-16 flex-col items-center justify-center gap-1 rounded-xl border border-border p-1 hover:bg-muted/60"
                  }
                >
                  <BankLogoTile
                    bankCode={preset.code}
                    bankName={preset.name}
                    logoKey={preset.logoKey}
                    className="size-8"
                  />
                  <span className="text-[10px] text-muted-foreground">
                    {preset.shortName}
                  </span>
                </button>
              );
            })}
          </div>

          <div className="flex flex-wrap items-center gap-3">
            <label
              htmlFor="topup-bank-logo-file"
              className="inline-flex cursor-pointer items-center gap-2 rounded-full border border-border px-3 py-1.5 text-sm hover:bg-muted"
            >
              <Upload aria-hidden className="size-4" />
              อัปโหลดโลโก้
              <input
                id="topup-bank-logo-file"
                name="logoFile"
                type="file"
                accept={LOGO_FILE_ACCEPT}
                className="sr-only"
                onChange={handleFileChange}
              />
            </label>
            {values.logoDataUrl ? (
              <>
                <BankLogoTile
                  bankCode={values.bankCode}
                  bankName={values.bankName}
                  logoDataUrl={values.logoDataUrl}
                />
                <span className="text-xs text-muted-foreground">
                  ใช้รูปที่อัปโหลด
                </span>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={() =>
                    update({
                      logoDataUrl: null,
                      logoKey: getBankPreset(values.bankCode)?.logoKey ?? null,
                    })
                  }
                >
                  ลบรูปที่อัปโหลด
                </Button>
              </>
            ) : null}
          </div>
          {logoError ? <FieldError>{logoError}</FieldError> : null}
          {errors.logo ? <FieldError>{errors.logo}</FieldError> : null}
        </Field>

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
