"use client";

import { type FormEvent, useState } from "react";
import { Info, QrCode, Save, ShieldCheck } from "lucide-react";
import { AdminCard } from "@/components/admin/admin-page";
import {
  ConfiguredBadge,
  EnabledBadge,
} from "@/components/admin/settings/topup-settings-ui";
import {
  type FieldErrors,
  type SettingsDraft,
  type TopupMutationResult,
  formatPromptpayNumber,
  hasFieldErrors,
  topupErrorMessage,
  validatePromptPay,
} from "@/components/admin/settings/topup-settings-api";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Field,
  FieldDescription,
  FieldError,
  FieldGroup,
  FieldLabel,
  FieldTitle,
} from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import type { PromptpayType } from "@/lib/topup/constants";
import type { TopupSettingsAdmin } from "@/lib/topup/types";

type PromptPayDraft = SettingsDraft["promptpay"];

const TYPE_LABELS: Record<PromptpayType, string> = {
  msisdn: "เบอร์โทรศัพท์",
  natId: "เลขบัตรประชาชน",
};

export function PromptPayCard({
  draft,
  saved,
  bankTransferEnabled,
  easySlipConfigured,
  saving,
  onChange,
  onSave,
}: {
  draft: PromptPayDraft;
  saved: TopupSettingsAdmin["promptpay"];
  bankTransferEnabled: boolean;
  easySlipConfigured: boolean;
  saving: boolean;
  onChange: (patch: Partial<PromptPayDraft>) => void;
  onSave: () => Promise<TopupMutationResult<unknown>>;
}) {
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState("");
  const [savedMessage, setSavedMessage] = useState("");

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (saving) return;
    const errors = validatePromptPay(draft, { easySlipConfigured });
    if (hasFieldErrors(errors)) {
      setFieldErrors(errors);
      setFormError(errors.form ?? "");
      setSavedMessage("");
      return;
    }
    setFieldErrors({});
    setFormError("");
    const result = await onSave();
    if (result.ok) {
      setSavedMessage("บันทึกการตั้งค่า PromptPay QR แล้ว");
    } else {
      setSavedMessage("");
      setFormError(topupErrorMessage(result.error));
    }
  }

  return (
    <AdminCard>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <QrCode aria-hidden className="size-4 text-primary" />
          PromptPay QR
        </CardTitle>
        <CardDescription>
          สร้าง QR พร้อมเพย์ให้ลูกค้าสแกนจ่าย และตรวจสลิปอัตโนมัติผ่าน EasySlip
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={(event) => void handleSubmit(event)} noValidate>
          <FieldGroup className="gap-5">
            <Field orientation="horizontal">
              <div className="flex flex-1 flex-col gap-1">
                <FieldLabel htmlFor="topup-promptpay-enabled">
                  เปิดใช้งาน PromptPay QR
                </FieldLabel>
                <FieldDescription>
                  เปิดได้ครั้งละหนึ่งรูปแบบร่วมกับช่องทางโอนผ่านบัญชีธนาคาร
                </FieldDescription>
              </div>
              <EnabledBadge enabled={draft.enabled} />
              <Switch
                id="topup-promptpay-enabled"
                aria-label="เปิดใช้งาน PromptPay QR"
                checked={draft.enabled}
                disabled={saving}
                onCheckedChange={(checked) => {
                  onChange({ enabled: checked });
                  setFieldErrors({});
                  setFormError("");
                }}
              />
            </Field>

            <Field>
              <FieldTitle>ประเภทหมายเลข PromptPay</FieldTitle>
              <ToggleGroup
                aria-label="ประเภทหมายเลข PromptPay"
                className="w-full sm:w-fit"
                value={[draft.type]}
                onValueChange={(values) => {
                  const next = values[0];
                  if (next === "msisdn" || next === "natId") {
                    onChange({ type: next });
                    setFieldErrors((current) => ({
                      ...current,
                      number: undefined,
                    }));
                    setFormError("");
                  }
                }}
              >
                <ToggleGroupItem value="msisdn" className="flex-1 sm:flex-none">
                  เบอร์โทรศัพท์
                </ToggleGroupItem>
                <ToggleGroupItem value="natId" className="flex-1 sm:flex-none">
                  เลขบัตรประชาชน
                </ToggleGroupItem>
              </ToggleGroup>
              <FieldDescription>
                ต้องตรงกับประเภทที่ผูกกับบัญชีพร้อมเพย์ของร้าน
              </FieldDescription>
            </Field>

            <Field data-invalid={Boolean(fieldErrors.number)}>
              <FieldLabel htmlFor="topup-promptpay-number">
                {draft.type === "msisdn"
                  ? "หมายเลข PromptPay (เบอร์โทรศัพท์)"
                  : "หมายเลข PromptPay (เลขบัตรประชาชน)"}
              </FieldLabel>
              <Input
                id="topup-promptpay-number"
                name="promptpayNumber"
                inputMode="numeric"
                autoComplete="off"
                maxLength={draft.type === "msisdn" ? 10 : 13}
                placeholder={draft.type === "msisdn" ? "0812345678" : "1234567890123"}
                value={draft.number}
                aria-invalid={Boolean(fieldErrors.number)}
                aria-describedby={
                  fieldErrors.number ? "topup-promptpay-number-error" : undefined
                }
                onChange={(event) => {
                  const digits = event.currentTarget.value
                    .replace(/\D+/g, "")
                    .slice(0, draft.type === "msisdn" ? 10 : 13);
                  onChange({ number: digits });
                  setFieldErrors((current) => ({ ...current, number: undefined }));
                  setFormError("");
                }}
              />
              {fieldErrors.number ? (
                <FieldError id="topup-promptpay-number-error">
                  {fieldErrors.number}
                </FieldError>
              ) : (
                <FieldDescription>
                  บันทึกล่าสุด: {formatPromptpayNumber(saved.type, saved.number)}
                </FieldDescription>
              )}
            </Field>

            <Field data-invalid={Boolean(fieldErrors.accountName)}>
              <FieldLabel htmlFor="topup-promptpay-account-name">
                ชื่อบัญชีสำหรับตรวจสอบสลิป
              </FieldLabel>
              <Input
                id="topup-promptpay-account-name"
                name="promptpayAccountName"
                autoComplete="off"
                maxLength={120}
                placeholder="ชื่อ-นามสกุลเจ้าของบัญชี"
                value={draft.accountName}
                aria-invalid={Boolean(fieldErrors.accountName)}
                aria-describedby={
                  fieldErrors.accountName
                    ? "topup-promptpay-account-name-error"
                    : undefined
                }
                onChange={(event) => {
                  onChange({ accountName: event.currentTarget.value });
                  setFieldErrors((current) => ({
                    ...current,
                    accountName: undefined,
                  }));
                  setFormError("");
                }}
              />
              {fieldErrors.accountName ? (
                <FieldError id="topup-promptpay-account-name-error">
                  {fieldErrors.accountName}
                </FieldError>
              ) : (
                <FieldDescription>
                  ใช้ตรวจสอบชื่อผู้รับเงินในสลิปโอนเงินให้ตรงกับบัญชีจริง
                </FieldDescription>
              )}
            </Field>

            {!easySlipConfigured ? (
              <Alert>
                <Info aria-hidden />
                <AlertTitle>ต้องตั้งค่า EasySlip API Key ก่อน</AlertTitle>
                <AlertDescription>
                  PromptPay QR ใช้ EasySlip ตรวจสลิปอัตโนมัติ
                  กรุณาตั้งค่าคีย์ในแท็บ &quot;ตรวจสอบสลิป&quot; ก่อนเปิดช่องทางนี้
                </AlertDescription>
              </Alert>
            ) : null}

            {bankTransferEnabled ? (
              <Alert>
                <Info aria-hidden />
                <AlertTitle>ช่องทางโอนผ่านบัญชีถูกเปิดอยู่</AlertTitle>
                <AlertDescription>
                  การเปิด PromptPay QR จะปิดช่องทางโอนผ่านบัญชีธนาคารให้อัตโนมัติ
                  เพราะระบบเปิดได้ครั้งละหนึ่งรูปแบบ
                </AlertDescription>
              </Alert>
            ) : null}

            {formError ? (
              <Alert variant="destructive">
                <AlertTitle>บันทึกไม่สำเร็จ</AlertTitle>
                <AlertDescription>{formError}</AlertDescription>
              </Alert>
            ) : null}

            <div className="flex flex-wrap items-center gap-3 border-t pt-4">
              <Button type="submit" disabled={saving}>
                {saving ? (
                  <Spinner data-icon="inline-start" />
                ) : (
                  <Save data-icon="inline-start" />
                )}
                {saving ? "กำลังบันทึก…" : "บันทึก PromptPay QR"}
              </Button>
              <span
                role="status"
                aria-live="polite"
                className="text-sm text-muted-foreground"
              >
                {savedMessage}
              </span>
            </div>
          </FieldGroup>
        </form>
      </CardContent>
    </AdminCard>
  );
}

