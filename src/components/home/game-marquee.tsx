"use client";

import Image from "next/image";
import { Pause, Play } from "lucide-react";
import {
  motion,
  useAnimationFrame,
  useMotionValue,
  useReducedMotion,
} from "motion/react";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

const games = [
  {
    name: "Counter-Strike 2",
    image: "counter-strike-2",
    genre: "Tactical FPS",
  },
  { name: "Cyberpunk 2077", image: "cyberpunk-2077", genre: "Open World RPG" },
  { name: "PUBG: BATTLEGROUNDS", image: "pubg", genre: "Battle Royale" },
  { name: "ELDEN RING", image: "elden-ring", genre: "Action RPG" },
  { name: "Apex Legends", image: "apex-legends", genre: "Hero Shooter" },
  { name: "Grand Theft Auto V", image: "gta-v", genre: "Open World" },
  { name: "Dota 2", image: "dota-2", genre: "MOBA" },
  { name: "Rust", image: "rust", genre: "Survival" },
];

// ขอบซ้าย-ขวาจางหาย ให้แถบดูต่อเนื่อง ไม่โดนตัดแข็ง ๆ
const edgeFade = {
  maskImage:
    "linear-gradient(to right, transparent, #000 8%, #000 92%, transparent)",
  WebkitMaskImage:
    "linear-gradient(to right, transparent, #000 8%, #000 92%, transparent)",
};

function Star({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      aria-hidden="true"
      className={className}
      fill="currentColor"
    >
      <path d="M12 0C12.8 7 17 11.2 24 12C17 12.8 12.8 17 12 24C11.2 17 7 12.8 0 12C7 11.2 11.2 7 12 0Z" />
    </svg>
  );
}

