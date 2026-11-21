"use client";

import { useCallback, useEffect, useState } from "react";
import {
  AlertTriangle,
  Gift,
  Landmark,
  QrCode,
  RefreshCw,
  Settings2,
  ShieldCheck,
  TicketPercent,
} from "lucide-react";
import toast from "react-hot-toast";
import { AdminPage, AdminPageHeader } from "@/components/admin/admin-page";
import { TopupAuditCard } from "@/components/admin/settings/topup-audit-card";
import {
  BankAccountsCard,
  BankRailCard,
} from "@/components/admin/settings/topup-bank-accounts-card";
import { CouponCard, CouponPreviewCard } from "@/components/admin/settings/topup-coupon-card";
import { PromptPayCard, PromptPayReceiverCard } from "@/components/admin/settings/topup-promptpay-card";
import {
  SlipProviderCredentialCard,
  SlipProviderSelectionCard,
} from "@/components/admin/settings/topup-slip-provider-card";
import { TrueMoneyCard, TrueMoneyInfoCard } from "@/components/admin/settings/topup-truemoney-card";
import {
  type BankAccountFormValues,
  type BankAccountPayload,
  type ProviderUpdatePayload,
  type SettingsDraft,
  type SettingsUpdatePayload,
  type TopupMutationResult,
  activeBankAccounts,
  archiveAdminBankAccount,
  asTopupApiError,
  buildTopupWarnings,
  createAdminBankAccount,
  loadAdminTopupSettings,
  mergeBankAccount,
  normalizeDigits,
  providerLabel,
  settingsDraftFromAdmin,
  testAdminTopupProvider,
  topupErrorMessage,
  updateAdminTopupSettings,
  updateAdminTopupProvider,
  updateAdminBankAccount,
} from "@/components/admin/settings/topup-settings-api";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { PageSkeleton, SkeletonBlock } from "@/components/loading";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import type { SlipProvider } from "@/lib/topup/constants";
import type {
  AdminTopupSettingsResponse,
  BankAccountAdmin,
  ProviderTestResult,
} from "@/lib/topup/types";

type TabValue = "truemoney" | "promptpay" | "bank" | "slip" | "coupon";

const TABS: { value: TabValue; label: string; icon: typeof Gift }[] = [
  { value: "truemoney", label: "TrueMoney", icon: Gift },
  { value: "promptpay", label: "PromptPay QR", icon: QrCode },
  { value: "bank", label: "บัญชีธนาคาร", icon: Landmark },
  { value: "slip", label: "ตรวจสอบสลิป", icon: ShieldCheck },
  { value: "coupon", label: "คูปอง", icon: TicketPercent },
];

function enabledChannelCount(data: AdminTopupSettingsResponse): number {
  const settings = data.settings;
  return [
    settings.truemoney.enabled,
    settings.promptpay.enabled,
    settings.bankTransfer.enabled,
    settings.coupon.enabled,
  ].filter(Boolean).length;
}

function LoadingSettings() {
  return (
    <PageSkeleton label="กำลังโหลดการตั้งค่าเติมเงิน" className="gap-4">
      <div className="flex flex-wrap items-center gap-2">
        {Array.from({ length: 5 }).map((_, index) => (
          <SkeletonBlock
            key={index}
            className="h-8 w-24"
            radius="rounded-xl"
          />
        ))}
      </div>
      <div className="grid items-start gap-4 md:grid-cols-2 xl:grid-cols-12">
        <SkeletonBlock className="h-36 md:col-span-2 xl:col-span-7" />
        <SkeletonBlock className="h-36 md:col-span-2 xl:col-span-5" />
        <SkeletonBlock className="h-36 md:col-span-2 xl:col-span-4" />
        <SkeletonBlock className="h-48 md:col-span-2 xl:col-span-8" />
      </div>
    </PageSkeleton>
  );
}

/**
 * Client manager for /admin/settings/topup.
 *
 * The aggregate GET never contains raw provider keys, and every save replaces
 * local state with the server response so `expectedUpdatedAt` stays correct.
 */
