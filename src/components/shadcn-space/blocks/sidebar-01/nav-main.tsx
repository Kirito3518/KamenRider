"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import type { LucideIcon } from "lucide-react";
import { SidebarGroup, SidebarGroupLabel, SidebarMenu, SidebarMenuButton, SidebarMenuItem, useSidebar } from "@/components/ui/sidebar";

export type NavItem = { title: string; href: string; icon: LucideIcon };
export function NavMain({ title, items }: { title: string; items: NavItem[] }) {
  const pathname = usePathname();
  const { setOpenMobile } = useSidebar();
  return <SidebarGroup><SidebarGroupLabel>{title}</SidebarGroupLabel><SidebarMenu>
    {items.map(({ title: label, href, icon: Icon }) => <SidebarMenuItem key={href}>
      <SidebarMenuButton render={<Link href={href} />} isActive={pathname === href} aria-current={pathname === href ? "page" : undefined} aria-label={label} title={label} onClick={() => setOpenMobile(false)}><Icon /><span>{label}</span></SidebarMenuButton>
    </SidebarMenuItem>)}
  </SidebarMenu></SidebarGroup>;
}
