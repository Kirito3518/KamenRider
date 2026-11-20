"use client";

import { useState } from "react";
import {
  CircleCheck,
  CircleX,
  KeyRound,
  RefreshCw,
  Save,
  ShieldCheck,
  Trash2,
} from "lucide-react";
import { AdminCard } from "@/components/admin/admin-page";
import {
  type ProviderUpdatePayload,
  type TopupMutationResult,
  PROVIDER_LABELS,
  formatAdminDateTime,
  providerLabel,
  providerLastTestSummary,
  topupErrorMessage,
  validateProviderSave,
} from "@/components/admin/settings/topup-settings-api";
import {
  ConfiguredBadge,
  LastTestBadge,
} from "@/components/admin/settings/topup-settings-ui";
import {
  Alert,
  AlertDescription,
  AlertTitle,
} from "@/components/ui/alert";
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
import { Button } from "@/components/ui/button";
import {
  CardAction,
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
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import type { SlipProvider } from "@/lib/topup/constants";
import type {
  ProviderAdminStatus,
  ProviderTestResult,
} from "@/lib/topup/types";

export type ProviderMutationResult = TopupMutationResult<unknown>;

const PROVIDER_DESCRIPTIONS: Record<SlipProvider, string> = {
  easyslip: "ตรวจสลิปโอนเงินผ่าน EasySlip และใช้ร่วมกับ PromptPay QR",
  slipok: "ตรวจสลิปโอนเงินผ่าน SlipOk พร้อม Branch ID ของร้าน",
};

const DETAIL_LABELS: Record<string, string> = {
  remainingQuota: "โควตาคงเหลือ",
  usedQuota: "โควตาที่ใช้แล้ว",
  maxQuota: "โควตาทั้งหมด",
  expiredAt: "วันหมดอายุคีย์",
  quota: "โควตาคงเหลือ",
  overQuota: "ใช้เกินโควตา",
  code: "รหัสข้อผิดพลาด",
  httpStatus: "HTTP Status",
};

function formatDetailValue(key: string, value: string | number): string {
  if (key === "overQuota") return value === 1 || value === "1" ? "ใช่" : "ไม่ใช่";
  if (key === "expiredAt" && typeof value === "number") {
    return formatAdminDateTime(value);
  }
  return String(value);
}

export function SlipProviderSelectionCard({
  draftProvider,
  savedProvider,
  pendingVerifications,
  saving,
  onChange,
  onSave,
}: {
  draftProvider: SlipProvider;
  savedProvider: SlipProvider;
  pendingVerifications: number;
  saving: boolean;
  onChange: (provider: SlipProvider) => void;
  onSave: () => Promise<ProviderMutationResult>;
}) {
  const [formError, setFormError] = useState("");
  const [savedMessage, setSavedMessage] = useState("");
  const changed = draftProvider !== savedProvider;

  async function handleSave() {
    if (saving || !changed) return;
    setFormError("");
    setSavedMessage("");
    const result = await onSave();
    if (result.ok) {
      setSavedMessage(`เลือกใช้ ${providerLabel(draftProvider)} แล้ว`);
    } else {
      setFormError(topupErrorMessage(result.error));
    }
  }

  return (
    <AdminCard>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <ShieldCheck aria-hidden className="size-4 text-primary" />
          ผู้ให้บริการตรวจสลิป
        </CardTitle>
        <CardDescription>
          เลือกผู้ให้บริการหนึ่งรายสำหรับตรวจสอบสลิปของช่องทางที่ต้องโอนเงิน
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <ToggleGroup
          aria-label="เลือกผู้ให้บริการตรวจสลิป"
          className="w-full sm:w-fit"
          value={[draftProvider]}
          onValueChange={(values) => {
            const next = values[0];
            if (next === "easyslip" || next === "slipok") {
              onChange(next);
              setFormError("");
            }
          }}
        >
          <ToggleGroupItem value="easyslip" className="flex-1 sm:flex-none">
            EasySlip
          </ToggleGroupItem>
          <ToggleGroupItem value="slipok" className="flex-1 sm:flex-none">
            SlipOk
          </ToggleGroupItem>
        </ToggleGroup>

        {pendingVerifications > 0 ? (
          <Alert>
            <RefreshCw aria-hidden />
            <AlertTitle>มีรายการรอตรวจสลิป {pendingVerifications} รายการ</AlertTitle>
            <AlertDescription>
              ยังเปลี่ยนผู้ให้บริการไม่ได้จนกว่ารายการที่รอตรวจจะเสร็จสิ้น
              เพื่อไม่ให้ผลตรวจคลาดเคลื่อน
            </AlertDescription>
          </Alert>
        ) : null}

        {formError ? (
          <Alert variant="destructive">
            <AlertTitle>บันทึกไม่สำเร็จ</AlertTitle>
            <AlertDescription>{formError}</AlertDescription>
          </Alert>
        ) : null}

        <div className="flex flex-wrap items-center gap-3">
          <Button
            type="button"
            disabled={saving || !changed}
            onClick={() => void handleSave()}
          >
            {saving ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <Save data-icon="inline-start" />
            )}
            {saving ? "กำลังบันทึก…" : "บันทึกผู้ให้บริการ"}
          </Button>
          <span role="status" aria-live="polite" className="text-sm text-muted-foreground">
            {savedMessage}
          </span>
        </div>
        <FieldDescription>
          ผู้ให้บริการที่เลือกใช้เฉพาะการตรวจสลิป ไม่ได้เปลี่ยนวิธีรับเงินของลูกค้า
        </FieldDescription>
      </CardContent>
    </AdminCard>
  );
}

