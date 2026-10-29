export function authError(error: { code?: string; message?: string } | null) {
  switch (error?.code) {
    case "INVALID_USERNAME_OR_PASSWORD":
    case "INVALID_EMAIL_OR_PASSWORD": return "ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง";
    case "USER_ALREADY_EXISTS":
    case "USER_ALREADY_EXISTS_USE_ANOTHER_EMAIL": return "อีเมลนี้ถูกใช้งานแล้ว กรุณาเข้าสู่ระบบ";
    case "USERNAME_IS_ALREADY_TAKEN": return "ชื่อผู้ใช้นี้ถูกใช้งานแล้ว";
    case "USERNAME_IS_INVALID":
    case "INVALID_USERNAME": return "ชื่อผู้ใช้ต้องเป็นตัวอักษรอังกฤษ ตัวเลข หรือขีดล่าง";
    case "USERNAME_TOO_SHORT":
    case "USERNAME_TOO_LONG": return "ชื่อผู้ใช้ต้องมีความยาว 3–30 ตัวอักษร";
    case "PASSWORD_TOO_SHORT":
    case "PASSWORD_TOO_LONG": return "รหัสผ่านต้องมีความยาว 8–128 ตัวอักษร";
    case "FAILED_TO_UNLINK_LAST_ACCOUNT": return "ไม่สามารถถอดช่องทางเข้าสู่ระบบสุดท้ายได้";
    case "SESSION_NOT_FRESH": return "กรุณาออกจากระบบแล้วเข้าสู่ระบบอีกครั้ง ก่อนถอดการเชื่อมต่อบัญชี";
    case "SESSION_EXPIRED":
    case "UNAUTHORIZED": return "กรุณาเข้าสู่ระบบอีกครั้ง";
    case "TOO_MANY_REQUESTS": return "ทำรายการบ่อยเกินไป กรุณารอสักครู่";
    default: return "ทำรายการไม่สำเร็จ กรุณาลองใหม่อีกครั้ง";
  }
}
