"use client";

import { useState, type ReactNode } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import toast from "react-hot-toast";
import { KeyRound, Link2, Link2Off, LogOut, Info, Wallet } from "lucide-react";
import { formatBalance } from "@/lib/money";
import { authClient } from "@/lib/auth-client";
import { authError } from "@/lib/auth-errors";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader } from "@/components/ui/card";
import { UserAvatar } from "@/components/auth/user-avatar";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { getRoleLabel } from "@/lib/roles";

type LinkedAccount = { id: string; providerId: string };
type Provider = "google" | "discord";

const PROVIDERS: Record<Provider, { label: string; icon: ReactNode }> = {
  google: {
    label: "Google",
    icon: (
      <svg viewBox="0 0 24 24" className="size-5" aria-hidden="true">
        <path
          fill="#4285F4"
          d="M22.5 12.27c0-.79-.07-1.54-.2-2.27H12v4.3h5.9a5.05 5.05 0 0 1-2.19 3.31v2.75h3.54c2.07-1.91 3.25-4.72 3.25-8.09Z"
        />
        <path
          fill="#34A853"
          d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.54-2.75c-.98.66-2.24 1.05-3.74 1.05-2.87 0-5.3-1.94-6.17-4.55H2.17v2.84A11 11 0 0 0 12 23Z"
        />
        <path
          fill="#FBBC05"
          d="M5.83 14.09a6.6 6.6 0 0 1 0-4.18V7.07H2.17a11 11 0 0 0 0 9.86l3.66-2.84Z"
        />
        <path
          fill="#EA4335"
          d="M12 5.36c1.62 0 3.07.56 4.21 1.65l3.15-3.15C17.45 2.1 14.97 1 12 1A11 11 0 0 0 2.17 7.07l3.66 2.84C6.7 7.3 9.13 5.36 12 5.36Z"
        />
      </svg>
    ),
  },
  discord: {
    label: "Discord",
    icon: (
      <svg viewBox="0 0 24 24" className="size-5" aria-hidden="true">
        <path
          fill="#5865F2"
          d="M20.32 4.37A19.8 19.8 0 0 0 15.43 2.9a.07.07 0 0 0-.08.04c-.21.38-.44.87-.6 1.26a18.3 18.3 0 0 0-5.5 0 12.6 12.6 0 0 0-.62-1.26.08.08 0 0 0-.08-.04c-1.7.3-3.34.8-4.88 1.47a.07.07 0 0 0-.03.03C.53 9.05-.32 13.58.1 18.06a.08.08 0 0 0 .03.06 19.9 19.9 0 0 0 5.99 3.03.08.08 0 0 0 .08-.03c.46-.63.87-1.3 1.22-2a.08.08 0 0 0-.04-.1 13 13 0 0 1-1.87-.9.08.08 0 0 1 0-.13l.37-.29a.07.07 0 0 1 .08-.01c3.93 1.8 8.18 1.8 12.07 0a.07.07 0 0 1 .08.01l.37.3a.08.08 0 0 1 0 .12c-.6.35-1.22.65-1.87.9a.08.08 0 0 0-.04.1c.36.7.77 1.37 1.22 2a.08.08 0 0 0 .08.03 19.8 19.8 0 0 0 6-3.03.08.08 0 0 0 .03-.05c.5-5.18-.84-9.67-3.55-13.66a.06.06 0 0 0-.03-.03ZM8.02 15.33c-1.18 0-2.16-1.09-2.16-2.42s.96-2.42 2.16-2.42c1.21 0 2.18 1.1 2.16 2.42 0 1.33-.96 2.42-2.16 2.42Zm7.97 0c-1.18 0-2.16-1.09-2.16-2.42s.95-2.42 2.16-2.42c1.21 0 2.18 1.1 2.16 2.42 0 1.33-.95 2.42-2.16 2.42Z"
        />
      </svg>
    ),
  },
};

function MethodRow({
  icon,
  title,
  status,
  connected,
  children,
}: {
  icon: ReactNode;
  title: string;
  status: string;
  connected: boolean;
  children?: ReactNode;
}) {
  return (
    <li className="flex items-center gap-3 px-4 py-3.5 sm:gap-4 sm:px-5">
      <span className="flex size-10 shrink-0 items-center justify-center rounded-xl border bg-background shadow-xs">
        {icon}
      </span>
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm font-medium leading-tight">{title}</p>
        <p className="mt-1 flex items-center gap-1.5 text-xs text-muted-foreground">
          <span
            className={cn(
              "size-1.5 rounded-full",
              connected ? "bg-emerald-500" : "bg-muted-foreground/40",
            )}
          />
          {status}
        </p>
      </div>
      {children && (
        <div className="flex shrink-0 flex-col gap-2">{children}</div>
      )}
    </li>
  );
}