export function SlipProviderCredentialCard({
  provider,
  status,
  selected,
  busyAction,
  onSave,
  onClear,
  onTest,
}: {
  provider: SlipProvider;
  status: ProviderAdminStatus;
  selected: boolean;
  busyAction: "save" | "clear" | "test" | null;
  onSave: (
    payload: Omit<ProviderUpdatePayload, "expectedUpdatedAt">,
  ) => Promise<ProviderMutationResult>;
  onClear: () => Promise<ProviderMutationResult>;
  onTest: () => Promise<TopupMutationResult<ProviderTestResult>>;
}) {
  const isEasySlip = provider === "easyslip";
  const [apiKey, setApiKey] = useState("");
  const [branchId, setBranchId] = useState(() => status.branchId ?? "");
  const [fieldErrors, setFieldErrors] = useState<
    Record<string, string | undefined>
  >({});
  const [formError, setFormError] = useState("");
  const [savedMessage, setSavedMessage] = useState("");
  const [clearOpen, setClearOpen] = useState(false);
  const [testResult, setTestResult] = useState<ProviderTestResult | null>(null);
  const busy = busyAction !== null;

  const branchChanged = !isEasySlip && branchId.trim() !== (status.branchId ?? "");
  const canSave = isEasySlip ? apiKey.trim().length > 0 : apiKey.trim().length > 0 || branchChanged;

  async function handleSave() {
    if (busy) return;
    const errors = validateProviderSave(provider, { apiKey, branchId });
    if (Object.values(errors).some(Boolean)) {
      setFieldErrors(errors);
      setFormError("");
      setSavedMessage("");
      return;
    }
    setFieldErrors({});
    setFormError("");
    setSavedMessage("");
    const payload: Omit<ProviderUpdatePayload, "expectedUpdatedAt"> = isEasySlip
      ? { apiKey: apiKey.trim() }
      : {
          ...(apiKey.trim() ? { apiKey: apiKey.trim() } : {}),
          branchId: branchId.trim(),
        };
    const result = await onSave(payload);
    if (result.ok) {
      setApiKey("");
      setSavedMessage(
        isEasySlip ? "บันทึก EasySlip API Key แล้ว" : "บันทึกการตั้งค่า SlipOk แล้ว",
      );
    } else {
      setFormError(topupErrorMessage(result.error));
    }
  }

  async function handleClear() {
    if (busy) return;
    setClearOpen(false);
    setFormError("");
    setSavedMessage("");
    const result = await onClear();
    if (result.ok) {
      setSavedMessage("ลบ API Key แล้ว ระบบจะหยุดตรวจสลิปจนกว่าจะตั้งค่าใหม่");
    } else {
      setFormError(topupErrorMessage(result.error));
    }
  }

  async function handleTest() {
    if (busy || !status.configured) return;
    setFormError("");
    setSavedMessage("");
    setTestResult(null);
    const result = await onTest();
    if (result.ok) {
      setTestResult(result.data);
    } else {
      setFormError(topupErrorMessage(result.error));
    }
  }

  return (
    <AdminCard>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <KeyRound aria-hidden className="size-4 text-primary" />
          {providerLabel(provider)}
          {selected ? (
            <span className="text-xs font-normal text-muted-foreground">
              (ผู้ให้บริการที่เลือก)
            </span>
          ) : null}
        </CardTitle>
        <CardDescription>{PROVIDER_DESCRIPTIONS[provider]}</CardDescription>
        <CardAction className="flex flex-wrap gap-2">
          <ConfiguredBadge configured={status.configured} />
          <LastTestBadge
            lastTestAt={status.lastTestAt}
            lastTestOk={status.lastTestOk}
          />
        </CardAction>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="rounded-xl bg-muted/50 p-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="text-sm text-muted-foreground">API Key ที่บันทึกไว้</span>
            <code className="font-mono text-sm" aria-label={`${PROVIDER_LABELS[provider]} API Key ที่บันทึกไว้`}>
              {status.maskedKey || "—"}
            </code>
          </div>
          <p className="mt-1 text-xs text-muted-foreground">
            ระบบแสดงเฉพาะ 4 ตัวท้ายของคีย์ และไม่เคยส่งคีย์เต็มกลับมาให้เบราว์เซอร์
          </p>
        </div>

        <p className="text-sm text-muted-foreground">
          {providerLastTestSummary(status)}
        </p>

        {!selected ? (
          <Alert>
            <AlertDescription>
              ผู้ให้บริการรายนี้ยังไม่ได้เลือกใช้งาน การตรวจสลิปจะใช้ผู้ให้บริการที่เลือกในแท็บนี้เท่านั้น
            </AlertDescription>
          </Alert>
        ) : null}

        <FieldGroup className="gap-4">
          <Field data-invalid={Boolean(fieldErrors.apiKey)}>
            <FieldLabel htmlFor={`topup-${provider}-api-key`}>
              {isEasySlip ? "API Key ใหม่ (UUID)" : "API Key ใหม่"}
            </FieldLabel>
            <Input
              id={`topup-${provider}-api-key`}
              name={`${provider}ApiKey`}
              type="password"
              autoComplete="new-password"
              maxLength={300}
              placeholder={isEasySlip ? "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" : "API Key ของ SlipOk"}
              value={apiKey}
              aria-invalid={Boolean(fieldErrors.apiKey)}
              onChange={(event) => {
                setApiKey(event.currentTarget.value);
                setFieldErrors((current) => ({ ...current, apiKey: undefined }));
                setFormError("");
              }}
            />
            {fieldErrors.apiKey ? (
              <FieldError>{fieldErrors.apiKey}</FieldError>
            ) : (
              <FieldDescription>
                {isEasySlip
                  ? "คีย์ของ EasySlip ต้องอยู่ในรูปแบบ UUID"
                  : "ห้ามมีช่องว่างภายในคีย์"}
              </FieldDescription>
            )}
          </Field>

          {!isEasySlip ? (
            <Field data-invalid={Boolean(fieldErrors.branchId)}>
              <FieldLabel htmlFor="topup-slipok-branch-id">Branch ID</FieldLabel>
              <Input
                id="topup-slipok-branch-id"
                name="slipokBranchId"
                autoComplete="off"
                maxLength={64}
                placeholder="เช่น 12345"
                value={branchId}
                aria-invalid={Boolean(fieldErrors.branchId)}
                onChange={(event) => {
                  setBranchId(event.currentTarget.value);
                  setFieldErrors((current) => ({
                    ...current,
                    branchId: undefined,
                  }));
                  setFormError("");
                }}
              />
              {fieldErrors.branchId ? (
                <FieldError>{fieldErrors.branchId}</FieldError>
              ) : (
                <FieldDescription>
                  ต้องระบุ Branch ID ก่อนเปิดใช้งาน SlipOk (สูงสุด 64 ตัวอักษร)
                </FieldDescription>
              )}
            </Field>
          ) : null}
        </FieldGroup>

        {formError ? (
          <Alert variant="destructive">
            <AlertTitle>ดำเนินการไม่สำเร็จ</AlertTitle>
            <AlertDescription>{formError}</AlertDescription>
          </Alert>
        ) : null}

        <div aria-live="polite" className="flex flex-col gap-3">
          {testResult ? (
            <Alert variant={testResult.ok ? "default" : "destructive"}>
              {testResult.ok ? (
                <CircleCheck aria-hidden />
              ) : (
                <CircleX aria-hidden />
              )}
              <AlertTitle>
                {testResult.ok
                  ? "ทดสอบการเชื่อมต่อสำเร็จ"
                  : "ทดสอบการเชื่อมต่อไม่สำเร็จ"}
              </AlertTitle>
              <AlertDescription>
                <p>{testResult.message}</p>
                {testResult.detail ? (
                  <ul className="mt-2 flex list-disc flex-col gap-1 pl-4">
                    {Object.entries(testResult.detail)
                      .filter(([, value]) => value !== null && value !== undefined)
                      .map(([key, value]) => (
                        <li key={key}>
                          <span className="text-muted-foreground">
                            {DETAIL_LABELS[key] ?? key}:
                          </span>{" "}
                          {formatDetailValue(key, value as string | number)}
                        </li>
                      ))}
                  </ul>
                ) : null}
                <p className="mt-2 text-xs">
                  ทดสอบเมื่อ {formatAdminDateTime(testResult.testedAt)}
                </p>
              </AlertDescription>
            </Alert>
          ) : null}
        </div>

        <div className="flex flex-wrap items-center gap-2 border-t pt-4">
          <Button
            type="button"
            disabled={busy || !canSave}
            onClick={() => void handleSave()}
          >
            {busyAction === "save" ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <Save data-icon="inline-start" />
            )}
            {busyAction === "save" ? "กำลังบันทึก…" : "บันทึกคีย์"}
          </Button>
          <Button
            type="button"
            variant="outline"
            disabled={busy || !status.configured}
            onClick={() => void handleTest()}
            title={
              status.configured
                ? undefined
                : "ต้องตั้งค่า API Key ของผู้ให้บริการก่อนทดสอบ"
            }
          >
            {busyAction === "test" ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <RefreshCw data-icon="inline-start" />
            )}
            {busyAction === "test" ? "กำลังทดสอบ…" : "ทดสอบการเชื่อมต่อ"}
          </Button>
          <Button
            type="button"
            variant="destructive"
            disabled={busy || !status.configured}
            onClick={() => setClearOpen(true)}
          >
            {busyAction === "clear" ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <Trash2 data-icon="inline-start" />
            )}
            {busyAction === "clear" ? "กำลังลบ…" : "ลบ API Key"}
          </Button>
          <span role="status" aria-live="polite" className="text-sm text-muted-foreground">
            {savedMessage}
          </span>
        </div>
      </CardContent>

      <AlertDialog open={clearOpen} onOpenChange={setClearOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              ลบ API Key ของ {PROVIDER_LABELS[provider]}?
            </AlertDialogTitle>
            <AlertDialogDescription>
              ระบบจะหยุดตรวจสลิปผ่าน {PROVIDER_LABELS[provider]}
              จนกว่าจะตั้งค่าคีย์ใหม่ และอาจปิดช่องทางที่ต้องตรวจสลิปโดยอัตโนมัติ
              การลบไม่มีผลกับประวัติรายการที่ตรวจแล้ว
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={busy}>ยกเลิก</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              disabled={busy}
              onClick={(event) => {
                event.preventDefault();
                void handleClear();
              }}
            >
              ลบ API Key
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </AdminCard>
  );
}
