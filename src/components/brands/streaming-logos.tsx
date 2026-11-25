/**
 * Inline, dependency-free brand marks for the streaming clients a renter can use.
 *
 * - `MoonlightMark` is adapted from the official Moonlight-Qt logo
 *   (https://github.com/moonlight-stream/moonlight-qt/blob/master/app/res/moonlight.svg):
 *   the gray outer disc became a currentColor ring and the white inner disc was
 *   dropped so the mark works on any background. The eight-point glyph is kept.
 * - `ParsecMark` is the icon-only crop of the official Parsec logo published as an
 *   inline SVG on https://parsec.app/ (viewBox 0 0 290 441 of the 1824x441 canvas,
 *   wordmark removed) and keeps the two official fills #26ABFF / #F50049.
 *
 * Both components are decorative (`aria-hidden`, no accessible name) unless a
 * `title` is supplied, in which case they expose an accessible image label.
 * See docs/brand-assets.md for sources and the trademark notice.
 */
import type { SVGProps } from "react";

export type BrandMarkProps = {
  className?: string;
  /** Optional accessible name. Omit for decorative use (the default). */
  title?: string;
};

const moonlightGlyph =
  "M 137.66 132 L 224 132 L 224 124 L 137.66 124 L 198.71 62.95 L 193.05 57.29 L 132 118.34 L 132 32 L 124 32 L 124 118.34 L 62.95 57.29 L 57.29 62.95 L 118.34 124 L 32 124 L 32 132 L 118.34 132 L 57.29 193.05 L 62.95 198.71 L 124 137.66 L 124 224 L 132 224 L 132 137.66 L 193.05 198.71 L 198.71 193.05 L 137.66 132 Z";

const parsecBluePath =
  "M53.7 302.68c12.16 7.46 178.56 109.98 178.56 109.98s17.4 13.64 34.94 5.18c17.53-8.46 16.82-25.58 16.82-27.82V163.59s.65-13.59-5.18-25.88c-6.57-13.86-15.5-21.2-25.23-27.17C243.88 104.57 84.76 6.38 84.76 6.38S70.61-4.81 53.71 2.5C36.8 9.81 36.24 26.05 36.24 30.32v244.86c.12 3.69.55 8.59 1.94 11.33 2.42 4.79 3.36 8.7 15.52 16.17zm56.93-192.15l98.98 60.81v137.8l-98.98-60.17V110.53z";

const parsecPinkPath =
  "M0 294.91c.12 3.69.55 8.9 1.94 11.65 2.43 4.79 3.37 8.71 15.53 16.17 12.16 7.46 178.56 109.98 178.56 109.98s17.4 13.64 34.94 5.18c17.53-8.46 16.82-25.58 16.82-27.82V183.64s.65-13.59-5.18-25.88c-6.57-13.86-15.5-21.2-25.23-27.17-9.73-5.97-168.85-104.16-168.85-104.16s-14.15-11.19-31.05-3.88C.56 29.86 0 46.1 0 50.37v244.54zm74.4-164.32l98.98 60.81v137.8L74.4 269.03V130.59z";

function markA11y(title: string | undefined) {
  return title
    ? ({ role: "img" as const } satisfies SVGProps<SVGSVGElement>)
    : ({ "aria-hidden": true, focusable: false } satisfies SVGProps<SVGSVGElement>);
}

export function MoonlightMark({ className, title }: BrandMarkProps) {
  return (
    <svg
      viewBox="0 0 256 256"
      xmlns="http://www.w3.org/2000/svg"
      className={className}
      {...markA11y(title)}
    >
      {title ? <title>{title}</title> : null}
      <circle
        cx="128"
        cy="128"
        r="112"
        fill="none"
        stroke="currentColor"
        strokeWidth="32"
      />
      <path d={moonlightGlyph} fill="currentColor" fillRule="evenodd" />
    </svg>
  );
}

export function ParsecMark({ className, title }: BrandMarkProps) {
  return (
    <svg
      viewBox="0 0 290 441"
      xmlns="http://www.w3.org/2000/svg"
      className={className}
      {...markA11y(title)}
    >
      {title ? <title>{title}</title> : null}
      <path d={parsecBluePath} fill="#26ABFF" />
      <path d={parsecPinkPath} fill="#F50049" />
    </svg>
  );
}
