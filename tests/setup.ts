import { tmpdir } from "node:os";
import { join } from "node:path";

// Deterministic test-only encryption key (32 bytes base64) and slip directory.
// Production resolves its own key from env/sidecar files.
process.env.TOPUP_SETTINGS_KEY ??= Buffer.alloc(32, 7).toString("base64");
process.env.TOPUP_SLIP_DIR ??= join(tmpdir(), "kamenraiderplay-topup-slips");

type ObserverCallback = (entries: unknown[], observer: unknown) => void;

class TestResizeObserver {
  private readonly callback: ObserverCallback;
  constructor(callback: ObserverCallback) {
    this.callback = callback;
  }
  observe() {}
  unobserve() {}
  disconnect() {}
}

class TestIntersectionObserver {
  private readonly callback: ObserverCallback;
  constructor(callback: ObserverCallback) {
    this.callback = callback;
  }
  observe() {}
  unobserve() {}
  disconnect() {}
  takeRecords() {
    return [];
  }
}

if (typeof globalThis.ResizeObserver === "undefined") {
  globalThis.ResizeObserver =
    TestResizeObserver as unknown as typeof ResizeObserver;
}
if (typeof globalThis.IntersectionObserver === "undefined") {
  globalThis.IntersectionObserver =
    TestIntersectionObserver as unknown as typeof IntersectionObserver;
}

if (typeof window !== "undefined" && typeof window.matchMedia !== "function") {
  window.matchMedia = ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  })) as typeof window.matchMedia;
}
