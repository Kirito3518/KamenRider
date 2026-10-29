"use client";

import { createAuthClient } from "better-auth/react";
import { inferAdditionalFields, usernameClient } from "better-auth/client/plugins";
import type { getAuth } from "@/lib/auth";

export const authClient = createAuthClient({ plugins: [usernameClient(), inferAdditionalFields<Awaited<ReturnType<typeof getAuth>>>()] });
