"use client";
import Link from "next/link";
import { History, Home, KeyRound, LayoutDashboard, Monitor, Package, Settings2, ShieldCheck, UserRound, Users, Wallet } from "lucide-react";
import { Sidebar, SidebarContent, SidebarFooter, SidebarHeader, SidebarMenu, SidebarMenuItem } from "@/components/ui/sidebar";
import { ScrollArea } from "@/components/ui/scroll-area";
import { NavMain } from "@/components/shadcn-space/blocks/sidebar-01/nav-main";

// Adapted from @shadcn-space/sidebar-01 for KamenRaiderPlay.
export function AppSidebar() {
  return <Sidebar collapsible="icon">
    <SidebarHeader className="py-5"><SidebarMenu><SidebarMenuItem>
      <Link href="/admin" aria-label="KamenRaiderPlay Admin" className="flex items-center gap-3 overflow-hidden px-2">
        <span className="flex size-8 shrink-0 items-center justify-center rounded-xl bg-primary text-primary-foreground"><Monitor className="size-5" /></span>
        <span className="flex flex-col truncate group-data-[collapsible=icon]:hidden"><span className="text-sm font-semibold">KamenRaiderPlay</span><span className="text-xs text-muted-foreground">ระบบผู้ดูแล</span></span>
      </Link>
    </SidebarMenuItem></SidebarMenu></SidebarHeader>
    <SidebarContent><ScrollArea className="h-full"><nav aria-label="เมนูผู้ดูแล">
      <NavMain title="จัดการระบบ" items={[
        { title: "PC Control", href: "/admin/pc-control", icon: Monitor },
        { title: "ภาพรวม", href: "/admin/overview", icon: LayoutDashboard },
        { title: "เครื่องและ Agent", href: "/admin/machines", icon: Monitor },
        { title: "ประวัติการเช่า", href: "/admin/rentals", icon: History },
        { title: "ประวัติการเติมเงิน", href: "/admin/topups", icon: Wallet },
        { title: "Agent releases", href: "/admin/agent-releases", icon: Package },
        { title: "สมาชิก", href: "/admin/users", icon: Users },
        { title: "ตั้งค่าเติมเงิน", href: "/admin/settings/topup", icon: Settings2 },
        { title: "ตั้งค่า Sunshine", href: "/admin/settings/sunshine", icon: KeyRound },
      ]} />
      <NavMain title="ทางลัด" items={[
        { title: "กลับหน้าเว็บไซต์", href: "/", icon: Home },
        { title: "บัญชีของฉัน", href: "/account", icon: UserRound },
        { title: "หน้าเช่าคอมพิวเตอร์", href: "/rent", icon: Monitor },
      ]} />
    </nav></ScrollArea></SidebarContent>
    <SidebarFooter className="p-3"><div className="flex items-center gap-3 rounded-xl bg-sidebar-accent p-3 group-data-[collapsible=icon]:p-1"><ShieldCheck className="size-5 shrink-0" /><div className="group-data-[collapsible=icon]:hidden"><p className="text-sm font-medium">พื้นที่ผู้ดูแลระบบ</p><p className="text-xs text-muted-foreground">ข้อมูลบัญชีและยอดเงินสมาชิก</p></div></div></SidebarFooter>
  </Sidebar>;
}
