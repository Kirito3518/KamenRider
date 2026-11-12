import { describe, expect, it } from "vitest";

import { recoveryHeadFromConflict } from "@/lib/controller-safe-responses";

describe("controller recovery-head errors", () => {
  it("preserves a validated current chain, head, and generation from a 409", async () => {
    const currentHead = {
      currentChainId: "chain-current",
      currentHeadOperationId: "operation-current",
      currentGeneration: 9,
    };
    const response = Response.json(currentHead, { status: 409 });

    await expect(recoveryHeadFromConflict(response)).resolves.toEqual(currentHead);
  });

  it("does not expose an unrecognized 409 body as current maintenance state", async () => {
    const response = Response.json(
      {
        currentChainId: "chain-current",
        currentHeadOperationId: "operation-current",
        currentGeneration: 9,
        message: "not part of the recovery conflict contract",
      },
      { status: 409 },
    );

    await expect(recoveryHeadFromConflict(response)).resolves.toBeNull();
  });
});
