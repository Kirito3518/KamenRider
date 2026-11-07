import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MachinesManager } from "@/components/admin/machines-manager";

const MACHINES_URL = "/api/admin/machines";
const INSTALL_COMMAND_URL = "/api/admin/agent-install-command";
const ALLOWED_URLS = new Set([MACHINES_URL, INSTALL_COMMAND_URL]);
const TEST_NOW = "2026-11-07T12:00:00.000Z";
const FIRST_COMMAND = "first-test-command";
const FIRST_EXPIRY = "2026-11-07T12:34:56.789Z";
const SECOND_COMMAND = "second-test-command";
const SECOND_EXPIRY = "2026-11-07T13:34:56.789Z";

function jsonResponse(body: unknown) {
  return {
    ok: true,
    json: async () => body,
  } as Response;
}

describe("MachinesManager install command interactions", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  let unexpectedUrls: string[];
  let clipboardWriteText: ReturnType<typeof vi.fn>;
  let clipboardDescriptor: PropertyDescriptor | undefined;

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(TEST_NOW));
    unexpectedUrls = [];
    clipboardDescriptor = Object.getOwnPropertyDescriptor(
      navigator,
      "clipboard",
    );
    clipboardWriteText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText: clipboardWriteText },
    });

    let installResponseIndex = 0;
    const installResponses = [
      {
        available: true,
        command: FIRST_COMMAND,
        expiresAtUtc: FIRST_EXPIRY,
      },
      {
        available: true,
        command: SECOND_COMMAND,
        expiresAtUtc: SECOND_EXPIRY,
      },
    ];
    fetchSpy = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === MACHINES_URL) return jsonResponse([]);
      if (url === INSTALL_COMMAND_URL) {
        const response = installResponses[installResponseIndex];
        installResponseIndex += 1;
        if (!response) {
          unexpectedUrls.push(url);
          throw new Error(`Unexpected extra install-command request: ${url}`);
        }
        return jsonResponse(response);
      }

      unexpectedUrls.push(url);
      throw new Error(`Unexpected fetch URL: ${url}`);
    });
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    if (clipboardDescriptor) {
      Object.defineProperty(navigator, "clipboard", clipboardDescriptor);
    } else {
      Reflect.deleteProperty(navigator, "clipboard");
    }
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("shows the literal UTC expiry and copies the command before its deadline", async () => {
    render(<MachinesManager />);

    const commandField = await screen.findByLabelText("คำสั่งติดตั้ง Agent");
    expect(commandField).toHaveProperty("value", FIRST_COMMAND);
    expect(screen.getByText(`หมดอายุเมื่อ (UTC): ${FIRST_EXPIRY}`)).toBeDefined();

    const copyButton = screen.getByRole("button", {
      name: "คัดลอกคำสั่ง PowerShell",
    });
    expect((copyButton as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(copyButton);

    expect(await screen.findByText("คัดลอกคำสั่งแล้ว")).toBeDefined();
    expect(clipboardWriteText).toHaveBeenCalledWith(FIRST_COMMAND);
    expectNetworkIsolation(1);
  });

  it("disables copy at the exact expiry deadline after focus", async () => {
    render(<MachinesManager />);

    const copyButton = (await screen.findByRole("button", {
      name: "คัดลอกคำสั่ง PowerShell",
    })) as HTMLButtonElement;
    expect(copyButton.disabled).toBe(false);

    act(() => {
      vi.setSystemTime(new Date(FIRST_EXPIRY));
      window.dispatchEvent(new Event("focus"));
    });

    await waitFor(() => expect(copyButton.disabled).toBe(true));
    expect(
      screen.getByText("คำสั่งติดตั้งหมดอายุแล้ว กรุณาขอคำสั่งใหม่"),
    ).toBeDefined();
    expectNetworkIsolation(1);
  });

  it("requests a replacement only on explicit refresh, not focus or visibility", async () => {
    render(<MachinesManager />);

    const commandField = (await screen.findByLabelText(
      "คำสั่งติดตั้ง Agent",
    )) as HTMLTextAreaElement;
    expect(commandField.value).toBe(FIRST_COMMAND);
    expectNetworkIsolation(1);

    act(() => {
      window.dispatchEvent(new Event("focus"));
      document.dispatchEvent(new Event("visibilitychange"));
    });
    expectNetworkIsolation(1);

    fireEvent.click(
      screen.getByRole("button", { name: "ขอคำสั่งติดตั้งใหม่" }),
    );

    await waitFor(() => expect(commandField.value).toBe(SECOND_COMMAND));
    expect(
      screen.getByText(`หมดอายุเมื่อ (UTC): ${SECOND_EXPIRY}`),
    ).toBeDefined();
    expectNetworkIsolation(2);
  });

  function expectNetworkIsolation(expectedInstallRequests: number) {
    const requestedUrls = fetchSpy.mock.calls.map(([input]) => String(input));
    expect(unexpectedUrls).toEqual([]);
    expect(requestedUrls.every((url) => ALLOWED_URLS.has(url))).toBe(true);
    expect(requestedUrls.filter((url) => url === MACHINES_URL)).toHaveLength(1);
    expect(
      requestedUrls.filter((url) => url === INSTALL_COMMAND_URL),
    ).toHaveLength(expectedInstallRequests);
  }
});
