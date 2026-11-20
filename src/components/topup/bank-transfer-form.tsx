"use client";

import { useState, type FormEvent } from "react";
import { Banknote, CircleAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
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
import { parseBahtToSatang, formatBahtNumber } from "@/lib/topup/amount";
import type {
  TopupCreateResponse,
  TopupDetail,
  TopupPublicConfig,
  TopupSlipResponse,
} from "@/lib/topup/types";
import { BankLogo } from "@/components/topup/topup-display";
import { BankTransferPanel } from "@/components/topup/bank-transfer-panel";
import {
  clientErrorMessage,
  newRequestId,
  topupFetch,
} from "@/components/topup/client";

const PRESETS = [100, 300, 500, 1_000];

export function BankTransferForm({
  config,
  onBalance,
  embedded = false,
}: {
  config: TopupPublicConfig;
  onBalance: (balanceSatang: number) => void;
  embedded?: boolean;
}) {
  const accounts = config.bankTransfer.accounts;
  const [amount, setAmount] = useState("100");
  const [accountId, setAccountId] = useState(accounts[0]?.id ?? "");
  const [active, setActive] = useState<TopupDetail | null>(null);
  const [error, setError] = useState("");
  const [fieldError, setFieldError] = useState("");
  const [pending, setPending] = useState(false);

  const unavailable = !config.bankTransfer.enabled;
  const verificationUnavailable = !config.slipVerificationAvailable;
  const selectedAccount = accounts.find((account) => account.id === accountId);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending) return;
    const satang = parseBahtToSatang(amount);
    if (satang === null) {
      setFieldError("กรอกจำนวนเงินให้ถูกต้อง เช่น 100 หรือ 100.50");
      return;
    }
    if (satang < config.minSatang || satang > config.maxSatang) {
      setFieldError(
        `จำนวนเงินต้องอยู่ระหว่าง ${formatBahtNumber(config.minSatang)} – ${formatBahtNumber(config.maxSatang)} บาท`,
      );
      return;
    }
    if (!accountId) {
      setFieldError("กรุณาเลือกบัญชีธนาคารที่ต้องการโอน");
      return;
    }
    setPending(true);
    setError("");
    setFieldError("");
    try {
      const result = await topupFetch<TopupCreateResponse>("/api/topups/bank", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          requestId: newRequestId(),
          amountSatang: satang,
          bankAccountId: accountId,
        }),
      });
      setActive(result.topup);
      onBalance(result.balanceSatang);
      if (result.outcome === "failed") {
        setError(result.message || "สร้างรายการโอนไม่สำเร็จ");
      }
    } catch (cause) {
      setError(clientErrorMessage(cause));
    } finally {
      setPending(false);
    }
  }

  function updated(result: TopupSlipResponse) {
    setActive(result.topup);
    onBalance(result.balanceSatang);
    setError(result.outcome === "failed" ? result.message : "");
  }

  if (active) {
    return (
      <BankTransferPanel
        topup={active}
        onUpdated={updated}
        onReset={() => {
          setActive(null);
          setError("");
        }}
      />
    );
  }

  return (
    <div className="flex flex-col gap-4">
      {!embedded ? (
        <div className="flex items-start gap-3 rounded-2xl border bg-card p-4">
          <Banknote aria-hidden className="mt-0.5 size-5 text-primary" />
          <div>
            <p className="font-medium">โอนเงินผ่านบัญชีธนาคาร</p>
            <p className="text-sm text-muted-foreground">
              เลือกบัญชีที่ต้องการโอน กรอกจำนวนเงิน แล้วอัปโหลดสลิปหลังโอนเพื่อให้ระบบตรวจสอบ
            </p>
          </div>
        </div>
      ) : null}

      {unavailable ? (
        <Alert>
          <CircleAlert aria-hidden />
          <AlertTitle>ยังไม่เปิดให้บริการ</AlertTitle>
          <AlertDescription>
            ช่องทางโอนผ่านบัญชีธนาคารยังไม่เปิดใช้งาน กรุณาติดต่อผู้ดูแลระบบ
          </AlertDescription>
        </Alert>
      ) : null}
      {verificationUnavailable ? (
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>ระบบตรวจสลิปยังไม่พร้อม</AlertTitle>
          <AlertDescription>
            ผู้ดูแลระบบยังไม่ได้ตั้งค่าผู้ให้บริการตรวจสลิป กรุณาติดต่อผู้ดูแล
          </AlertDescription>
        </Alert>
      ) : null}
      {accounts.length === 0 ? (
        <Alert>
          <CircleAlert aria-hidden />
          <AlertTitle>ยังไม่มีบัญชีรับโอน</AlertTitle>
          <AlertDescription>กรุณาติดต่อผู้ดูแลระบบเพื่อเพิ่มบัญชีธนาคาร</AlertDescription>
        </Alert>
      ) : null}

      <form onSubmit={(event) => void submit(event)} noValidate>
        <FieldGroup className="max-w-xl gap-4">
          <Field data-invalid={Boolean(fieldError)}>
            <FieldLabel htmlFor="bank-amount">จำนวนเงินที่ต้องการเติม (บาท)</FieldLabel>
            <Input
              id="bank-amount"
              name="amount"
              value={amount}
              inputMode="decimal"
              autoComplete="off"
              maxLength={12}
              aria-invalid={Boolean(fieldError)}
              onChange={(event) => {
                setAmount(event.currentTarget.value);
                setFieldError("");
              }}
              disabled={pending || unavailable || verificationUnavailable}
            />
            <FieldDescription>
              ขั้นต่ำ {formatBahtNumber(config.minSatang)} บาท · สูงสุด{" "}
              {formatBahtNumber(config.maxSatang)} บาท
            </FieldDescription>
            {fieldError ? <FieldError>{fieldError}</FieldError> : null}
          </Field>
          <div className="flex flex-wrap gap-2">
            {PRESETS.map((preset) => {
              const value = `${preset}.00`;
              const selected = amount === value || amount === String(preset);
              return (
                <Button
                  key={preset}
                  type="button"
                  size="sm"
                  variant={selected ? "default" : "outline"}
                  onClick={() => setAmount(value)}
                  disabled={pending || unavailable}
                >
                  {preset.toLocaleString("th-TH")} บาท
                </Button>
              );
            })}
          </div>
          <Field>
            <FieldLabel htmlFor="bank-account">บัญชีธนาคารที่ต้องการโอน</FieldLabel>
            <Select
              value={accountId}
              onValueChange={(value) => setAccountId(value ?? "")}
              disabled={pending || unavailable || accounts.length === 0}
              items={accounts.map((account) => ({
                value: account.id,
                label: `${account.bankName} (${account.bankCode})`,
              }))}
            >
              <SelectTrigger id="bank-account" aria-label="เลือกบัญชีธนาคาร" className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectGroup>
                  {accounts.map((account) => (
                    <SelectItem key={account.id} value={account.id}>
                      <span className="flex items-center gap-2">
                        <BankLogo
                          bankCode={account.bankCode}
                          bankName={account.bankName}
                          logoKey={account.logoKey}
                          logoDataUrl={account.logoDataUrl}
                          className="size-6 rounded-lg text-[8px]"
                        />
                        <span className="min-w-0">
                          <span className="block truncate">{account.bankName}</span>
                          <span className="block text-xs text-muted-foreground">
                            {account.bankCode} · {account.accountNumber}
                          </span>
                        </span>
                      </span>
                    </SelectItem>
                  ))}
                </SelectGroup>
              </SelectContent>
            </Select>
          </Field>
          {selectedAccount ? (
            <div className="flex items-center gap-3 rounded-2xl border bg-muted/40 p-3 text-sm">
              <BankLogo
                bankCode={selectedAccount.bankCode}
                bankName={selectedAccount.bankName}
                logoKey={selectedAccount.logoKey}
                logoDataUrl={selectedAccount.logoDataUrl}
              />
              <div className="min-w-0">
                <p className="truncate font-medium">{selectedAccount.bankName}</p>
                <p className="truncate text-xs text-muted-foreground">
                  {selectedAccount.accountName} · {selectedAccount.accountNumber}
                </p>
              </div>
            </div>
          ) : null}
          <Button
            type="submit"
            disabled={pending || unavailable || verificationUnavailable || accounts.length === 0}
          >
            {pending ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <Banknote data-icon="inline-start" />
            )}
            {pending ? "กำลังสร้างรายการ…" : "ดำเนินการต่อ"}
          </Button>
        </FieldGroup>
      </form>

      {error ? (
        <Alert variant="destructive" role="alert">
          <CircleAlert aria-hidden />
          <AlertTitle>สร้างรายการไม่สำเร็จ</AlertTitle>
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}
    </div>
  );
}
