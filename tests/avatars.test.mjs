import assert from "node:assert/strict";
import { test } from "node:test";
import Database from "better-sqlite3";

test("avatar allocation cycles, persists, and preserves provider photos", async () => {
  const { createAvatarAllocator } = await import("../src/lib/avatar-assignment.ts");
  const { defaultAvatars, getUserAvatar } = await import("../src/lib/avatars.ts");
  const db = new Database(":memory:");
  const assign = createAvatarAllocator(db, defaultAvatars);
  const provider = "https://cdn.discordapp.com/avatars/123/photo.png";
  assert.equal(assign(provider), provider);
  const cycle = Array.from({ length: defaultAvatars.length }, () => assign(null));
  assert.equal(new Set(cycle).size, defaultAvatars.length);
  assert.deepEqual(cycle, defaultAvatars);
  const recreated = createAvatarAllocator(db, defaultAvatars);
  assert.equal(recreated(null), cycle[0]);
  assert.equal(recreated(""), cycle[1]);
  assert.equal(getUserAvatar({ id: "stable-user", image: provider }), provider);
  assert.equal(getUserAvatar({ id: "stable-user", image: "/avatars/robot-01.svg" }), "/avatars/robot-01.svg");
  assert.equal(getUserAvatar({ id: "stable-user", image: null }), getUserAvatar({ id: "stable-user", image: null }));
  assert.ok(defaultAvatars.includes(getUserAvatar({ id: "stable-user", image: "javascript:alert(1)" })));
  db.close();
});

test("all bundled avatars are present and self-contained SVG images", async () => {
  const { readFile } = await import("node:fs/promises");
  const { defaultAvatars } = await import("../src/lib/avatars.ts");
  for (const path of defaultAvatars) {
    const svg = await readFile(new URL(`../public${path}`, import.meta.url), "utf8");
    assert.match(svg, /<svg/);
    assert.doesNotMatch(svg, /<script|<foreignObject|(?:href|src)\s*=\s*["'](?:https?:|\/\/)/i);
  }
});
