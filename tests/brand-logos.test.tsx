import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import {
  MoonlightMark,
  ParsecMark,
} from "@/components/brands/streaming-logos";
import { Button, buttonVariants } from "@/components/ui/button";

afterEach(cleanup);

describe("streaming brand marks", () => {
  it("renders the Moonlight mark decoratively by default", () => {
    const { container } = render(<MoonlightMark className="size-6" />);
    const svg = container.querySelector("svg");
    expect(svg).not.toBeNull();
    expect(svg?.getAttribute("aria-hidden")).toBe("true");
    expect(svg?.querySelector("title")).toBeNull();
    expect(svg?.getAttribute("class")).toContain("size-6");
    // The adapted mark draws with currentColor so it inherits button text tones.
    expect(svg?.querySelector("path")?.getAttribute("fill")).toBe("currentColor");
    expect(svg?.querySelector("circle")?.getAttribute("stroke")).toBe(
      "currentColor",
    );
  });

  it("exposes an accessible name for the Moonlight mark when asked", () => {
    const { container } = render(<MoonlightMark title="Moonlight" />);
    const svg = container.querySelector("svg");
    expect(svg?.getAttribute("role")).toBe("img");
    expect(svg?.getAttribute("aria-hidden")).toBeNull();
    expect(svg?.querySelector("title")?.textContent).toBe("Moonlight");
    expect(
      screen.getByRole("img", { name: "Moonlight" }),
    ).toBe(svg);
  });

  it("renders the Parsec mark with the official blue and pink fills", () => {
    const { container } = render(<ParsecMark className="h-6 w-auto" />);
    const svg = container.querySelector("svg");
    expect(svg).not.toBeNull();
    expect(svg?.getAttribute("viewBox")).toBe("0 0 290 441");
    expect(svg?.getAttribute("aria-hidden")).toBe("true");
    const fills = Array.from(svg?.querySelectorAll("path") ?? []).map((path) =>
      path.getAttribute("fill"),
    );
    expect(fills).toEqual(["#26ABFF", "#F50049"]);
  });

  it("exposes an accessible name for the Parsec mark when asked", () => {
    const { container } = render(<ParsecMark title="Parsec" />);
    const svg = container.querySelector("svg");
    expect(svg?.getAttribute("role")).toBe("img");
    expect(svg?.querySelector("title")?.textContent).toBe("Parsec");
    expect(screen.getByRole("img", { name: "Parsec" })).toBe(svg);
  });
});

describe("brand theming", () => {
  it("declares the Parsec tokens in the global theme", () => {
    const css = readFileSync(
      resolve(process.cwd(), "src/app/globals.css"),
      "utf8",
    );
    expect(css).toContain("--parsec-blue: #26abff;");
    expect(css).toContain("--parsec-pink: #f50049;");
    expect(css).toContain("--color-parsec-blue: var(--parsec-blue);");
    expect(css).toContain("--color-parsec-pink: var(--parsec-pink);");
    // The tokens are declared for both light (:root) and dark theme scopes.
    expect(css.match(/--parsec-blue: #26abff;/g)).toHaveLength(2);
    expect(css.match(/--parsec-pink: #f50049;/g)).toHaveLength(2);
  });

  it("builds the semantic button variants used by the connect actions", () => {
    const parsec = buttonVariants({ variant: "parsec" });
    expect(parsec).toContain("var(--parsec-blue)");
    expect(parsec).toContain("var(--parsec-pink)");
    expect(parsec).toContain("text-white");
    expect(parsec).toContain("hover:brightness-110");
    expect(parsec).toContain("focus-visible:ring-parsec-pink/40");

    expect(buttonVariants({ variant: "success" })).toContain(
      "status-available",
    );
    expect(buttonVariants({ variant: "destructive" })).toContain(
      "text-destructive",
    );
    // Keep the existing semantics for the other rental actions.
    expect(buttonVariants({ variant: "default" })).toContain("bg-primary");
    expect(buttonVariants({ variant: "outline" })).toContain("border-border");
  });

  it("renders a parsec button with the gradient brand style", () => {
    render(<Button variant="parsec">Parsec</Button>);
    const button = screen.getByRole("button", { name: "Parsec" });
    expect(button.className).toContain("linear-gradient");
  });
});
