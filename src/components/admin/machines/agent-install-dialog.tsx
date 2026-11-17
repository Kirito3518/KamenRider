"use client";

import { useCallback, useEffect, useState } from "react";
import { Terminal } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Spinner } from "@/components/ui/spinner";
import { Textarea } from "@/components/ui/textarea";
import { getInstallCommandExpiryState } from "@/lib/agent-install-expiry.mjs";

type AgentInstallCommandResult = {
  available: boolean;
  command?: string;
  expiresAtUtc?: string;
  reason?: string;
};

async function fetchInstallCommand(): Promise<AgentInstallCommandResult> {
  const response = await fetch("/api/admin/agent-install-command", {
    method: "POST",
    cache: "no-store",
  });
  if (!response.ok) throw new Error("ตรวจชุดติดตั้งไม่ได้");
  return (await response.json()) as AgentInstallCommandResult;
}

export function AgentInstallDialog({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const [installCommand, setInstallCommand] = useState<string | null>(null);
  const [installExpiresAtUtc, setInstallExpiresAtUtc] = useState<string | null>(null);
  const [installClockNow, setInstallClockNow] = useState(0);
  const [isRefreshingInstallCommand, setIsRefreshingInstallCommand] = useState(false);
  const [installStatus, setInstallStatus] = useState("กดปุ่มเพื่อขอคำสั่งติดตั้ง");
  const [copyStatus, setCopyStatus] = useState("");

  const refreshInstallCommand = useCallback(async () => {
    setIsRefreshingInstallCommand(true);
    setInstallStatus("กำลังขอคำสั่งติดตั้ง…");
    setCopyStatus("");
    try {
      const result = await fetchInstallCommand();
      const available = result.available && Boolean(result.command) && Boolean(result.expiresAtUtc);
      setInstallCommand(available ? result.command! : null);
      setInstallExpiresAtUtc(available ? result.expiresAtUtc! : null);
      setInstallClockNow(Date.now());
      setInstallStatus(available ? "ชุดติดตั้งพร้อมใช้งาน" : result.reason || "ชุดติดตั้งยังไม่พร้อม");
    } catch (error) {
      setInstallStatus(`ตรวจชุดติดตั้งไม่ได้: ${String(error)}`);
    } finally {
      setIsRefreshingInstallCommand(false);
    }
  }, []);

  useEffect(() => {
    if (!installExpiresAtUtc) return;
    const expiresAtMs = Date.parse(installExpiresAtUtc);
    const updateClock = () => setInstallClockNow(Date.now());
    if (!Number.isFinite(expiresAtMs)) {
      updateClock();
      return;
    }
    const timer = window.setTimeout(updateClock, Math.max(0, expiresAtMs - Date.now()));
    window.addEventListener("focus", updateClock);
    document.addEventListener("visibilitychange", updateClock);
    return () => {
      window.clearTimeout(timer);
      window.removeEventListener("focus", updateClock);
      document.removeEventListener("visibilitychange", updateClock);
    };
  }, [installExpiresAtUtc]);

  const installExpiry = installExpiresAtUtc
    ? getInstallCommandExpiryState(installExpiresAtUtc, installClockNow)
    : null;

  async function copyInstallCommand() {
    if (!installCommand || !installExpiresAtUtc) return;
    const expiry = getInstallCommandExpiryState(installExpiresAtUtc, Date.now());
    setInstallClockNow(Date.now());
    if (expiry.expired) {
      setCopyStatus("คำสั่งหมดอายุแล้ว กรุณาขอคำสั่งใหม่");
      return;
    }
    try {
      await navigator.clipboard.writeText(installCommand);
      setCopyStatus("คัดลอกคำสั่งแล้ว");
    } catch {
      setCopyStatus("คัดลอกอัตโนมัติไม่ได้ เลือกข้อความในช่องแล้วกด Ctrl+C");
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[85dvh] overflow-y-auto sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>ติดตั้ง Agent บนเครื่องลูก</DialogTitle>
          <DialogDescription>
            สร้างคำสั่ง PowerShell สำหรับลงทะเบียนเครื่องใหม่ คำสั่งมีอายุจำกัดและใช้ได้ครั้งเดียว
          </DialogDescription>
        </DialogHeader>
        <div className="flex flex-col gap-4">
          <ol className="flex list-inside list-decimal flex-col gap-1 text-sm text-muted-foreground">
            <li>เปิด PowerShell แบบ Administrator บนเครื่องลูก</li>
            <li>คัดลอกคำสั่งด้านล่างไปรันบนเครื่องนั้น</li>
            <li>หลังลงทะเบียนแล้ว หน้าแอดมินจะเปิดขึ้นและเครื่องจะแสดงเป็น pending</li>
          </ol>
          <div className="flex flex-wrap items-center gap-2">
            <Button
              type="button"
              size="sm"
              disabled={isRefreshingInstallCommand}
              onClick={() => void refreshInstallCommand()}
            >
              {isRefreshingInstallCommand ? <Spinner data-icon="inline-start" /> : <Terminal data-icon="inline-start" />}
              {isRefreshingInstallCommand
                ? "กำลังขอคำสั่ง…"
                : installCommand
                  ? "ขอคำสั่งติดตั้งใหม่"
                  : "ขอคำสั่งติดตั้ง"}
            </Button>
            {installCommand ? <Badge variant="busy">ใช้ได้ครั้งเดียว</Badge> : null}
          </div>
          <p role="status" aria-live="polite" className="text-sm">
            {installCommand && installExpiry?.expired
              ? "คำสั่งติดตั้งหมดอายุแล้ว กรุณาขอคำสั่งใหม่"
              : installStatus}
          </p>
          {installCommand ? (
            <div className="flex flex-col gap-3">
              <p className="text-sm">หมดอายุเมื่อ (UTC): {installExpiry?.displayUtc || "ไม่ทราบ"}</p>
              <Textarea
                id="agent-install-command"
                className="font-mono text-xs"
                rows={5}
                readOnly
                spellCheck={false}
                value={installCommand}
                aria-label="คำสั่งติดตั้ง Agent"
              />
              <div className="flex flex-wrap items-center gap-2">
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  disabled={isRefreshingInstallCommand || !installExpiresAtUtc || installExpiry?.expired !== false}
                  onClick={() => void copyInstallCommand()}
                >
                  คัดลอกคำสั่ง PowerShell
                </Button>
                <p role="status" aria-live="polite" className="text-sm text-muted-foreground">
                  {copyStatus}
                </p>
              </div>
            </div>
          ) : null}
          {installStatus.startsWith("ตรวจชุดติดตั้งไม่ได้") ? (
            <Alert variant="destructive">
              <AlertTitle>ตรวจชุดติดตั้งไม่ได้</AlertTitle>
              <AlertDescription>
                ตรวจสอบการตั้งค่า Controller และชุดติดตั้ง แล้วกดขอคำสั่งอีกครั้ง
              </AlertDescription>
            </Alert>
          ) : null}
        </div>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            ปิด
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
