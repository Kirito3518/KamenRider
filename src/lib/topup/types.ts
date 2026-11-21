/** Shared top-up response contracts. Safe to import from client components. */

import type {
  BankRail,
  PromptpayType,
  SlipProvider,
  TopupChannel,
  TopupSort,
  TopupStatus,
  VerificationStatus,
} from "@/lib/topup/constants";

export type TopupPagination = {
  page: number;
  pageSize: number;
  total: number;
  pageCount: number;
};

/** "all" is used by UI/admin filters only, never stored in the database. */
export type TopupChannelFilter = TopupChannel | "all";
export type TopupStatusFilter = TopupStatus | "all";
export type TopupPeriod = "today" | "7d" | "30d" | "90d" | "custom" | "all";

export type TopupFilters = {
  q: string;
  channel: TopupChannelFilter;
  status: TopupStatusFilter;
  period: TopupPeriod;
  from: string;
  to: string;
  sort: TopupSort;
};

export type TopupSummary = {
  total: number;
  completed: number;
  pending: number;
  failed: number;
  completedSatang: number;
};

export type TopupEntry = {
  id: string;
  reference: string;
  channel: TopupChannel;
  status: TopupStatus;
  requestedSatang: number | null;
  creditedSatang: number;
  createdAt: number;
  completedAt: number | null;
  expiresAt: number | null;
  verificationStatus: VerificationStatus;
  failureCode: string | null;
  failureMessage: string | null;
  bankName: string | null;
  bankCode: string | null;
  receiverAccountName: string | null;
};

export type TopupDetail = TopupEntry & {
  updatedAt: number;
  qrImageDataUrl: string | null;
  qrPayload: string | null;
  qrExpiresAt: number | null;
  receiverAccountNumber: string | null;
  promptpayType: PromptpayType | null;
  promptpayNumberMasked: string | null;
  truemoneyPhoneMasked: string | null;
  slipProvider: SlipProvider | null;
  slipTransRef: string | null;
  slipAmountSatang: number | null;
  slipSenderName: string | null;
  slipReceiverName: string | null;
  slipReceiverAccountMasked: string | null;
  slipReceiverProxyMasked: string | null;
  slipBankCode: string | null;
  slipImageUrl: string | null;
  verifiedAt: number | null;
  verificationCode: string | null;
  verificationMessage: string | null;
  attemptsCount: number;
  canResume: boolean;
  canRetry: boolean;
  events: TopupEventView[];
};

export type TopupEventView = {
  id: string;
  fromStatus: TopupStatus | null;
  toStatus: TopupStatus;
  reason: string | null;
  actor: "system" | "user" | "admin";
  createdAt: number;
};

export type TopupHistoryResponse = {
  balanceSatang: number;
  summary: TopupSummary;
  entries: TopupEntry[];
  pagination: TopupPagination;
  filters: TopupFilters;
  rangeError: string;
};

export type TopupCreateOutcome = "pending" | "completed" | "processing" | "failed";

export type TopupCreateResponse = {
  topup: TopupDetail;
  balanceSatang: number;
  repeated: boolean;
  outcome: TopupCreateOutcome;
  message?: string;
};

export type TopupSlipOutcome = "completed" | "processing" | "failed";

export type TopupSlipResponse = {
  topup: TopupDetail;
  balanceSatang: number;
  outcome: TopupSlipOutcome;
  message: string;
};

/** Public config — never contains API keys, receiver phone, or raw setting rows. */
export type TopupBankAccountPublic = {
  id: string;
  bankCode: string;
  bankName: string;
  accountNumber: string;
  accountName: string;
  logoKey: string | null;
  logoDataUrl: string | null;
};

export type TopupPublicConfig = {
  minSatang: number;
  maxSatang: number;
  qrExpirySeconds: number;
  bankExpirySeconds: number;
  slipVerificationAvailable: boolean;
  bankRail: BankRail | null;
  truemoney: { enabled: boolean };
  promptpay: {
    enabled: boolean;
    type: PromptpayType | null;
    numberMasked: string | null;
    accountName: string | null;
  };
  bankTransfer: {
    enabled: boolean;
    accounts: TopupBankAccountPublic[];
  };
  coupon: { enabled: boolean; message: string };
};

