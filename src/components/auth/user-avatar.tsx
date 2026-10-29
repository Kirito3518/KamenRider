"use client";

import { useState } from "react";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { getUserAvatar } from "@/lib/avatars";

export function UserAvatar({ user, size = "default" }: {
  user: { id: string; name: string; image?: string | null };
  size?: "default" | "sm" | "lg";
}) {
  const [failedImage, setFailedImage] = useState<string | null>(null);
  const preferred = getUserAvatar(user);
  const fallback = getUserAvatar({ id: user.id });
  const src = failedImage === preferred ? fallback : preferred;
  return <Avatar size={size}>
    <AvatarImage src={src} alt="" referrerPolicy="no-referrer" onLoadingStatusChange={status => { if (status === "error") setFailedImage(preferred); }} />
    <AvatarFallback>{user.name.trim().slice(0, 1).toUpperCase() || "U"}</AvatarFallback>
  </Avatar>;
}
