"use client";

import { useRef } from "react";
import { flushSync } from "react-dom";
import { useTheme } from "next-themes";

export function useThemeTransition(duration = 400) {
  const { resolvedTheme, setTheme } = useTheme();
  const switching = useRef(false);

  async function toggleTheme() {
    if (switching.current) return;
    const nextTheme = resolvedTheme === "dark" ? "light" : "dark";
    if (!document.startViewTransition || window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      setTheme(nextTheme);
      return;
    }

    switching.current = true;
    document.documentElement.dataset.themeTransition = "";
    try {
      const transition = document.startViewTransition(() => {
        flushSync(() => setTheme(nextTheme));
      });
      await transition.ready;
      const animation = document.documentElement.animate(
        { opacity: [0, 1] },
        {
          duration: duration * 0.5,
          easing: "ease-in-out",
          pseudoElement: "::view-transition-new(root)",
        },
      );
      await animation.finished;
      await transition.finished;
    } catch {
      setTheme(nextTheme);
    } finally {
      delete document.documentElement.dataset.themeTransition;
      switching.current = false;
    }
  }

  return toggleTheme;
}
