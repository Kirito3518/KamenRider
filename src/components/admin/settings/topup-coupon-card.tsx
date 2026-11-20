"use client";

import { type FormEvent, useState } from "react";
import { Gift, Save, TicketPercent } from "lucide-react";
import { AdminCard } from "@/components/admin/admin-page";
import { EnabledBadge } from "@/components/admin/settings/topup-settings-ui";
import {
  type FieldErrors,
  type SettingsDraft,
  type TopupMutationResult,
  hasFieldErrors,
  topupErrorMessage,
  validateCoupon,
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
import { Spinner } from "@/components/ui/spinner";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";

type CouponDraft = SettingsDraft["coupon"];

const DEFAULT_COUPON_MESSAGE = "คูปองยังไม่เปิดให้บริการในขณะนี้";

export function CouponCard({
  draft,
  saving,
  onChange,
  onSave,
}: {
  draft: CouponDraft;
  saving: boolean;
  onChange: (patch: Partial<CouponDraft>) => void;
  onSave: () => Promise<TopupMutationResult<unknown>>;
}) {
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState("");
  const [savedMessage, setSavedMessage] = useState("");

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (saving) return;
    const errors = validateCoupon(draft.message);
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
      setSavedMessage("บันทึกการตั้งค่าคูปองแล้ว");
    } else {
      setSavedMessage("");
      setFormError(topupErrorMessage(result.error));
    }
  }

  return (
    <AdminCard>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <TicketPercent aria-hidden className="size-4 text-primary" />
          คูปอง
        </CardTitle>
        <CardDescription>
          เปิดช่องทางคูปองและกำหนดข้อความแจ้งลูกค้าเมื่อยังไม่พร้อมให้บริการ
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={(event) => void handleSubmit(event)} noValidate>
          <FieldGroup className="gap-5">
            <Field orientation="horizontal">
              <div className="flex flex-1 flex-col gap-1">
                <FieldLabel htmlFor="topup-coupon-enabled">
                  เปิดใช้งานคูปอง
                </FieldLabel>
                <FieldDescription>
                  ลูกค้าจะเห็นช่องทางคูปองในหน้าเติมเงินและใช้โค้ดเมื่อเปิดใช้งาน
                </FieldDescription>
              </div>
              <EnabledBadge enabled={draft.enabled} />
              <Switch
                id="topup-coupon-enabled"
                aria-label="เปิดใช้งานคูปอง"
                checked={draft.enabled}
                disabled={saving}
                onCheckedChange={(checked) => {
                  onChange({ enabled: checked });
                  setFormError("");
                }}
              />
            </Field>

            <Field data-invalid={Boolean(fieldErrors.message)}>
              <FieldLabel htmlFor="topup-coupon-message">
                ข้อความคูปองสำหรับลูกค้า
              </FieldLabel>
              <Textarea
                id="topup-coupon-message"
                name="couponMessage"
                maxLength={200}
                placeholder={DEFAULT_COUPON_MESSAGE}
                value={draft.message}
                aria-invalid={Boolean(fieldErrors.message)}
                aria-describedby="topup-coupon-message-hint"
                onChange={(event) => {
                  onChange({ message: event.currentTarget.value });
                  setFieldErrors((current) => ({
                    ...current,
                    message: undefined,
                  }));
                  setFormError("");
                }}
              />
              {fieldErrors.message ? (
                <FieldError id="topup-coupon-message-error">
                  {fieldErrors.message}
                </FieldError>
              ) : (
                <FieldDescription id="topup-coupon-message-hint">
                  ไม่เกิน 200 ตัวอักษร · ใช้แสดงเมื่อช่องทางคูปองไม่พร้อมหรือไม่มีโค้ด
                </FieldDescription>
              )}
              <div className="flex justify-end">
                <span
                  className="text-xs text-muted-foreground tabular-nums"
                  aria-live="off"
                >
                  {draft.message.length}/200
                </span>
              </div>
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
                {saving ? "กำลังบันทึก…" : "บันทึกคูปอง"}
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

export function CouponPreviewCard({ draft }: { draft: CouponDraft }) {
  const message = draft.message.trim() || DEFAULT_COUPON_MESSAGE;
  return (
    <AdminCard>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Gift aria-hidden className="size-4 text-primary" />
          ตัวอย่างข้อความคูปอง
        </CardTitle>
        <CardDescription>
          ตัวอย่างข้อความที่ลูกค้าจะเห็นในหน้าเติมเงิน
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="rounded-2xl border border-dashed bg-muted/40 p-4">
          <div className="flex items-center justify-between gap-3">
            <span className="flex items-center gap-2 text-sm font-medium">
              <TicketPercent aria-hidden className="size-4" />
              คูปองเติมเงิน
            </span>
            <EnabledBadge enabled={draft.enabled} />
          </div>
          <p className="mt-3 text-sm whitespace-pre-wrap text-muted-foreground">
            {message}
          </p>
        </div>
        <Alert>
          <AlertTitle>หมายเหตุ</AlertTitle>
          <AlertDescription>
            หากข้อความว่าง ระบบจะใช้ข้อความเริ่มต้น
            &quot;{DEFAULT_COUPON_MESSAGE}&quot; ให้อัตโนมัติ
          </AlertDescription>
        </Alert>
      </CardContent>
    </AdminCard>
  );
}
