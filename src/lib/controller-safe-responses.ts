export type SafeRecoveryHead = {
  currentChainId: string;
  currentHeadOperationId: string;
  currentGeneration: number;
};

export async function recoveryHeadFromConflict(
  response: Response,
): Promise<SafeRecoveryHead | null> {
  if (response.status !== 409) return null;
  try {
    const body: unknown = await response.clone().json();
    if (typeof body !== "object" || body === null || Array.isArray(body))
      return null;
    const result = body as Record<string, unknown>;
    const keys = Object.keys(result).sort();
    if (
      keys.length !== 3 ||
      keys[0] !== "currentChainId" ||
      keys[1] !== "currentGeneration" ||
      keys[2] !== "currentHeadOperationId" ||
      typeof result.currentChainId !== "string" ||
      !result.currentChainId.trim() ||
      result.currentChainId.length > 128 ||
      /[\u0000-\u001f\u007f]/.test(result.currentChainId) ||
      typeof result.currentHeadOperationId !== "string" ||
      !result.currentHeadOperationId.trim() ||
      result.currentHeadOperationId.length > 128 ||
      /[\u0000-\u001f\u007f]/.test(result.currentHeadOperationId) ||
      !Number.isSafeInteger(result.currentGeneration) ||
      Number(result.currentGeneration) <= 0
    )
      return null;
    return {
      currentChainId: result.currentChainId,
      currentHeadOperationId: result.currentHeadOperationId,
      currentGeneration: Number(result.currentGeneration),
    };
  } catch {
    return null;
  }
}