// ---------------------------------------------------------------------------
// Admin settings
// ---------------------------------------------------------------------------

export type TopupSettingsAdmin = {
  truemoney: { enabled: boolean; phone: string | null };
  promptpay: {
    enabled: boolean;
    type: PromptpayType;
    number: string | null;
    accountName: string | null;
    /** Bank of the PromptPay account, used to verify the slip's receiving bank. */
    bankCode: string | null;
    /** Bank account number of the PromptPay account, used to verify the slip. */
    accountNumber: string | null;
  };
  bankTransfer: { enabled: boolean };
  coupon: { enabled: boolean; message: string };
  slipProvider: SlipProvider;
  updatedAt: number;
  updatedBy: string | null;
  updatedByName: string | null;
};

export type ProviderAdminStatus = {
  provider: SlipProvider;
  configured: boolean;
  maskedKey: string;
  branchId: string | null;
  lastTestAt: number | null;
  lastTestOk: boolean | null;
  lastTestMessage: string | null;
  updatedAt: number;
};

export type BankAccountAdmin = TopupBankAccountPublic & {
  isActive: boolean;
  archivedAt: number | null;
  createdAt: number;
  updatedAt: number;
};

export type TopupSettingsAuditEntry = {
  id: string;
  actorName: string;
  action: string;
  targetType: string;
  targetId: string | null;
  changes: string;
  createdAt: number;
};

export type AdminTopupSettingsResponse = {
  settings: TopupSettingsAdmin;
  providers: { easyslip: ProviderAdminStatus; slipok: ProviderAdminStatus };
  bankAccounts: BankAccountAdmin[];
  audit: TopupSettingsAuditEntry[];
  pendingVerifications: number;
};

export type ProviderTestResult = {
  provider: SlipProvider;
  ok: boolean;
  message: string;
  detail?: Record<string, string | number | null>;
  testedAt: number;
};

// ---------------------------------------------------------------------------
// Admin dashboard
// ---------------------------------------------------------------------------

export type AdminTopupEntry = TopupEntry & {
  userId: string;
  userName: string;
  userEmail: string;
  slipProvider: SlipProvider | null;
  slipTransRef: string | null;
  slipAmountSatang: number | null;
  /** True when the row is waiting for an Admin approve/reject decision. */
  canReview: boolean;
};

export type AdminTopupReview = {
  outcome: "match" | "mismatch" | "insufficient";
  message: string;
};

export type AdminTopupStats = {
  requestedSatang: number;
  total: number;
  completed: number;
  pending: number;
  failed: number;
  creditedSatang: number;
};

export type TopupChartPoint = {
  key: string;
  label: string;
  truemoneySatang: number;
  promptpaySatang: number;
  bankTransferSatang: number;
  couponSatang: number;
  truemoneyCount: number;
  promptpayCount: number;
  bankTransferCount: number;
  couponCount: number;
};

export type AdminTopupDashboardResponse = {
  stats: AdminTopupStats;
  previous: AdminTopupStats;
  chart: TopupChartPoint[];
  chartPrevious: { requestedSatang: number; creditedSatang: number; total: number };
  entries: AdminTopupEntry[];
  pagination: TopupPagination;
  filters: TopupFilters;
  rangeError: string;
};

export type TopupAttemptView = {
  id: string;
  attemptNo: number;
  provider: SlipProvider | "truemoney";
  outcome: string;
  httpStatus: number | null;
  errorCode: string | null;
  errorMessage: string | null;
  transRef: string | null;
  amountSatang: number | null;
  durationMs: number | null;
  retryAfterSeconds: number | null;
  requestSummary: string | null;
  responseExcerpt: string | null;
  createdAt: number;
};

export type AdminTopupDetail = TopupDetail & {
  userId: string;
  userName: string;
  userEmail: string;
  providerRequestId: string | null;
  attempts: TopupAttemptView[];
  canReview: boolean;
  review: AdminTopupReview | null;
};

export type TopupBankAccountCreateInput = {
  bankCode: string;
  bankName: string;
  accountNumber: string;
  accountName: string;
  logoKey?: string | null;
  logoDataUrl?: string | null;
  isActive?: boolean;
};

export type TopupBankAccountUpdateInput = TopupBankAccountCreateInput & {
  expectedUpdatedAt: number;
};