export function TopupSettingsManager() {
  const [data, setData] = useState<AdminTopupSettingsResponse | null>(null);
  const [draft, setDraft] = useState<SettingsDraft | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [conflict, setConflict] = useState(false);
  const [savingSection, setSavingSection] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState<TabValue>("truemoney");

  const load = useCallback(async (options?: { background?: boolean }) => {
    if (options?.background) setRefreshing(true);
    else setLoading(true);
    try {
      const response = await loadAdminTopupSettings();
      setData(response);
      setDraft(settingsDraftFromAdmin(response.settings));
      setLoadError("");
      setConflict(false);
    } catch (error) {
      const message = topupErrorMessage(error);
      if (options?.background) toast.error(message);
      else setLoadError(message);
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void load(), 0);
    return () => window.clearTimeout(initial);
  }, [load]);

  async function perform<T>(
    key: string,
    run: () => Promise<T>,
  ): Promise<TopupMutationResult<T>> {
    setSavingSection(key);
    try {
      const value = await run();
      setConflict(false);
      return { ok: true, data: value };
    } catch (error) {
      const apiError = asTopupApiError(error);
      if (apiError.status === 409) setConflict(true);
      toast.error(topupErrorMessage(apiError));
      return { ok: false, error: apiError };
    } finally {
      setSavingSection(null);
    }
  }

  async function saveSettingsSection(
    key: string,
    payload: Omit<SettingsUpdatePayload, "expectedUpdatedAt">,
    successMessage: string,
  ): Promise<TopupMutationResult<AdminTopupSettingsResponse>> {
    if (!data) {
      return {
        ok: false,
        error: asTopupApiError(new Error("ยังโหลดการตั้งค่าไม่เสร็จ")),
      };
    }
    const expectedUpdatedAt = data.settings.updatedAt;
    const result = await perform(key, () =>
      updateAdminTopupSettings({ expectedUpdatedAt, ...payload }),
    );
    if (result.ok) {
      setData(result.data);
      setDraft(settingsDraftFromAdmin(result.data.settings));
      toast.success(successMessage);
    }
    return result;
  }

  async function saveProvider(
    provider: SlipProvider,
    payload: Omit<ProviderUpdatePayload, "expectedUpdatedAt">,
  ): Promise<TopupMutationResult<AdminTopupSettingsResponse>> {
    if (!data) {
      return {
        ok: false,
        error: asTopupApiError(new Error("ยังโหลดการตั้งค่าไม่เสร็จ")),
      };
    }
    const expectedUpdatedAt = data.providers[provider].updatedAt;
    const result = await perform(`provider:${provider}`, () =>
      updateAdminTopupProvider(provider, { expectedUpdatedAt, ...payload }),
    );
    if (result.ok) {
      setData(result.data);
      toast.success(`บันทึกการตั้งค่า ${providerLabel(provider)} แล้ว`);
    }
    return result;
  }

  async function clearProvider(
    provider: SlipProvider,
  ): Promise<TopupMutationResult<AdminTopupSettingsResponse>> {
    if (!data) {
      return {
        ok: false,
        error: asTopupApiError(new Error("ยังโหลดการตั้งค่าไม่เสร็จ")),
      };
    }
    const expectedUpdatedAt = data.providers[provider].updatedAt;
    const result = await perform(`clear:${provider}`, () =>
      updateAdminTopupProvider(provider, {
        expectedUpdatedAt,
        clearApiKey: true,
      }),
    );
    if (result.ok) {
      setData(result.data);
      toast.success(`ลบ API Key ของ ${providerLabel(provider)} แล้ว`);
    }
    return result;
  }

  async function testProvider(
    provider: SlipProvider,
  ): Promise<TopupMutationResult<ProviderTestResult>> {
    const result = await perform(`test:${provider}`, () =>
      testAdminTopupProvider(provider),
    );
    if (result.ok) {
      const tested = result.data;
      setData((current) =>
        current
          ? {
              ...current,
              providers: {
                ...current.providers,
                [provider]: {
                  ...current.providers[provider],
                  lastTestAt: tested.testedAt,
                  lastTestOk: tested.ok,
                  lastTestMessage: tested.message,
                },
              },
            }
          : current,
      );
      if (tested.ok) toast.success("ทดสอบการเชื่อมต่อสำเร็จ");
      else toast.error(tested.message);
    }
    return result;
  }

  async function createBankAccount(
    values: BankAccountFormValues,
  ): Promise<TopupMutationResult<BankAccountAdmin>> {
    const result = await perform("bank:create", () =>
      createAdminBankAccount(values),
    );
    if (result.ok) {
      setData((current) =>
        current
          ? {
              ...current,
              bankAccounts: mergeBankAccount(current.bankAccounts, result.data),
            }
          : current,
      );
      toast.success("เพิ่มบัญชีธนาคารแล้ว");
    }
    return result;
  }

  async function updateBankAccount(
    account: BankAccountAdmin,
    values: BankAccountFormValues,
  ): Promise<TopupMutationResult<BankAccountAdmin>> {
    const payload: BankAccountPayload = {
      bankCode: values.bankCode,
      bankName: values.bankName,
      accountNumber: values.accountNumber,
      accountName: values.accountName,
      logoKey: values.logoKey,
      logoDataUrl: values.logoDataUrl,
      isActive: values.isActive,
    };
    const result = await perform(`bank:${account.id}`, () =>
      updateAdminBankAccount(account.id, {
        ...payload,
        expectedUpdatedAt: account.updatedAt,
      }),
    );
    if (result.ok) {
      setData((current) =>
        current
          ? {
              ...current,
              bankAccounts: mergeBankAccount(current.bankAccounts, result.data),
            }
          : current,
      );
      toast.success("บันทึกบัญชีธนาคารแล้ว");
    }
    return result;
  }

  async function toggleBankAccount(
    account: BankAccountAdmin,
    isActive: boolean,
  ): Promise<TopupMutationResult<BankAccountAdmin>> {
    return updateBankAccount(account, {
      bankCode: account.bankCode,
      bankName: account.bankName,
      accountNumber: account.accountNumber,
      accountName: account.accountName,
      logoKey: account.logoKey,
      logoDataUrl: account.logoDataUrl,
      isActive,
    });
  }

  async function archiveBankAccount(
    account: BankAccountAdmin,
  ): Promise<TopupMutationResult<BankAccountAdmin>> {
    const result = await perform(`bank:archive:${account.id}`, () =>
      archiveAdminBankAccount(account.id),
    );
    if (result.ok) {
      setData((current) =>
        current
          ? {
              ...current,
              bankAccounts: mergeBankAccount(current.bankAccounts, result.data),
            }
          : current,
      );
      toast.success("เก็บถาวรบัญชีธนาคารแล้ว");
    }
    return result;
  }

  const settings = data?.settings;
  const warnings = data ? buildTopupWarnings(data) : [];
  const bankBusy =
    savingSection === "bankTransfer" ||
    Boolean(savingSection?.startsWith("bank:"));

  return (
    <AdminPage>
      <AdminPageHeader
        icon={Settings2}
        title="ตั้งค่าเติมเงิน"
        titleSuffix={
          data ? (
            <Badge variant={enabledChannelCount(data) > 0 ? "available" : "outline"}>
              {enabledChannelCount(data)} ช่องทางเปิดใช้งาน
            </Badge>
          ) : null
        }
        description="เปิด/ปิดช่องทางเติมเงิน ตั้งค่าผู้ให้บริการตรวจสลิป และจัดการบัญชีธนาคารสำหรับรับโอน"
        actions={
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={loading || refreshing}
            onClick={() => void load({ background: true })}
          >
            {refreshing ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <RefreshCw data-icon="inline-start" />
            )}
            รีเฟรช
          </Button>
        }
      />

      {loading && !data ? <LoadingSettings /> : null}

      {loadError && !data ? (
        <Alert variant="destructive">
          <AlertTitle>โหลดการตั้งค่าไม่สำเร็จ</AlertTitle>
          <AlertDescription>
            <p>{loadError}</p>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="mt-3"
              onClick={() => void load()}
            >
              <RefreshCw data-icon="inline-start" />
              ลองใหม่
            </Button>
          </AlertDescription>
        </Alert>
      ) : null}

      {conflict ? (
        <Alert variant="destructive">
          <AlertTriangle aria-hidden />
          <AlertTitle>ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรช</AlertTitle>
          <AlertDescription>
            <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
              <span>
                มีผู้ดูแลท่านอื่นบันทึกการตั้งค่าใหม่ ข้อมูลบนหน้านี้อาจไม่ใช่ค่าล่าสุด
              </span>
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={refreshing}
                onClick={() => void load({ background: true })}
              >
                {refreshing ? (
                  <Spinner data-icon="inline-start" />
                ) : (
                  <RefreshCw data-icon="inline-start" />
                )}
                รีเฟรชข้อมูล
              </Button>
            </div>
          </AlertDescription>
        </Alert>
      ) : null}

      {warnings.length > 0 ? (
        <Alert>
          <AlertTriangle aria-hidden />
          <AlertTitle>ข้อควรตรวจสอบก่อนใช้งาน</AlertTitle>
          <AlertDescription>
            <ul className="flex list-disc flex-col gap-1 pl-4">
              {warnings.map((warning) => (
                <li key={warning}>{warning}</li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      ) : null}

      {data && draft && settings ? (
        <>
          <Tabs
            value={activeTab}
            onValueChange={(value) => {
              if (typeof value === "string") setActiveTab(value as TabValue);
            }}
          >
            <TabsList
              className="w-full justify-start overflow-x-auto sm:w-fit"
              aria-label="หมวดการตั้งค่าเติมเงิน"
            >
              {TABS.map((tab) => {
                const Icon = tab.icon;
                return (
                  <TabsTrigger key={tab.value} value={tab.value}>
                    <Icon aria-hidden />
                    {tab.label}
                  </TabsTrigger>
                );
              })}
            </TabsList>

            <TabsContent
              value="truemoney"
              keepMounted
              className="grid items-start gap-4 md:grid-cols-2 xl:grid-cols-12"
            >
              <div className="min-w-0 xl:col-span-7">
                <TrueMoneyCard
                  draft={draft.truemoney}
                  savedPhone={settings.truemoney.phone}
                  saving={savingSection === "truemoney"}
                  onChange={(patch) =>
                    setDraft((current) =>
                      current
                        ? {
                            ...current,
                            truemoney: { ...current.truemoney, ...patch },
                          }
                        : current,
                    )
                  }
                  onSave={() =>
                    saveSettingsSection(
                      "truemoney",
                      {
                        truemoney: {
                          enabled: draft.truemoney.enabled,
                          phone: draft.truemoney.phone
                            ? normalizeDigits(draft.truemoney.phone)
                            : null,
                        },
                      },
                      "บันทึกการตั้งค่า TrueMoney แล้ว",
                    )
                  }
                />
              </div>
              <div className="min-w-0 xl:col-span-5">
                <TrueMoneyInfoCard savedPhone={settings.truemoney.phone} />
              </div>
            </TabsContent>

            <TabsContent
              value="promptpay"
              keepMounted
              className="grid items-start gap-4 md:grid-cols-2 xl:grid-cols-12"
            >
              <div className="min-w-0 xl:col-span-7">
                <PromptPayCard
                  draft={draft.promptpay}
                  saved={settings.promptpay}
                  bankTransferEnabled={settings.bankTransfer.enabled}
                  easySlipConfigured={data.providers.easyslip.configured}
                  saving={savingSection === "promptpay"}
                  onChange={(patch) =>
                    setDraft((current) =>
                      current
                        ? {
                            ...current,
                            promptpay: { ...current.promptpay, ...patch },
                          }
                        : current,
                    )
                  }
                  onSave={() =>
                    saveSettingsSection(
                      "promptpay",
                      {
                        promptpay: {
                          enabled: draft.promptpay.enabled,
                          type: draft.promptpay.type,
                          number: draft.promptpay.number
                            ? normalizeDigits(draft.promptpay.number)
                            : null,
                          accountName: draft.promptpay.accountName.trim() || null,
                          bankCode: draft.promptpay.bankCode || null,
                          accountNumber: draft.promptpay.accountNumber
                            ? normalizeDigits(draft.promptpay.accountNumber)
                            : null,
                        },
                      },
                      "บันทึกการตั้งค่า PromptPay QR แล้ว",
                    )
                  }
                />
              </div>
              <div className="min-w-0 xl:col-span-5">
                <PromptPayReceiverCard
                  draft={draft.promptpay}
                  saved={settings.promptpay}
                  easySlipConfigured={data.providers.easyslip.configured}
                />
              </div>
            </TabsContent>

            <TabsContent
              value="bank"
              keepMounted
              className="grid items-start gap-4 md:grid-cols-2 xl:grid-cols-12"
            >
              <div className="min-w-0 xl:col-span-4">
                <BankRailCard
                  settings={settings}
                  draftEnabled={draft.bankTransfer.enabled}
                  activeAccounts={activeBankAccounts(data.bankAccounts).length}
                  saving={savingSection === "bankTransfer"}
                  onChange={(enabled) =>
                    setDraft((current) =>
                      current
                        ? {
                            ...current,
                            bankTransfer: { ...current.bankTransfer, enabled },
                          }
                        : current,
                    )
                  }
                  onSave={() =>
                    saveSettingsSection(
                      "bankTransfer",
                      { bankTransfer: { enabled: draft.bankTransfer.enabled } },
                      "บันทึกช่องทางโอนผ่านบัญชีแล้ว",
                    )
                  }
                />
              </div>
              <div className="min-w-0 xl:col-span-8">
                <BankAccountsCard
                  accounts={data.bankAccounts}
                  busy={bankBusy}
                  onCreate={createBankAccount}
                  onUpdate={updateBankAccount}
                  onToggleActive={toggleBankAccount}
                  onArchive={archiveBankAccount}
                />
              </div>
            </TabsContent>

            <TabsContent
              value="slip"
              keepMounted
              className="grid items-start gap-4 md:grid-cols-2 xl:grid-cols-12"
            >
              <div className="min-w-0 md:col-span-2 xl:col-span-12">
                <SlipProviderSelectionCard
                  draftProvider={draft.slipProvider}
                  savedProvider={settings.slipProvider}
                  pendingVerifications={data.pendingVerifications}
                  saving={savingSection === "slipProvider"}
                  onChange={(provider) =>
                    setDraft((current) =>
                      current ? { ...current, slipProvider: provider } : current,
                    )
                  }
                  onSave={() =>
                    saveSettingsSection(
                      "slipProvider",
                      { slipProvider: draft.slipProvider },
                      "บันทึกผู้ให้บริการตรวจสลิปแล้ว",
                    )
                  }
                />
              </div>
              <div className="min-w-0 xl:col-span-6">
                <SlipProviderCredentialCard
                  provider="easyslip"
                  status={data.providers.easyslip}
                  selected={settings.slipProvider === "easyslip"}
                  busyAction={
                    savingSection === "provider:easyslip"
                      ? "save"
                      : savingSection === "clear:easyslip"
                        ? "clear"
                        : savingSection === "test:easyslip"
                          ? "test"
                          : null
                  }
                  onSave={(payload) => saveProvider("easyslip", payload)}
                  onClear={() => clearProvider("easyslip")}
                  onTest={() => testProvider("easyslip")}
                />
              </div>
              <div className="min-w-0 xl:col-span-6">
                <SlipProviderCredentialCard
                  provider="slipok"
                  status={data.providers.slipok}
                  selected={settings.slipProvider === "slipok"}
                  busyAction={
                    savingSection === "provider:slipok"
                      ? "save"
                      : savingSection === "clear:slipok"
                        ? "clear"
                        : savingSection === "test:slipok"
                          ? "test"
                          : null
                  }
                  onSave={(payload) => saveProvider("slipok", payload)}
                  onClear={() => clearProvider("slipok")}
                  onTest={() => testProvider("slipok")}
                />
              </div>
            </TabsContent>

            <TabsContent
              value="coupon"
              keepMounted
              className="grid items-start gap-4 md:grid-cols-2 xl:grid-cols-12"
            >
              <div className="min-w-0 xl:col-span-7">
                <CouponCard
                  draft={draft.coupon}
                  saving={savingSection === "coupon"}
                  onChange={(patch) =>
                    setDraft((current) =>
                      current
                        ? { ...current, coupon: { ...current.coupon, ...patch } }
                        : current,
                    )
                  }
                  onSave={() =>
                    saveSettingsSection(
                      "coupon",
                      {
                        coupon: {
                          enabled: draft.coupon.enabled,
                          message: draft.coupon.message,
                        },
                      },
                      "บันทึกการตั้งค่าคูปองแล้ว",
                    )
                  }
                />
              </div>
              <div className="min-w-0 xl:col-span-5">
                <CouponPreviewCard draft={draft.coupon} />
              </div>
            </TabsContent>
          </Tabs>

          <TopupAuditCard entries={data.audit} />
        </>
      ) : null}
    </AdminPage>
  );
}
