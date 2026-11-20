"use client";

import { type FormEvent, useState } from "react";
import { Gift, Info, Save } from "lucide-react";
import { AdminCard } from "@/components/admin/admin-page";
import { EnabledBadge } from "@/components/admin/settings/topup-settings-ui";
import {
  type FieldErrors,
  type SettingsDraft,
  type TopupMutationResult,
  formatPhoneNumber,
  hasFieldErrors,
  topupErrorMessage,
  validateTrueMoney,
} from "@/components/admin/settings/topup-settings-api";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
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
} from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { Switch } from "@/components/ui/switch";
import { TRUEMONEY_PENDING_EXPIRY_MS } from "@/lib/topup/constants";

type TrueMoneyDraft = SettingsDraft["truemoney"];

export function TrueMoneyCard({
  draft,
  savedPhone,
  saving,
  onChange,
  onSave,
}: {
  draft: TrueMoneyDraft;
  savedPhone: string | null;
  saving: boolean;
  onChange: (patch: Partial<TrueMoneyDraft>) => void;
  onSave: () => Promise<TopupMutationResult<unknown>>;
}) {
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState("");
  const [savedMessage, setSavedMessage] = useState("");

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (saving) return;
    const errors = validateTrueMoney(draft);
    if (hasFieldErrors(errors)) {
      setFieldErrors(errors);
      setFormError("");
      setSavedMessage("");
      return;
    }
    setFieldErrors({});
    setFormError("");
    const result = await onSave();
    if (result.ok) {
      setSavedMessage("บันทึกการตั้งค่า TrueMoney แล้ว");
    } else {
      setSavedMessage("");
      setFormError(topupErrorMessage(result.error));
    }
  }

  return (
    <AdminCard>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Gift aria-hidden className="size-4 text-primary" />
          TrueMoney Angpao
        </CardTitle>
        <CardDescription>
          รับเติมเงินผ่านลิงก์ซองอั่งเป้า TrueMoney โดยใช้เบอร์โทรศัพท์เป็นผู้รับเงิน
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={(event) => void handleSubmit(event)} noValidate>
          <FieldGroup className="gap-5">
            <Field orientation="horizontal">
              <div className="flex flex-1 flex-col gap-1">
                <FieldLabel htmlFor="topup-truemoney-enabled">
                  เปิดใช้งาน TrueMoney
                </FieldLabel>
                <FieldDescription>
                  เมื่อเปิด ลูกค้าจะเห็นช่องทางซองอั่งเป้าและส่งลิงก์เพื่อรับเงินได้
                </FieldDescription>
              </div>
              <EnabledBadge enabled={draft.enabled} />
              <Switch
                id="topup-truemoney-enabled"
                aria-label="เปิดใช้งาน TrueMoney"
                checked={draft.enabled}
                disabled={saving}
                onCheckedChange={(checked) => {
                  onChange({ enabled: checked });
                  setFieldErrors({});
                  setFormError("");
                }}
              />
            </Field>

            <Field data-invalid={Boolean(fieldErrors.phone)}>
              <FieldLabel htmlFor="topup-truemoney-phone">
                เบอร์โทรศัพท์ผู้รับซองอั่งเปา
              </FieldLabel>
              <Input
                id="topup-truemoney-phone"
                name="truemoneyPhone"
                inputMode="numeric"
                autoComplete="off"
                maxLength={10}
                placeholder="0812345678"
                value={draft.phone}
                aria-invalid={Boolean(fieldErrors.phone)}
                aria-describedby={
                  fieldErrors.phone ? "topup-truemoney-phone-error" : undefined
                }
                onChange={(event) => {
                  onChange({
                    phone: event.currentTarget.value.replace(/\D+/g, "").slice(0, 10),
                  });
                  setFieldErrors((current) => ({ ...current, phone: undefined }));
                  setFormError("");
                }}
              />
              {fieldErrors.phone ? (
                <FieldError id="topup-truemoney-phone-error">
                  {fieldErrors.phone}
                </FieldError>
              ) : (
                <FieldDescription>
                  เบอร์ 10 หลักขึ้นต้นด้วย 0 · ปัจจุบันบันทึกไว้:{" "}
                  {savedPhone ? formatPhoneNumber(savedPhone) : "ยังไม่มี"}
                </FieldDescription>
              )}
            </Field>

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
                {saving ? "กำลังบันทึก…" : "บันทึก TrueMoney"}
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

export function TrueMoneyInfoCard({ savedPhone }: { savedPhone: string | null }) {
  const pendingMinutes = Math.round(TRUEMONEY_PENDING_EXPIRY_MS / 60_000);
  return (
    <AdminCard>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Info aria-hidden className="size-4 text-primary" />
          ข้อมูลช่องทาง TrueMoney
        </CardTitle>
        <CardDescription>
          ข้อมูลประกอบการตั้งค่า ไม่ได้แก้ไขได้จากหน้านี้
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <dl className="grid gap-3 text-sm">
          <div className="flex flex-col gap-1">
            <dt className="text-muted-foreground">รูปแบบรับเงิน</dt>
            <dd>ลิงก์ซองอั่งเป้า TrueMoney (Angpao)</dd>
          </div>
          <div className="flex flex-col gap-1">
            <dt className="text-muted-foreground">ผู้รับเงินปัจจุบัน</dt>
            <dd className="font-mono break-all">
              {savedPhone ? formatPhoneNumber(savedPhone) : "ยังไม่ได้ตั้งค่า"}
            </dd>
          </div>
          <div className="flex flex-col gap-1">
            <dt className="text-muted-foreground">เวลารอชำระ</dt>
            <dd>{pendingMinutes} นาทีก่อนรายการหมดอายุ</dd>
          </div>
          <div className="flex flex-col gap-1">
            <dt className="text-muted-foreground">เส้นทางรับชำระ</dt>
            <dd className="font-mono text-xs break-all">
              POST /api/topups/truemoney
            </dd>
          </div>
        </dl>
        <Alert>
          <Info aria-hidden />
          <AlertTitle>การเชื่อมต่อผู้ให้บริการ</AlertTitle>
          <AlertDescription>
            บริการซองอั่งเป้าเชื่อมต่อผ่านค่าที่ตั้งไว้บนเซิร์ฟเวอร์
            ไม่ได้เก็บ API Key ในหน้านี้ และหน้านี้จะไม่แสดงข้อมูลลับใด ๆ
          </AlertDescription>
        </Alert>
      </CardContent>
    </AdminCard>
  );
}
