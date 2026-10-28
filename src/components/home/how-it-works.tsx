"use client";

import Link from "next/link";
import { ArrowUpRight, Cable, Gamepad2, Monitor } from "lucide-react";
import { Button } from "@/components/ui/button";

const steps = [
  {
    number: "01",
    title: "เลือกเครื่อง",
    description: "ดูสเปก ราคา และเครื่องที่ว่างในหน้าเช่า แต่ละเครื่องมีราคาเป็นของตัวเอง เลือกเครื่องที่เหมาะกับเกมของคุณได้เลย",
    icon: Monitor,
  },
  {
    number: "02",
    title: "เชื่อมต่อ",
    description: "ใช้ Moonlight หรือ Parsec เชื่อมต่อคอมพิวเตอร์ที่เช่าจากอุปกรณ์ของคุณ แล้วทำตามขั้นตอนการเชื่อมต่อบนหน้าเช่า",
    icon: Cable,
  },
  {
    number: "03",
    title: "เริ่มเล่น",
    description: "เมื่อเชื่อมต่อแล้ว เข้าเครื่อง เปิดเกม แล้วเล่นได้ตามเวลาที่เช่า พร้อมออกไปสนุกกับเกมที่คุณชอบ",
    icon: Gamepad2,
  },
];

export function HowItWorks() {

  return (
    <section id="how-it-works" aria-labelledby="how-it-works-heading" className="scroll-mt-28 px-5 py-16 sm:px-6 sm:py-24">
      <div className="mx-auto max-w-6xl">
        <div data-scroll-reveal className="max-w-2xl">
          <h2 id="how-it-works-heading" className="text-3xl leading-tight font-bold tracking-tight sm:text-5xl">
            เช่าคอม <span className="text-primary">เล่นยังไง?</span>
          </h2>
          <p className="mt-3 text-sm text-muted-foreground sm:text-base">จากเครื่องที่ใช่ สู่เกมที่ชอบ ใน 3 ขั้นตอน</p>
        </div>

        <ol className="mt-10 grid gap-8 md:mt-14 md:grid-cols-3 md:gap-10">
          {steps.map(({ number, title, description, icon: Icon }) => (
            <li
              key={number}
              data-scroll-reveal
              className="border-t border-primary/25 pt-6 sm:pt-8"
            >
              <div className="flex items-center justify-between gap-4" aria-hidden="true">
                <span className="text-5xl leading-none font-semibold tracking-tighter text-primary/25 sm:text-6xl">{number}</span>
                <span className="flex size-12 items-center justify-center rounded-2xl bg-primary/10 text-primary"><Icon className="size-6" strokeWidth={1.7} /></span>
              </div>
              <h3 className="mt-6 text-xl font-semibold sm:text-2xl">{title}</h3>
              <p className="mt-3 max-w-sm text-sm leading-7 text-muted-foreground sm:text-base sm:leading-8">{description}</p>
            </li>
          ))}
        </ol>

        <div data-scroll-reveal className="mt-12 flex justify-center sm:mt-16">
          <Button nativeButton={false} render={<Link href="/rent" />} size="lg">
            ดูเครื่องที่เปิดให้เช่า <ArrowUpRight data-icon="inline-end" />
          </Button>
        </div>
      </div>
    </section>
  );
}
