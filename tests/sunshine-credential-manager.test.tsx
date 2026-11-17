import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SunshineCredentialManager } from "@/components/admin/sunshine-credential-manager";

const CREDENTIAL_URL = "/api/admin/sunshine-credential";
const INITIAL_STATE = {
  configured: true,
  userName: "sunshine-admin",
  revision: 3,
  updatedAtUtc: "2026-11-17T07:00:00.000Z",
};
const NEW_PASSWORD = "new-s3cret-password";

function jsonResponse(body: unknown, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response;
}

function requestBody(call: unknown[]) {
  return JSON.parse(String((call[1] as RequestInit).body));
}

describe("Sunshine credential Admin form", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  let updatedState: unknown;

  beforeEach(() => {
    updatedState = { ...INITIAL_STATE, userName: "new-admin", revision: 4 };
    fetchSpy = vi.fn(
      async (input: RequestInfo | URL, init?: RequestInit) => {
        if (String(input) !== CREDENTIAL_URL)
          throw new Error(`Unexpected fetch URL: ${String(input)}`);
        if (init?.method === "PUT") return jsonResponse(updatedState);
        return jsonResponse(INITIAL_STATE);
      },
    );
    vi.stubGlobal("fetch", fetchSpy);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  async function loadedForm() {
    render(<SunshineCredentialManager />);
    expect(await screen.findByText("sunshine-admin")).toBeDefined();
    return screen.getByRole("button", { name: "บันทึกบัญชี Sunshine" });
  }

  function fill(values: { userName?: string; password?: string; confirm?: string }) {
    if (values.userName !== undefined)
      fireEvent.change(screen.getByLabelText("ชื่อผู้ใช้ Sunshine"), {
        target: { value: values.userName },
      });
    if (values.password !== undefined)
      fireEvent.change(screen.getByLabelText("รหัสผ่าน Sunshine"), {
        target: { value: values.password },
      });
    if (values.confirm !== undefined)
      fireEvent.change(screen.getByLabelText("ยืนยันรหัสผ่าน Sunshine"), {
        target: { value: values.confirm },
      });
  }

  function submit(button: HTMLElement) {
    fireEvent.submit(button.closest("form")!);
  }

  it("shows the fleet-wide state and its password-handling warnings without a password", async () => {
    await loadedForm();

    expect(screen.getByText("ตั้งค่าแล้ว")).toBeDefined();
    expect(screen.getByText("3")).toBeDefined();
    expect(
      screen.getByText("บัญชีนี้ใช้ร่วมกันทั้งระบบ (fleet-wide) ไม่ใช่รายเครื่อง"),
    ).toBeDefined();
    expect(
      screen.getByText(
        "หน้านี้จะไม่แสดงรหัสผ่านซ้ำหลังบันทึก ควรเก็บรหัสผ่านไว้ในที่ปลอดภัย",
      ),
    ).toBeDefined();
    expect(
      screen.getByText("Controller จะส่งข้อมูลให้เฉพาะ Agent ที่อนุมัติแล้วเท่านั้น"),
    ).toBeDefined();
    expect(
      screen.getByLabelText("รหัสผ่าน Sunshine"),
    ).toHaveProperty("type", "password");
    expect(
      screen.getByLabelText("ยืนยันรหัสผ่าน Sunshine"),
    ).toHaveProperty("type", "password");
  });

  it("validates the username, password length and confirmation before saving", async () => {
    const saveButton = await loadedForm();

    submit(saveButton);
    expect(await screen.findByText("กรอกชื่อผู้ใช้ Sunshine")).toBeDefined();
    expect(screen.getByText("กรอกรหัสผ่าน Sunshine")).toBeDefined();
    expect(screen.getByText("ยืนยันรหัสผ่านอีกครั้ง")).toBeDefined();

    fill({ userName: "sunshine-admin", password: "short", confirm: "short" });
    submit(saveButton);
    expect(
      await screen.findByText("รหัสผ่านต้องมีอย่างน้อย 8 ตัวอักษร"),
    ).toBeDefined();

    fill({
      userName: "sunshine-admin",
      password: NEW_PASSWORD,
      confirm: "different-password",
    });
    submit(saveButton);
    expect(
      await screen.findByText("รหัสผ่านยืนยันไม่ตรงกับรหัสผ่านด้านบน"),
    ).toBeDefined();

    expect(
      fetchSpy.mock.calls.filter(([, init]) => init?.method === "PUT"),
    ).toHaveLength(0);
  });

  it("saves once, clears both password fields, and shows the new revision", async () => {
    const saveButton = await loadedForm();
    fill({
      userName: "  new-admin  ",
      password: NEW_PASSWORD,
      confirm: NEW_PASSWORD,
    });
    submit(saveButton);

    const success = await screen.findByText(
      "บันทึกข้อมูล Sunshine ของทั้งระบบแล้ว — รหัสผ่านจะไม่ถูกแสดงซ้ำ",
    );
    expect(success).toBeDefined();

    const putCalls = fetchSpy.mock.calls.filter(
      ([, init]) => init?.method === "PUT",
    );
    expect(putCalls).toHaveLength(1);
    expect(requestBody(putCalls[0])).toEqual({
      userName: "new-admin",
      password: NEW_PASSWORD,
    });

    await waitFor(() =>
      expect(
        (screen.getByLabelText("รหัสผ่าน Sunshine") as HTMLInputElement).value,
      ).toBe(""),
    );
    expect(
      (screen.getByLabelText("ยืนยันรหัสผ่าน Sunshine") as HTMLInputElement)
        .value,
    ).toBe("");
    expect(screen.getByLabelText("ชื่อผู้ใช้ Sunshine")).toHaveProperty(
      "value",
      "",
    );
    expect(await screen.findByText("new-admin")).toBeDefined();
    expect(screen.queryByDisplayValue(NEW_PASSWORD)).toBeNull();
    expect(document.body.textContent).not.toContain(NEW_PASSWORD);
  });

  it("shows a server error without leaving the password in the page text", async () => {
    fetchSpy.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input) !== CREDENTIAL_URL || init?.method !== "PUT")
        return jsonResponse(INITIAL_STATE);
      return jsonResponse({ error: "Operation rejected" }, 409);
    });
    const saveButton = await loadedForm();
    fill({ userName: "admin2", password: NEW_PASSWORD, confirm: NEW_PASSWORD });
    submit(saveButton);

    expect(
      await screen.findByText(
        "ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรชสถานะแล้วลองใหม่",
      ),
    ).toBeDefined();
    expect(document.body.textContent).not.toContain(NEW_PASSWORD);
  });
});
