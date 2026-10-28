import Link from "next/link";
import { ArrowUpRight, Monitor } from "lucide-react";

const links = [
  { label: "หน้าแรก", href: "/" },
  { label: "เช่าคอมพิวเตอร์", href: "/rent" },
  { label: "เครื่องของฉัน", href: "/my-machines" },
  { label: "วิธีใช้งาน", href: "/#how-it-works" },
  { label: "คำถามที่พบบ่อย", href: "/#faq" },
];

export function Footer() {
  return (
    <footer className="border-t border-border bg-background px-5 py-10 sm:px-6 sm:py-14">
      <div data-scroll-reveal className="mx-auto max-w-6xl">
        <div className="flex flex-col justify-between gap-8 md:flex-row md:items-start">
          <div>
            <Link href="/" className="inline-flex items-center gap-2 text-lg font-bold tracking-tight" aria-label="KamenRaiderPlay หน้าแรก">
              <Monitor className="size-5 text-primary" />
              <span>KamenRaider<span className="text-primary">Play</span></span>
            </Link>
            <p className="mt-3 text-sm text-muted-foreground">เช่าคอมง่าย เล่นได้ทุกที่ ในแบบของคุณ</p>
          </div>
          <nav aria-label="เมนูท้ายเว็บไซต์" className="flex flex-wrap gap-x-6 gap-y-3 text-sm text-muted-foreground">
            {links.map(({ label, href }) => (
              <Link key={href} href={href} className="rounded-sm transition-colors hover:text-primary focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-ring">{label}</Link>
            ))}
          </nav>
        </div>
        <div className="mt-10 flex flex-col justify-between gap-3 text-xs text-muted-foreground sm:mt-12 sm:flex-row sm:items-center sm:text-sm">
          <p>© 2026 KamenRaiderPlay</p>
          <p className="flex items-center gap-1.5">
            Made with <span className="text-primary">♥</span> by
            <a href="https://www.asynx.me/" target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 rounded-sm font-semibold text-foreground transition-colors hover:text-primary focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-ring">
              Asynx <ArrowUpRight className="size-3.5" aria-hidden="true" /><span className="sr-only"> (เปิดในแท็บใหม่)</span>
            </a>
          </p>
        </div>
      </div>
    </footer>
  );
}
