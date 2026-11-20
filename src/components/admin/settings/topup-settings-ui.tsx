"use client";

import { Badge } from "@/components/ui/badge";
import { getBankPreset, getBankPresetByLogoKey } from "@/lib/topup/banks";
import { cn } from "@/lib/utils";

/** Small shared status atoms for the top-up settings cards. */

export function EnabledBadge({
  enabled,
  onLabel = "เปิด",
  offLabel = "ปิด",
}: {
  enabled: boolean;
  onLabel?: string;
  offLabel?: string;
}) {
  return (
    <Badge variant={enabled ? "available" : "outline"}>
      {enabled ? onLabel : offLabel}
    </Badge>
  );
}

export function ConfiguredBadge({ configured }: { configured: boolean }) {
  return (
    <Badge variant={configured ? "available" : "outline"}>
      {configured ? "ตั้งค่าแล้ว" : "ยังไม่ได้ตั้งค่า"}
    </Badge>
  );
}

export function LastTestBadge({
  lastTestAt,
  lastTestOk,
}: {
  lastTestAt: number | null;
  lastTestOk: boolean | null;
}) {
  if (lastTestAt === null) {
    return <Badge variant="outline">ยังไม่เคยทดสอบ</Badge>;
  }
  return (
    <Badge variant={lastTestOk ? "available" : "destructive"}>
      {lastTestOk ? "ทดสอบล่าสุดสำเร็จ" : "ทดสอบล่าสุดไม่สำเร็จ"}
    </Badge>
  );
}

/**
 * Bank logo tile for the admin bank-account list and dialog preview: uploaded
 * data URL when present, otherwise the brand-coloured preset tile.
 */
export function BankLogoTile({
  bankCode,
  bankName,
  logoKey,
  logoDataUrl,
  className,
}: {
  bankCode: string;
  bankName?: string | null;
  logoKey?: string | null;
  logoDataUrl?: string | null;
  className?: string;
}) {
  const preset = getBankPresetByLogoKey(logoKey) ?? getBankPreset(bankCode);
  if (logoDataUrl) {
    return (
      // Uploaded logos are MIME/size validated by the server before storage.
      // eslint-disable-next-line @next/next/no-img-element
      <img
        src={logoDataUrl}
        alt={`โลโก้ ${bankName ?? preset?.name ?? "ธนาคาร"}`}
        className={cn("size-9 shrink-0 rounded-xl bg-white object-contain p-1", className)}
      />
    );
  }
  if (preset) {
    return (
      // Local, trusted SVG bank logo bundled with the app.
      // eslint-disable-next-line @next/next/no-img-element
      <img
        src={preset.logoUrl}
        alt={`โลโก้ ${bankName ?? preset.name}`}
        className={cn("size-9 shrink-0 rounded-xl object-contain", className)}
      />
    );
  }
  const shortName = bankName?.slice(0, 4) ?? bankCode;
  return (
    <span
      aria-hidden
      className={cn(
        "flex size-9 shrink-0 items-center justify-center rounded-xl text-[10px] font-bold tracking-tight",
        className,
      )}
      style={{
        backgroundColor: "var(--primary)",
        color: "var(--primary-foreground)",
      }}
    >
      {shortName}
    </span>
  );
}
