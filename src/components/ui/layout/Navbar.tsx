"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import {
  History,
  Home,
  LogIn,
  Menu,
  Monitor,
  MonitorPlay,
  Moon,
  Sun,
  Wallet,
  X,
} from "lucide-react";
import { useRef, useState, type MouseEventHandler } from "react";
import { useThemeTransition } from "@/hooks/use-theme-transition";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { AuthDialog } from "@/components/auth/auth-dialog";
import { authClient } from "@/lib/auth-client";
import { AccountDropdown } from "@/components/auth/account-dropdown";
import { cn } from "@/lib/utils";
import {
  Navbar as ResizableNavbar,
  NavBody,
  MobileNav,
  MobileNavHeader,
  MobileNavMenu,
} from "@/components/ui/resizable-navbar";

const links = [
  { label: "หน้าแรก", href: "/", icon: Home },
  { label: "เช่าคอมพิวเตอร์", href: "/rent", icon: Monitor },
  { label: "เครื่องของฉัน", href: "/my-machines", icon: MonitorPlay },
  { label: "เติมเงิน", href: "/topup", icon: Wallet },
  { label: "ประวัติ", href: "/history", icon: History },
];

function Brand() {
  return (
    <Link
      href="/"
      className="inline-flex shrink-0 items-center gap-2 whitespace-nowrap text-[15px] font-bold tracking-[-0.035em] sm:text-[17px]"
      aria-label="KamenRaiderPlay หน้าแรก"
    >
      <Monitor className="size-5 shrink-0 text-primary" />
      <span>
        KamenRaider<span className="text-primary">Play</span>
      </span>
    </Link>
  );
}

function ThemeToggle() {
  const toggleTheme = useThemeTransition();
  return (
    <Button
      variant="outline"
      size="icon"
      onClick={toggleTheme}
      aria-label="สลับโหมดสว่างและโหมดมืด"
    >
      <Sun className="hidden dark:block" />
      <Moon className="dark:hidden" />
    </Button>
  );
}

function LoginButton({ onClick, onNavigate, mobile = false }: { onClick: MouseEventHandler<HTMLButtonElement>; onNavigate?: () => void; mobile?: boolean }) {
  const { data: session, isPending } = authClient.useSession();
  if (isPending) return (
    <div
      role="status"
      aria-busy="true"
      className={cn("inline-flex items-center justify-center", mobile ? "size-9" : "h-9 w-24")}
    >
      <span className="sr-only">กำลังโหลดบัญชี</span>
      <Skeleton aria-hidden="true" className="size-full rounded-full" />
    </div>
  );
  if (session) return <AccountDropdown user={session.user} onNavigate={onNavigate} />;
  return <Button onClick={onClick} className={mobile ? "max-[399px]:size-9 max-[399px]:px-0" : undefined}>{mobile && <LogIn className="min-[400px]:hidden" />}<span className={mobile ? "max-[399px]:sr-only" : undefined}>เข้าสู่ระบบ</span></Button>;
}

export function Navbar() {
  const pathname = usePathname();
  const [menuOpen, setMenuOpen] = useState(false);
  const [loginOpen, setLoginOpen] = useState(false);
  const loginTriggerRef = useRef<HTMLButtonElement>(null);
  const mobileMenuTriggerRef = useRef<HTMLButtonElement>(null);
  const openLogin: MouseEventHandler<HTMLButtonElement> = (event) => {
    loginTriggerRef.current = event.currentTarget.closest("#mobile-menu")
      ? mobileMenuTriggerRef.current
      : event.currentTarget;
    setMenuOpen(false);
    setLoginOpen(true);
  };

  return (
    <>
    <ResizableNavbar className="top-0 pt-3.5 sm:pt-6">
      <NavBody className="h-[58px] border bg-card px-6 py-0 pl-7 shadow-[0_12px_35px_rgb(0_0_0/4%)]">
        <Brand />
        <nav className="flex items-center gap-0.5" aria-label="เมนูหลัก">
          {links.map(({ label, href, icon: Icon }) => (
            <Link
              key={label}
              href={href}
              aria-current={pathname === href ? "page" : undefined}
              className={cn(
                "inline-flex items-center gap-1.5 rounded-full px-2.5 py-[7px] text-[13px] font-medium whitespace-nowrap text-muted-foreground transition-colors hover:bg-accent hover:text-foreground xl:gap-2 xl:px-3 xl:text-sm",
                pathname === href && "bg-accent text-foreground",
              )}
            >
              <Icon
                className={cn("size-[17px]", pathname === href && "text-primary")}
                strokeWidth={1.8}
              />
              {label}
            </Link>
          ))}
        </nav>
        <div className="flex items-center gap-2">
          <ThemeToggle />
          <LoginButton onClick={openLogin} />
        </div>
      </NavBody>
      <MobileNav className="min-h-[58px] border bg-card px-3 py-2 shadow-sm sm:px-5">
        <MobileNavHeader>
          <Brand />
          <div className="flex items-center gap-1 sm:gap-2">
            <ThemeToggle />
            <LoginButton mobile onClick={openLogin} onNavigate={() => setMenuOpen(false)} />
            <Button
              variant="outline"
              size="icon"
              onClick={() => setMenuOpen(!menuOpen)}
              aria-label={menuOpen ? "ปิดเมนู" : "เปิดเมนู"}
              aria-expanded={menuOpen}
              aria-controls="mobile-menu"
              ref={mobileMenuTriggerRef}
            >
              {menuOpen ? <X /> : <Menu />}
            </Button>
          </div>
        </MobileNavHeader>
        <MobileNavMenu isOpen={menuOpen} onClose={() => setMenuOpen(false)}>
          <nav
            id="mobile-menu"
            aria-label="เมนูมือถือ"
            className="flex w-full flex-col gap-1"
          >
            {links.map(({ label, href, icon: Icon }) => (
              <Link
                className="flex items-center gap-2.5 rounded-xl px-3 py-2.5 text-sm hover:bg-accent"
                href={href}
                aria-current={pathname === href ? "page" : undefined}
                key={label}
                onClick={() => setMenuOpen(false)}
              >
                <Icon className="size-[18px]" />
                {label}
              </Link>
            ))}
          </nav>
        </MobileNavMenu>
      </MobileNav>
    </ResizableNavbar>
    <AuthDialog open={loginOpen} onOpenChange={setLoginOpen} returnFocusRef={loginTriggerRef} />
    </>
  );
}
