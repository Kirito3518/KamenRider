import {
  CircleAlert,
  CircleCheck,
  Clock3,
  Gift,
  Landmark,
  QrCode,
  TicketPercent,
  XCircle,
} from "lucide-react";
import type { ComponentType } from "react";
import { Badge } from "@/components/ui/badge";
import { getBankPreset, getBankPresetByLogoKey } from "@/lib/topup/banks";
import type {
  TopupChannel,
  TopupStatus,
  VerificationStatus,
} from "@/lib/topup/constants";
import { cn } from "@/lib/utils";

export type BadgeTone =
  | "default"
  | "available"
  | "busy"
  | "maintenance"
  | "secondary"
  | "destructive"
  | "outline";

const channelLabels: Record<TopupChannel, string> = {
  truemoney: "TrueMoney Angpao",
  promptpay: "PromptPay QR",
  bank_transfer: "โอนผ่านธนาคาร",
  coupon: "คูปอง",
};

const statusLabels: Record<TopupStatus, string> = {
  pending: "รอชำระเงิน",
  processing: "กำลังตรวจสอบ",
  completed: "สำเร็จ",
  failed: "ไม่สำเร็จ",
  expired: "หมดอายุ",
};

const statusTones: Record<TopupStatus, BadgeTone> = {
  pending: "busy",
  processing: "default",
  completed: "available",
  failed: "destructive",
  expired: "secondary",
};

const verificationLabels: Record<VerificationStatus, string> = {
  pending: "ยังไม่ตรวจสอบ",
  verified: "ตรวจสอบผ่าน",
  rejected: "ตรวจสอบไม่ผ่าน",
  duplicate: "สลิปซ้ำ",
  error: "ตรวจสอบผิดพลาด",
  unknown: "รอผลตรวจสอบ",
};

const verificationTones: Record<VerificationStatus, BadgeTone> = {
  pending: "outline",
  verified: "available",
  rejected: "destructive",
  duplicate: "destructive",
  error: "maintenance",
  unknown: "busy",
};

export function topupChannelLabel(channel: TopupChannel) {
  return channelLabels[channel];
}

export function topupStatusLabel(status: TopupStatus) {
  return statusLabels[status];
}

export function topupStatusTone(status: TopupStatus): BadgeTone {
  return statusTones[status];
}

export function verificationLabel(status: VerificationStatus) {
  return verificationLabels[status];
}

export function verificationTone(status: VerificationStatus): BadgeTone {
  return verificationTones[status];
}

export function topupChannelIcon(channel: TopupChannel): ComponentType<{ className?: string }> {
  switch (channel) {
    case "truemoney":
      return Gift;
    case "promptpay":
      return QrCode;
    case "bank_transfer":
      return Landmark;
    default:
      return TicketPercent;
  }
}

export function verificationIcon(status: VerificationStatus) {
  switch (status) {
    case "verified":
      return CircleCheck;
    case "rejected":
    case "duplicate":
      return XCircle;
    case "error":
      return CircleAlert;
    default:
      return Clock3;
  }
}

export function ChannelBadge({ channel }: { channel: TopupChannel }) {
  return (
    <Badge variant="outline" className="gap-1.5 whitespace-nowrap">
      {channel === "truemoney" ? (
        <Gift aria-hidden />
      ) : channel === "promptpay" ? (
        <QrCode aria-hidden />
      ) : channel === "bank_transfer" ? (
        <Landmark aria-hidden />
      ) : (
        <TicketPercent aria-hidden />
      )}
      {topupChannelLabel(channel)}
    </Badge>
  );
}

export function StatusBadge({ status }: { status: TopupStatus }) {
  return (
    <Badge variant={topupStatusTone(status)}>{topupStatusLabel(status)}</Badge>
  );
}

export function VerificationBadge({ status }: { status: VerificationStatus }) {
  return (
    <Badge variant={verificationTone(status)} className="gap-1.5 whitespace-nowrap">
      {status === "verified" ? (
        <CircleCheck aria-hidden />
      ) : status === "rejected" || status === "duplicate" ? (
        <XCircle aria-hidden />
      ) : status === "error" ? (
        <CircleAlert aria-hidden />
      ) : (
        <Clock3 aria-hidden />
      )}
      {verificationLabel(status)}
    </Badge>
  );
}

/**
 * Bank logo for preset banks (brand-coloured tile with the short name) or an
 * uploaded data URL. Admins may replace the generated tile with an official logo.
 */
export function BankLogo({
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
      // User-uploaded logo is size/MIME validated server-side; eslint's next/image
      // rule does not help for data URLs.
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
