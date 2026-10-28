"use client";

import Link from "next/link";
import { useState, type FormEvent } from "react";
import { ArrowUpRight, Check, Clock3, Copy, Cpu, HardDrive, Info, MemoryStick, Monitor, MonitorPlay, ShieldCheck } from "lucide-react";
import { Navbar } from "@/components/ui/layout/Navbar";
import { Footer } from "@/components/ui/layout/Footer";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Field, FieldError, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Progress } from "@/components/ui/progress";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useLandingMotion } from "@/hooks/use-landing-motion";
import { rentalMachines } from "@/lib/rental-machines";

const machine = rentalMachines[0];

function CopyAddress({ value, label }: { value: string; label: string }) {
  const [message, setMessage] = useState("");
  const [copied, setCopied] = useState(false);
  async function copy() {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      setMessage("คัดลอกแล้ว");
    } catch {
      setMessage("คัดลอกไม่ได้ กรุณาเลือกข้อความและคัดลอกด้วยตัวเอง");
    }
  }
  return (
    <Field>
      <FieldLabel htmlFor={`address-${label}`}>{label}</FieldLabel>
      <div className="flex gap-2">
        <Input id={`address-${label}`} value={value} readOnly className="font-mono" onFocus={event => event.currentTarget.select()} />
        <Button variant="outline" size="icon" className="h-13 w-13 shrink-0 rounded-xl" onClick={copy} aria-label={`คัดลอก${label}`}>
          {copied ? <Check /> : <Copy />}
        </Button>
      </div>
      <p role="status" className="text-xs text-muted-foreground">{message}</p>
    </Field>
  );
}