export function PromptPayReceiverCard({
  draft,
  saved,
  easySlipConfigured,
}: {
  draft: PromptPayDraft;
  saved: TopupSettingsAdmin["promptpay"];
  easySlipConfigured: boolean;
}) {
  return (
    <AdminCard>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <ShieldCheck aria-hidden className="size-4 text-primary" />
          ผู้รับเงินปลายทาง
        </CardTitle>
        <CardDescription>
          ข้อมูลที่ลูกค้าจะเห็นตอนสร้าง QR พร้อมเพย์
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <dl className="grid gap-3 text-sm">
          <div className="flex items-center justify-between gap-3">
            <dt className="text-muted-foreground">สถานะ</dt>
            <dd>
              <EnabledBadge enabled={saved.enabled} />
            </dd>
          </div>
          <div className="flex flex-col gap-1">
            <dt className="text-muted-foreground">ประเภทหมายเลข</dt>
            <dd className="flex flex-wrap items-center gap-2">
              <span>{TYPE_LABELS[saved.type]}</span>
              <Badge variant="outline">
                {saved.type === "msisdn" ? "MSISDN" : "National ID"}
              </Badge>
            </dd>
          </div>
          <div className="flex flex-col gap-1">
            <dt className="text-muted-foreground">หมายเลขผู้รับ (แสดงเต็มสำหรับผู้ดูแล)</dt>
            <dd className="font-mono break-all">
              {formatPromptpayNumber(saved.type, saved.number)}
            </dd>
          </div>
          <div className="flex flex-col gap-1">
            <dt className="text-muted-foreground">ชื่อบัญชี</dt>
            <dd>{saved.accountName || "ยังไม่ได้ตั้งค่า"}</dd>
          </div>
          <div className="flex flex-col gap-1">
            <dt className="text-muted-foreground">ตัวอย่างที่กำลังแก้ไข</dt>
            <dd className="font-mono break-all">
              {formatPromptpayNumber(draft.type, draft.number)}
            </dd>
          </div>
        </dl>
        <div className="flex flex-wrap items-center gap-2 border-t pt-4">
          <span className="text-sm text-muted-foreground">EasySlip API Key</span>
          <ConfiguredBadge configured={easySlipConfigured} />
        </div>
        <Alert>
          <Info aria-hidden />
          <AlertTitle>เปิดได้ครั้งละหนึ่งรูปแบบ</AlertTitle>
          <AlertDescription>
            PromptPay QR และการโอนผ่านบัญชีธนาคารใช้ร่วมกันไม่ได้
            ระบบจะสลับช่องทางให้อัตโนมัติเมื่อบันทึก
          </AlertDescription>
        </Alert>
      </CardContent>
    </AdminCard>
  );
}
