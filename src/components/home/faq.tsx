import Link from "next/link";
import { Accordion, AccordionContent, AccordionItem, AccordionTrigger } from "@/components/ui/accordion";

const questions = [
  {
    id: "pricing",
    question: "เช่าคอมคิดราคายังไง?",
    answer: "แต่ละเครื่องมีราคาของตัวเอง คุณสามารถดูสเปกและราคาของเครื่องที่สนใจได้ในหน้าเช่า แล้วเลือกเครื่องและระยะเวลาที่เหมาะกับการใช้งานของคุณ",
  },
  {
    id: "device",
    question: "เครื่องของฉันต้องเป็นคอมสเปกสูงไหม?",
    answer: "เกมจะทำงานบนคอมพิวเตอร์ที่คุณเช่า ส่วนอุปกรณ์ของคุณใช้รับภาพและส่งคำสั่งควบคุม จึงควรตรวจสอบว่าอุปกรณ์ใช้งานโปรแกรมเชื่อมต่อได้ และมีอินเทอร์เน็ตที่เสถียร",
  },
  {
    id: "connection",
    question: "ใช้โปรแกรมอะไรเชื่อมต่อเข้าเครื่อง?",
    answer: "ใช้ Moonlight หรือ Parsec ตามช่องทางที่เครื่องนั้นรองรับ โดยตรวจสอบรายละเอียดและทำตามขั้นตอนการเชื่อมต่อที่แสดงในหน้าเช่าของเครื่องที่คุณเลือก",
  },
  {
    id: "available",
    question: "ดูได้ยังไงว่าเครื่องไหนว่าง?",
    answer: "เข้าไปที่หน้าเช่าคอมพิวเตอร์เพื่อดูสถานะของแต่ละเครื่อง พร้อมเปรียบเทียบสเปกและราคา แล้วเลือกเครื่องที่เปิดให้เช่าได้จากหน้านั้น",
  },
  {
    id: "before-end",
    question: "ก่อนหมดเวลาเช่าควรทำอะไร?",
    answer: "บันทึกเกมหรืองานที่กำลังทำ และออกจากบัญชีเกมกับบริการส่วนตัวให้เรียบร้อยก่อนหมดเวลา เพื่อป้องกันข้อมูลสูญหายหรือบัญชีค้างอยู่ในเครื่องที่เช่า",
  },
];

export function Faq() {
  return (
    <section id="faq" aria-labelledby="faq-heading" className="scroll-mt-28 px-5 py-16 sm:px-6 sm:py-24">
      <div className="mx-auto max-w-3xl">
        <div data-scroll-reveal className="mb-10 flex flex-col justify-between gap-5 sm:mb-14 sm:flex-row sm:items-end sm:gap-10">
          <div>
            <p className="mb-4 text-sm font-medium text-primary">คำถามที่พบบ่อย</p>
            <h2 id="faq-heading" className="text-4xl leading-[1.2] font-bold tracking-tight sm:text-6xl">มีข้อสงสัย<br /><span className="text-primary">อะไรไหม?</span></h2>
          </div>
          <p className="max-w-[240px] text-sm leading-7 text-muted-foreground sm:pb-1 sm:text-right sm:text-base">รวมคำถามก่อนเริ่มเช่าคอม<br className="hidden sm:block" /> ให้คุณพร้อมก่อนเริ่มเล่น</p>
        </div>

        <div data-scroll-reveal>
          <Accordion defaultValue={["connection"]}>
            {questions.map(({ id, question, answer }) => (
              <AccordionItem key={id} value={id}>
                <AccordionTrigger>{question}</AccordionTrigger>
                <AccordionContent><p>{answer}</p></AccordionContent>
              </AccordionItem>
            ))}
          </Accordion>
        </div>

        <p data-scroll-reveal className="mt-8 text-center text-sm leading-7 text-muted-foreground sm:mt-10">
          ยังไม่แน่ใจว่าจะเริ่มยังไง?{" "}<Link href="/#how-it-works" className="font-semibold text-primary underline-offset-4 hover:underline focus-visible:rounded-sm focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-ring">ดูวิธีใช้งาน</Link>
        </p>
      </div>
    </section>
  );
}