export function MyMachines() {
  const pageRef = useLandingMotion();
  const [pin, setPin] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  function pair(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!/^\d{4}$/.test(pin)) {
      setError("กรุณากรอก PIN เป็นตัวเลข 4 หลักจากแอป Moonlight");
      return;
    }
    setError("");
    setNotice("นี่คือหน้าตัวอย่าง ยังไม่ได้ส่ง PIN หรือเชื่อมต่อเครื่องจริง");
  }
  return (
    <div ref={pageRef} className="flex min-h-screen flex-col bg-background">
      <Navbar />
      <main className="mx-auto w-full max-w-6xl flex-1 px-5 pt-10 pb-10 sm:px-6 sm:pt-12 sm:pb-14">
        <header data-hero-reveal className="mb-6 flex flex-col justify-between gap-5 sm:mb-8 sm:flex-row sm:items-end">
          <div>
            <p className="mb-3 text-sm font-medium text-primary">YOUR PLAY SPACE</p>
            <h1 className="text-4xl font-bold tracking-tight sm:text-5xl">เครื่อง<span className="text-primary">ของฉัน</span></h1>
            <p className="mt-4 text-sm text-muted-foreground sm:text-base">พื้นที่ของคุณพร้อมแล้ว เลือกวิธีเชื่อมต่อแล้วเริ่มเล่นได้เลย</p>
          </div>
          <Badge variant="secondary" className="w-fit gap-2 px-3 py-1.5"><Info className="size-3.5" />ข้อมูลตัวอย่าง</Badge>
        </header>

        <div className="grid items-start gap-5 lg:grid-cols-[0.85fr_1.15fr] lg:gap-6">
          <section data-hero-reveal aria-label="เครื่องที่เช่า" className="space-y-5">
            <Card className="overflow-hidden rounded-3xl">
              <CardHeader className="gap-5">
                <div className="flex items-center justify-between gap-3">
                  <div className="flex size-14 items-center justify-center rounded-2xl bg-primary/10 text-primary"><Monitor className="size-7" /></div>
                  <Badge variant="available" className="gap-1.5"><span className="size-1.5 rounded-full bg-current" />พร้อมเชื่อมต่อ</Badge>
                </div>
                <CardTitle><h2 className="text-2xl font-bold">{machine.id}</h2></CardTitle>
              </CardHeader>
              <CardContent className="space-y-6">
                <dl className="grid grid-cols-2 gap-x-3 gap-y-5 text-sm">
                  {[
                    { label: "CPU", value: machine.cpu, icon: Cpu },
                    { label: "GPU", value: machine.gpu, icon: MonitorPlay },
                    { label: "หน่วยความจำ", value: `${machine.ram} GB RAM`, icon: MemoryStick },
                    { label: "พื้นที่จัดเก็บ", value: machine.storage, icon: HardDrive },
                  ].map(({ label, value, icon: Icon }) => (
                    <div key={label} className="min-w-0"><dt className="mb-1.5 flex items-center gap-1.5 text-xs text-muted-foreground"><Icon className="size-3.5" />{label}</dt><dd className="font-medium">{value}</dd></div>
                  ))}
                </dl>
                <div className="rounded-2xl bg-primary/5 p-5">
                  <div className="flex items-center gap-2 text-sm text-muted-foreground"><Clock3 className="size-4 text-primary" />เวลาคงเหลือ (ตัวอย่าง)</div>
                  <p className="mt-3 font-mono text-4xl font-semibold tracking-tight tabular-nums">01:24:00</p>
                  <Progress value={70} aria-label="เวลาคงเหลือ 84 นาที จาก 120 นาที" className="mt-4 [&_[data-slot=progress-track]]:h-1.5" />
                  <p className="mt-3 text-xs text-muted-foreground">เหลือ 84 นาที จากเวลาเช่า 2 ชั่วโมง</p>
                </div>
              </CardContent>
            </Card>
            <Button variant="outline" nativeButton={false} render={<Link href="/rent" />} className="w-full rounded-xl">ดูเครื่องอื่นที่พร้อมให้เช่า<ArrowUpRight data-icon="inline-end" /></Button>
          </section>

          <Card data-hero-reveal className="rounded-3xl">
            <CardHeader>
              <CardTitle><h2 className="flex items-center gap-2 text-xl font-bold"><MonitorPlay className="size-5 text-primary" />เชื่อมต่อเครื่อง</h2></CardTitle>
              <p className="text-sm text-muted-foreground">เลือกแอปที่คุณต้องการใช้งานบนอุปกรณ์ของคุณ</p>
            </CardHeader>
            <CardContent>
              <Tabs defaultValue="moonlight" onValueChange={() => { setNotice(""); setError(""); }} className="gap-6">
                <TabsList className="h-11! w-full"><TabsTrigger value="moonlight">Moonlight</TabsTrigger><TabsTrigger value="parsec">Parsec</TabsTrigger></TabsList>
                <TabsContent value="moonlight" className="space-y-6">
                  <ol className="space-y-3 text-sm text-muted-foreground">
                    {["เปิด Moonlight แล้วเพิ่มเครื่องด้วยที่อยู่ด้านล่าง", "เลือกเครื่องในแอป เพื่อรับ PIN 4 หลัก", "กรอก PIN ในหน้านี้เพื่อจับคู่เครื่อง"].map((text, index) => <li key={text} className="flex items-center gap-3"><span className="flex size-6 shrink-0 items-center justify-center rounded-full bg-primary/10 text-xs font-semibold text-primary">{index + 1}</span>{text}</li>)}
                  </ol>
                  <CopyAddress value="192.0.2.10" label="ที่อยู่เครื่อง (ตัวอย่าง)" />
                  <form onSubmit={pair} noValidate className="space-y-4">
                    <Field data-invalid={!!error}>
                      <FieldLabel htmlFor="moonlight-pin">PIN จาก Moonlight</FieldLabel>
                      <Input id="moonlight-pin" value={pin} onChange={event => { setPin(event.target.value); setError(""); setNotice(""); }} inputMode="numeric" autoComplete="off" maxLength={4} placeholder="กรอก PIN 4 หลัก" aria-invalid={!!error} aria-describedby={error ? "pin-error" : undefined} className="font-mono tracking-widest" />
                      {error && <FieldError id="pin-error">{error}</FieldError>}
                    </Field>
                    <Button type="submit" className="h-11 w-full rounded-xl"><ShieldCheck data-icon="inline-start" />จับคู่เครื่อง</Button>
                  </form>
                  <a href="https://moonlight-stream.org/" target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline">ดาวน์โหลด Moonlight<ArrowUpRight className="size-3.5" /><span className="sr-only"> (เปิดในแท็บใหม่)</span></a>
                </TabsContent>
                <TabsContent value="parsec" className="space-y-6">
                  <p className="leading-relaxed text-muted-foreground">เปิด Parsec และเข้าสู่บัญชีของคุณ จากนั้นเลือกชื่อเครื่องที่ได้รับสิทธิ์ในรายการ Computers</p>
                  <CopyAddress value="KamenRaiderPlay-PC01" label="ชื่อเครื่อง (ตัวอย่าง)" />
                  <Button className="h-11 w-full rounded-xl" onClick={() => setNotice("ตัวอย่าง frontend ยังไม่ได้เชื่อมบัญชี Parsec หรือเปิดการเชื่อมต่อจริง")}><MonitorPlay data-icon="inline-start" />เชื่อมต่อผ่าน Parsec</Button>
                  <a href="https://parsec.app/downloads" target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline">ดาวน์โหลด Parsec<ArrowUpRight className="size-3.5" /><span className="sr-only"> (เปิดในแท็บใหม่)</span></a>
                </TabsContent>
              </Tabs>
              {notice && <p role="status" className="mt-5 rounded-xl bg-primary/10 p-4 text-sm leading-relaxed text-primary">{notice}</p>}
              <p className="mt-6 flex items-start gap-2 text-xs leading-relaxed text-muted-foreground"><Info className="mt-0.5 size-3.5 shrink-0" />หน้าตัวอย่างสำหรับแสดง UI ข้อมูลเครื่องและเวลายังไม่เชื่อมกับระบบเช่าจริง</p>
            </CardContent>
          </Card>
        </div>
      </main>
      <Footer />
    </div>
  );
}
