"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { ChevronsUpDown, History, LogOut, MonitorPlay, UserRound, Wallet, ShieldCheck } from "lucide-react";
import { formatBalance } from "@/lib/money";
import toast from "react-hot-toast";
import { authClient } from "@/lib/auth-client";
import { authError } from "@/lib/auth-errors";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuGroup, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { UserAvatar } from "@/components/auth/user-avatar";
import { Badge } from "@/components/ui/badge";
import { getRoleLabel } from "@/lib/roles";

export function AccountDropdown({ user, onNavigate }: {
  user: { id: string; name: string; email: string; image?: string | null; balanceSatang: number; role: string };
  onNavigate?: () => void;
}) {
  const router = useRouter();
  const [pending, setPending] = useState(false);
  async function logout() {
    setPending(true);
    try {
      const result = await authClient.signOut();
      if (result.error) { toast.error(authError(result.error)); return; }
      toast.success("ออกจากระบบแล้ว");
      onNavigate?.();
      router.replace("/");
      router.refresh();
    } catch { toast.error("ออกจากระบบไม่สำเร็จ กรุณาลองใหม่"); }
    finally { setPending(false); }
  }
  return <DropdownMenu>
    <DropdownMenuTrigger render={<Button variant="ghost" className="h-10 gap-2 rounded-full px-1.5 sm:pr-3" />} aria-label={`เมนูบัญชี ${user.name}`} disabled={pending}>
      <UserAvatar user={user} />
      <span className="hidden max-w-24 truncate sm:inline">{user.name}</span>
      <ChevronsUpDown className="hidden size-3.5 text-muted-foreground sm:block" />
    </DropdownMenuTrigger>
    <DropdownMenuContent align="end" sideOffset={10} className="w-64" data-lenis-prevent>
      <DropdownMenuGroup>
        <DropdownMenuLabel>
          <span className="flex items-center gap-3"><UserAvatar user={user} size="lg" /><span className="flex min-w-0 flex-col gap-1"><span className="truncate font-medium text-foreground">{user.name}</span><span className="truncate">{user.email}</span></span></span>
        </DropdownMenuLabel>
      </DropdownMenuGroup>
      <div className="px-3 pb-2"><Badge variant="secondary">{getRoleLabel(user.role)}</Badge></div>
      <DropdownMenuSeparator />
      <div className="flex items-center justify-between gap-3 px-3 py-2.5 text-sm"><span className="flex items-center gap-2 text-muted-foreground"><Wallet className="size-4" aria-hidden="true" />ยอดเงิน</span><span className="font-medium tabular-nums">{formatBalance(user.balanceSatang)}</span></div>
      <DropdownMenuSeparator />
      <DropdownMenuGroup>
        {user.role === "admin" && <DropdownMenuItem render={<Link href="/admin" />} onClick={onNavigate}><ShieldCheck />จัดการระบบ</DropdownMenuItem>}
        <DropdownMenuItem render={<Link href="/account" />} onClick={onNavigate}><UserRound />บัญชีของฉัน</DropdownMenuItem>
        <DropdownMenuItem render={<Link href="/my-machines" />} onClick={onNavigate}><MonitorPlay />เครื่องของฉัน</DropdownMenuItem>
        <DropdownMenuItem render={<Link href="/history" />} onClick={onNavigate}><History />ประวัติการเช่า</DropdownMenuItem>
      </DropdownMenuGroup>
      <DropdownMenuSeparator />
      <DropdownMenuGroup><DropdownMenuItem variant="destructive" disabled={pending} onClick={logout}><LogOut />{pending ? "กำลังออกจากระบบ…" : "ออกจากระบบ"}</DropdownMenuItem></DropdownMenuGroup>
    </DropdownMenuContent>
  </DropdownMenu>;
}
