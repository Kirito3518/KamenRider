import { z } from "zod";

export const roleField = {
  type: "string", required: true, defaultValue: "user", input: false,
  validator: { input: z.enum(["user", "admin"]), output: z.enum(["user", "admin"]) },
} as const;
