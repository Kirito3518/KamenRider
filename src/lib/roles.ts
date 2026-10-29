export const roleLabels = { user: "สมาชิก", admin: "ผู้ดูแลระบบ" } as const;
export type UserRole = keyof typeof roleLabels;

export function getRoleLabel(role: string) {
  return role === "admin" ? roleLabels.admin : roleLabels.user;
}
