"use client";

import Link from "next/link";
import { ArrowUpRight, Monitor } from "lucide-react";
import { Button } from "@/components/ui/button";
import { useLandingMotion } from "@/hooks/use-landing-motion";
import { Navbar } from "@/components/ui/layout/Navbar";
import { Footer } from "@/components/ui/layout/Footer";
import { GameMarquee } from "@/components/home/game-marquee";
import { HowItWorks } from "@/components/home/how-it-works";
import { Faq } from "@/components/home/faq";

export default function HomePage() {
  const pageRef = useLandingMotion();

  return (
    <div ref={pageRef} className="min-h-dvh bg-background text-foreground">
      <Navbar />
      <main>
        <section className="flex min-h-[calc(100dvh-72px)] items-center justify-center px-5 pb-[75px] text-center sm:min-h-[calc(100dvh-82px)] sm:px-6 sm:pb-[100px]">
          <div className="w-full">
            <h1 data-hero-reveal className="text-[clamp(35px,9vw,48px)] leading-[1.15] font-bold tracking-[-0.055em] sm:text-[clamp(42px,5vw,68px)]">
              <span className="text-primary">KamenRaiderPlay</span>
              <br />
              เช่าคอมง่าย เล่นได้ทุกที่
            </h1>
            <p data-hero-reveal className="mx-auto mt-7 text-[15px] leading-[1.65] text-muted-foreground sm:text-lg">
              เปิดประสบการณ์เล่นเกมบนคอมพิวเตอร์ระยะไกล
              <br className="hidden sm:block" /> เลือกเครื่องที่ใช่ แล้วเริ่มเล่นได้ในแบบของคุณ
            </p>
            <div data-hero-reveal className="mx-auto mt-[34px] flex min-h-[53px] w-full max-w-[510px] items-center justify-between gap-2 rounded-full border bg-card p-[5px] pl-3.5 shadow-[0_10px_22px_rgb(0_0_0/8%)] sm:pl-5">
              <div className="flex items-center gap-2 text-xs text-muted-foreground sm:text-sm">
                <Monitor className="size-[18px] shrink-0 text-primary" />
                <span>พร้อมออกไปเล่นด้วยกันหรือยัง?</span>
              </div>
              <Button nativeButton={false} render={<Link href="/rent" />} size="lg">
                เริ่มเช่าคอม <ArrowUpRight data-icon="inline-end" />
              </Button>
            </div>
            <div data-hero-reveal className="mt-[27px] flex items-center justify-center gap-2 text-[13px] text-muted-foreground">
              <span className="size-2 rounded-full bg-primary" /> เช่าคอมออนไลน์
              <span className="text-primary">·</span> เล่นได้ทุกที่ ทุกเวลา
            </div>
          </div>
        </section>
        <GameMarquee />
        <HowItWorks />
        <Faq />
      </main>
      <Footer />
    </div>
  );
}
