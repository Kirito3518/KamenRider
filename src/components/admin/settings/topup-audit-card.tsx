"use client";

import { History, ShieldCheck } from "lucide-react";
import { AdminCard } from "@/components/admin/admin-page";
import {
  auditActionLabel,
  auditTargetLabel,
  formatAdminDateTime,
  latestAuditEntries,
} from "@/components/admin/settings/topup-settings-api";
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty";
import {
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import type { TopupSettingsAuditEntry } from "@/lib/topup/types";

/**
 * Latest audit entries. Only the human-readable action/target/time are rendered;
 * `changes` is never shown so a key material value can never leak through it.
 */
export function TopupAuditCard({
  entries,
}: {
  entries: TopupSettingsAuditEntry[];
}) {
  const rows = latestAuditEntries(entries);
  return (
    <AdminCard>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <History aria-hidden className="size-4 text-primary" />
          ประวัติการตั้งค่าล่าสุด
        </CardTitle>
        <CardDescription>
          รายการล่าสุด 10 รายการ · ไม่แสดงข้อมูลคีย์หรือข้อมูลลับใด ๆ
        </CardDescription>
      </CardHeader>
      <CardContent>
        {rows.length === 0 ? (
          <Empty>
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <ShieldCheck aria-hidden />
              </EmptyMedia>
              <EmptyTitle>ยังไม่มีประวัติการตั้งค่า</EmptyTitle>
              <EmptyDescription>
                เมื่อมีการบันทึกการตั้งค่า รายการจะแสดงที่นี่
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>เวลา</TableHead>
                <TableHead>การดำเนินการ</TableHead>
                <TableHead>เป้าหมาย</TableHead>
                <TableHead>ผู้ดูแล</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((entry) => (
                <TableRow key={entry.id}>
                  <TableCell className="whitespace-nowrap text-muted-foreground">
                    {formatAdminDateTime(entry.createdAt)}
                  </TableCell>
                  <TableCell className="font-medium">
                    {auditActionLabel(entry.action)}
                  </TableCell>
                  <TableCell className="text-muted-foreground">
                    {auditTargetLabel(entry)}
                  </TableCell>
                  <TableCell>{entry.actorName}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </AdminCard>
  );
}
