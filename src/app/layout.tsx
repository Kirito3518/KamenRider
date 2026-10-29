import type { Metadata } from "next";
import { Geist_Mono, IBM_Plex_Sans_Thai } from "next/font/google";
import "./globals.css";
import { cn } from "@/lib/utils";
import { ThemeProvider } from "@/components/theme-provider";
import { AuthToaster } from "@/components/auth/auth-toaster";

const ibmPlexSansThai = IBM_Plex_Sans_Thai({
  variable: "--font-ibm-plex-sans-thai",
  subsets: ["latin", "thai"],
  weight: ["100", "200", "300", "400", "500", "600", "700"],
  display: "swap",
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: "KamenRaiderPlay | เช่าคอมออนไลน์",
  description: "เช่าคอมพิวเตอร์ระยะไกล เล่นได้ทุกที่ในแบบของคุณ",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html
      lang="th" suppressHydrationWarning
      className={cn("h-full", "antialiased", ibmPlexSansThai.variable, geistMono.variable, "font-sans")}
    >
      <body className="min-h-full flex flex-col"><ThemeProvider attribute="class" defaultTheme="light" enableSystem>{children}<AuthToaster /></ThemeProvider></body>
    </html>
  );
}