export function AccountSettings({
  user,
  initialAccounts,
  providers,
}: {
  user: { id: string; name: string; email: string; image?: string | null; balanceSatang: number; role: string };
  initialAccounts: LinkedAccount[];
  providers: { google: boolean; discord: boolean };
}) {
  const [accounts, setAccounts] = useState(initialAccounts);
  const [pending, setPending] = useState(false);
  const router = useRouter();

  async function link(provider: Provider) {
    setPending(true);
    try {
      const result = await authClient.linkSocial({
        provider,
        callbackURL: "/account?authResult=linked",
        errorCallbackURL: "/account",
      });
      if (result.error) toast.error(authError(result.error));
    } catch {
      toast.error("เชื่อมต่อไม่ได้ กรุณาลองใหม่");
    } finally {
      setPending(false);
    }
  }

  async function unlink(accountId: string) {
    setPending(true);
    try {
      const result = await authClient.unlinkAccount({ accountId });
      if (result.error) {
        toast.error(authError(result.error));
        return;
      }
      const refreshed = await authClient.listAccounts();
      if (refreshed.error || !refreshed.data) {
        toast.error("ถอดบัญชีแล้ว แต่โหลดรายการใหม่ไม่สำเร็จ กรุณารีเฟรช");
        router.refresh();
        return;
      }
      setAccounts(refreshed.data);
      toast.success("ถอดการเชื่อมต่อสำเร็จ");
      router.refresh();
    } catch {
      toast.error("ทำรายการไม่สำเร็จ กรุณาลองใหม่");
    } finally {
      setPending(false);
    }
  }

  async function logout() {
    setPending(true);
    try {
      const result = await authClient.signOut();
      if (result.error) {
        toast.error(authError(result.error));
        return;
      }
      toast.success("ออกจากระบบแล้ว");
      router.replace("/");
      router.refresh();
    } catch {
      toast.error("ออกจากระบบไม่สำเร็จ กรุณาลองใหม่");
    } finally {
      setPending(false);
    }
  }

  const hasPassword = accounts.some((a) => a.providerId === "credential");
  const onlyOne = accounts.length <= 1;

  return (
    <Card className="mx-auto w-full max-w-xl gap-0 overflow-hidden p-0">
      {/* โปรไฟล์ */}
      <CardHeader className="flex flex-row items-center gap-4 border-b bg-muted/40 p-5 sm:p-6">
        <div className="rounded-full p-1 ring-2 ring-border ring-offset-2 ring-offset-background">
          <UserAvatar user={user} size="lg" />
        </div>
        <div className="min-w-0">
          <h2 className="truncate text-xl font-semibold tracking-tight">
            {user.name}
          </h2>
          <p className="truncate text-sm text-muted-foreground">{user.email}</p>
          <Badge variant="secondary" className="mt-2">{getRoleLabel(user.role)}</Badge>
        </div>
      </CardHeader>

      <CardContent className="flex flex-col gap-5 p-5 sm:p-6">
        <div className="flex flex-wrap items-center gap-3 rounded-2xl border p-4">
          <Wallet className="size-5 text-muted-foreground" aria-hidden="true" />
          <div className="min-w-0 flex-1"><p className="text-sm text-muted-foreground">ยอดเงินคงเหลือ</p><p className="text-2xl font-semibold tabular-nums">{formatBalance(user.balanceSatang)}</p></div>
          <Button size="sm" nativeButton={false} render={<Link href="/topup" />}>
            <Wallet className="size-4" />
            เติมเงิน
          </Button>
        </div>
        <div>
          <h3 className="text-base font-semibold">ช่องทางเข้าสู่ระบบ</h3>
          <p className="mt-1 text-sm text-muted-foreground">
            เชื่อมหลายช่องทางกับบัญชีเดียวได้ แม้ใช้อีเมลต่างกัน
          </p>
        </div>

        <ul className="divide-y overflow-hidden rounded-2xl border bg-card">
          {hasPassword && (
            <MethodRow
              icon={<KeyRound className="size-5 text-muted-foreground" />}
              title="ชื่อผู้ใช้และรหัสผ่าน"
              status="พร้อมใช้งาน"
              connected
            />
          )}

          {(Object.keys(PROVIDERS) as Provider[]).map((provider) => {
            const { label, icon } = PROVIDERS[provider];
            const linked = accounts.filter((a) => a.providerId === provider);
            const configured = providers[provider];
            const isLinked = linked.length > 0;

            return (
              <MethodRow
                key={provider}
                icon={icon}
                title={label}
                connected={isLinked}
                status={
                  isLinked
                    ? "เชื่อมแล้ว"
                    : configured
                      ? "ยังไม่เชื่อม"
                      : "ยังไม่ได้ตั้งค่า"
                }
              >
                {linked.map((account) => (
                  <Button
                    key={account.id}
                    variant="outline"
                    size="sm"
                    aria-label={`ถอดการเชื่อมต่อ ${label}`}
                    disabled={pending || onlyOne || !configured}
                    onClick={() => unlink(account.id)}
                  >
                    <Link2Off className="size-4" />
                    ถอด
                  </Button>
                ))}
                {!isLinked && (
                  <Button
                    size="sm"
                    aria-label={`เชื่อม ${label}`}
                    disabled={pending || !configured}
                    onClick={() => link(provider)}
                  >
                    <Link2 className="size-4" />
                    เชื่อม
                  </Button>
                )}
              </MethodRow>
            );
          })}
        </ul>

        {accounts.length === 1 && (
          <div className="flex items-start gap-2.5 rounded-xl bg-amber-500/10 px-3.5 py-3 text-sm text-amber-900 dark:text-amber-200">
            <Info className="mt-0.5 size-4 shrink-0" />
            <p>ต้องเหลือช่องทางเข้าสู่ระบบอย่างน้อยหนึ่งช่องทาง</p>
          </div>
        )}

        <div className="border-t pt-5">
          <Button
            variant="destructive"
            className="w-full"
            disabled={pending}
            onClick={logout}
          >
            <LogOut className="size-4" />
            {pending ? "กำลังดำเนินการ…" : "ออกจากระบบ"}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
