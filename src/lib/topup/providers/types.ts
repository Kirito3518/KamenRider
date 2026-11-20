import type { PromptpayType, SlipProvider } from "@/lib/topup/constants";

export type ProviderOk<T> = {
  kind: "ok";
  data: T;
  httpStatus: number;
  durationMs: number;
  requestId: string | null;
  raw: unknown;
};

export type ProviderFailureKind =
  | "provider-error"
  | "timeout"
  | "network"
  | "invalid-response";

export type ProviderFailure = {
  kind: ProviderFailureKind;
  code: string;
  message: string;
  httpStatus: number | null;
  durationMs: number;
  requestId: string | null;
  retryAfterSeconds: number | null;
  retryable: boolean;
  raw: unknown;
};

export type ProviderResult<T> = ProviderOk<T> | ProviderFailure;

export type EasySlipQrData = {
  imageDataUrl: string;
  payload: string;
  mime: string;
};

export type NormalizedSlip = {
  provider: SlipProvider;
  transRef: string | null;
  amountSatang: number | null;
  dateIso: string | null;
  senderName: string | null;
  receiverName: string | null;
  receiverBankCode: string | null;
  receiverBankName: string | null;
  receiverAccountNumber: string | null;
  receiverProxyType: string | null;
  receiverProxyValue: string | null;
};

export type ProviderTestData = {
  application?: string | null;
  remainingQuota?: number | null;
  usedQuota?: number | null;
  maxQuota?: number | null;
  expiredAt?: string | null;
  quota?: number | null;
  overQuota?: number | null;
};

export type TrueMoneyClaimData = {
  voucherId: string;
  amountSatang: number;
  displayName: string | null;
  requestId: string | null;
};

export type EasySlipQrInput = {
  apiKey: string;
  promptpayType: PromptpayType;
  promptpayNumber: string;
  amountSatang: number;
};

export type VerifySlipInput = {
  apiKey: string;
  file: {
    bytes: Uint8Array;
    mime: string;
    fileName: string;
  };
  expectedAmountSatang: number;
  checkDuplicate: boolean;
};

export type SlipOkVerifyInput = VerifySlipInput & {
  branchId: string;
};