function Marquee({
  children,
  reverse = false,
  paused,
  speed = 32,
}: {
  children: ReactNode;
  reverse?: boolean;
  paused: boolean;
  speed?: number;
}) {
  const group = useRef<HTMLDivElement>(null);
  const width = useRef(0);
  const offset = useRef(0);
  const hovered = useRef(false);
  const x = useMotionValue(0);
  const reduceMotion = useReducedMotion();

  useEffect(() => {
    const element = group.current;
    if (!element) return;
    const observer = new ResizeObserver(() => {
      width.current = element.offsetWidth;
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  useAnimationFrame((_, delta) => {
    if (paused || reduceMotion || hovered.current || !width.current) return;
    offset.current =
      (offset.current + (speed * Math.min(delta, 64)) / 1000) % width.current;
    x.set(reverse ? offset.current - width.current : -offset.current);
  });

  return (
    <div
      className="overflow-hidden motion-reduce:overflow-x-auto"
      style={edgeFade}
      onMouseEnter={() => {
        hovered.current = true;
      }}
      onMouseLeave={() => {
        hovered.current = false;
      }}
    >
      <motion.div
        className="flex w-max motion-reduce:transform-none!"
        style={{ x }}
      >
        <div
          ref={group}
          className="flex shrink-0 items-center gap-4 pr-4 sm:gap-5 sm:pr-5"
        >
          {children}
        </div>
        <div
          aria-hidden="true"
          className="flex shrink-0 items-center gap-4 pr-4 motion-reduce:hidden sm:gap-5 sm:pr-5"
        >
          {children}
        </div>
      </motion.div>
    </div>
  );
}

function GameCard({
  game,
  small = false,
}: {
  game: (typeof games)[number];
  small?: boolean;
}) {
  return (
    <figure
      className={cn(
        "group relative shrink-0 overflow-hidden rounded-xl bg-muted ring-1 ring-border",
        small ? "w-[200px] sm:w-[260px]" : "w-[260px] sm:w-[360px]",
      )}
    >
      <div className="aspect-[460/215] overflow-hidden">
        <Image
          src={`/games/${game.image}.jpg`}
          alt=""
          width={460}
          height={215}
          sizes={
            small
              ? "(max-width: 640px) 200px, 260px"
              : "(max-width: 640px) 260px, 360px"
          }
          className="size-full object-cover transition-transform duration-500 group-hover:scale-105 motion-reduce:transition-none"
        />
      </div>
      {/* ไล่เฉดดำด้านล่าง ให้อ่านชื่อเกมได้ชัดบนทุกภาพ */}
      <div className="pointer-events-none absolute inset-x-0 bottom-0 h-2/3 bg-gradient-to-t from-black/80 via-black/30 to-transparent" />
      <figcaption className="absolute inset-x-0 bottom-0 flex flex-col gap-0.5 p-3 text-white sm:p-4">
        <span
          className={cn(
            "truncate font-semibold",
            small ? "text-sm" : "text-sm sm:text-base",
          )}
        >
          {game.name}
        </span>
        <span className="truncate text-xs text-white/70">{game.genre}</span>
      </figcaption>
    </figure>
  );
}

export function GameMarquee() {
  const [paused, setPaused] = useState(false);
  const secondRow = [...games.slice(4), ...games.slice(0, 4)];

  return (
    <section
      aria-labelledby="games-heading"
      className="overflow-hidden py-16 sm:py-24"
    >
      <div data-scroll-reveal className="mx-auto mb-10 flex max-w-6xl items-end justify-between gap-5 px-5 sm:mb-14 sm:px-6">
        <div>
          <h2
            id="games-heading"
            className="text-3xl leading-tight font-bold tracking-tight sm:text-5xl"
          >
            โลกของเกมที่คุณชอบ
          </h2>
          <p className="mt-3 max-w-xl text-sm text-muted-foreground sm:text-base">
            จากเกมแข่งขันสุดเดือด สู่โลกกว้างที่รอให้คุณออกสำรวจ
          </p>
        </div>
        <Button
          variant="outline"
          size="icon"
          className="shrink-0 motion-reduce:hidden"
          onClick={() => setPaused(!paused)}
          aria-label={paused ? "เล่นแถบเกมต่อ" : "หยุดแถบเกม"}
          aria-pressed={paused}
        >
          {paused ? <Play /> : <Pause />}
        </Button>
      </div>

      <div className="-rotate-2 flex flex-col gap-6 py-5 sm:gap-10 sm:py-7">
        <div data-scroll-reveal>
        <Marquee paused={paused}>
          {games.map((game) => (
            <GameCard key={game.image} game={game} />
          ))}
        </Marquee>
        </div>

        {/* แถบชื่อเกม: สลับตัวทึบสี primary กับตัวโปร่งเส้นขอบ + ป้ายเล็กสองบรรทัด + ดาว 4 แฉก */}
        <div data-scroll-reveal>
        <Marquee paused={paused} reverse speed={45}>
          {games.map((game, index) => {
            const solid = index % 2 === 0;
            return (
              <div
                key={game.image}
                className="flex shrink-0 items-center gap-4 sm:gap-6"
              >
                <span
                  className={cn(
                    "text-5xl leading-none font-extrabold tracking-tight whitespace-nowrap sm:text-7xl lg:text-8xl",
                    solid ? "text-primary" : "text-transparent",
                  )}
                  style={
                    solid
                      ? undefined
                      : {
                          WebkitTextStroke:
                            "2px color-mix(in oklab, var(--muted-foreground) 45%, transparent)",
                        }
                  }
                >
                  {game.name}
                </span>
                <span
                  className={cn(
                    "flex flex-col text-xs leading-snug sm:text-base",
                    solid
                      ? "text-muted-foreground"
                      : "text-muted-foreground/60",
                  )}
                >
                  <span>แนวเกม</span>
                  <span>{game.genre}</span>
                </span>
                <Star className="size-7 shrink-0 text-primary sm:size-11" />
              </div>
            );
          })}
        </Marquee>
        </div>

        <div data-scroll-reveal>
        <Marquee paused={paused} reverse speed={28}>
          {secondRow.map((game) => (
            <GameCard key={game.image} game={game} small />
          ))}
        </Marquee>
        </div>
      </div>
    </section>
  );
}
