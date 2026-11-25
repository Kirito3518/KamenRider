import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Badge } from "@/components/ui/badge";
import { UserAvatar } from "@/components/auth/user-avatar";
import { Empty, EmptyDescription, EmptyHeader, EmptyTitle } from "@/components/ui/empty";
import { getRoleLabel } from "@/lib/roles";
import type { AdminMember } from "@/lib/admin";

export function MemberTable({ members }: { members: AdminMember[] }) {
  if (!members.length) return <Empty><EmptyHeader><EmptyTitle>ยังไม่มีสมาชิก</EmptyTitle><EmptyDescription>บัญชีที่สมัครจะแสดงในหน้านี้</EmptyDescription></EmptyHeader></Empty>;
  return <Table><caption className="sr-only">รายชื่อสมาชิก</caption><TableHeader><TableRow><TableHead>สมาชิก</TableHead><TableHead>อีเมล</TableHead><TableHead>สิทธิ์การใช้งาน</TableHead></TableRow></TableHeader>
    <TableBody>{members.map(user => <TableRow key={user.id}>
      <TableCell><span className="flex items-center gap-3"><UserAvatar user={user} /><span className="max-w-48 truncate font-medium">{user.name}</span></span></TableCell>
      <TableCell>{user.email}</TableCell>
      <TableCell><Badge variant="secondary">{getRoleLabel(user.role)}</Badge></TableCell>
    </TableRow>)}</TableBody></Table>;
}
