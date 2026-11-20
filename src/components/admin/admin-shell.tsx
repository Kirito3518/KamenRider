"use client";
import { usePathname } from "next/navigation";
import { SunMoon } from "lucide-react";
import { AppSidebar } from "@/components/shadcn-space/blocks/sidebar-01/app-sidebar";
import { SidebarInset, SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { TooltipProvider } from "@/components/ui/tooltip";
import { Separator } from "@/components/ui/separator";
import { Button } from "@/components/ui/button";
import { AccountDropdown } from "@/components/auth/account-dropdown";
import { useThemeTransition } from "@/hooks/use-theme-transition";
import type { AdminMember } from "@/lib/admin";

const titles: Record<string, string> = { "/admin": "ภาพรวม", "/admin/overview": "ภาพรวม", "/admin/pc-control": "PC Control", "/admin/users": "สมาชิก", "/admin/balances": "ยอดเงินสมาชิก", "/admin/machines": "เครื่องและ Agent", "/admin/rentals": "ประวัติการเช่า", "/admin/agent-releases": "Agent releases", "/admin/settings/sunshine": "ตั้งค่า Sunshine" };
export function AdminShell({ user, children }: { user: AdminMember; children: React.ReactNode }) {
  const pathname = usePathname();
  const toggleTheme = useThemeTransition();
  return <TooltipProvider><SidebarProvider><AppSidebar /><SidebarInset>
    <header className="sticky top-0 flex h-16 shrink-0 items-center justify-between gap-3 border-b bg-background px-4 sm:px-6">
      <div className="flex min-w-0 items-center gap-3"><SidebarTrigger aria-label="เปิดหรือปิดเมนูผู้ดูแล" /><Separator orientation="vertical" className="h-5" /><span className="truncate text-sm font-medium">{titles[pathname] || "ผู้ดูแลระบบ"}</span></div>
      <div className="flex items-center gap-2"><Button variant="ghost" size="icon" onClick={toggleTheme} aria-label="สลับโหมดสว่างและโหมดมืด"><SunMoon /></Button><AccountDropdown user={user} /></div>
    </header>
    <main className="flex min-w-0 w-full flex-1 flex-col gap-6 p-4 sm:p-6">{children}</main>
  </SidebarInset></SidebarProvider></TooltipProvider>;
}
