"use client";

import { useLayoutEffect, useRef } from "react";
import Lenis from "lenis";
import { gsap } from "gsap";
import { ScrollTrigger } from "gsap/ScrollTrigger";

export function useLandingMotion() {
  const scope = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    const root = scope.current;
    if (!root) return;
    gsap.registerPlugin(ScrollTrigger);
    const media = gsap.matchMedia();

    media.add("(prefers-reduced-motion: no-preference)", () => {
      const lenis = new Lenis({
        autoRaf: false,
        duration: 1.05,
        smoothWheel: true,
        anchors: true,
      });
      const tick = (time: number) => lenis.raf(time * 1000);
      const unsubscribe = lenis.on("scroll", ScrollTrigger.update);
      gsap.ticker.add(tick);

      gsap.from(root.querySelectorAll("[data-hero-reveal]"), {
        autoAlpha: 0,
        y: 24,
        duration: 0.8,
        stagger: 0.1,
        ease: "power3.out",
        clearProps: "opacity,visibility,transform",
      });

      root.querySelectorAll<HTMLElement>("[data-scroll-reveal]").forEach((element) => {
        gsap.from(element, {
          autoAlpha: 0,
          y: 32,
          duration: 0.75,
          ease: "power3.out",
          clearProps: "opacity,visibility,transform",
          scrollTrigger: {
            trigger: element,
            start: "clamp(top bottom-=48)",
            once: true,
          },
        });
      });

      let active = true;
      let refreshFrame = 0;
      const observer = new ResizeObserver(() => {
        cancelAnimationFrame(refreshFrame);
        refreshFrame = requestAnimationFrame(() => {
          lenis.resize();
          ScrollTrigger.refresh();
        });
      });
      observer.observe(root);
      ScrollTrigger.refresh();
      void document.fonts.ready.then(() => {
        if (active) ScrollTrigger.refresh();
      });

      return () => {
        active = false;
        observer.disconnect();
        cancelAnimationFrame(refreshFrame);
        gsap.ticker.remove(tick);
        unsubscribe();
        lenis.destroy();
      };
    }, scope);

    return () => media.revert();
  }, []);

  return scope;
}
