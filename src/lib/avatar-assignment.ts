import type Database from "better-sqlite3";

export function createAvatarAllocator(database: Database.Database, images: readonly string[]) {
  if (!images.length) throw new Error("Avatar pool must not be empty");
  database.exec(`
    CREATE TABLE IF NOT EXISTS avatarRotation (id INTEGER PRIMARY KEY CHECK (id = 1), nextIndex INTEGER NOT NULL);
    INSERT OR IGNORE INTO avatarRotation (id, nextIndex) VALUES (1, 0);
  `);
  const next = database.prepare("UPDATE avatarRotation SET nextIndex = nextIndex + 1 WHERE id = 1 RETURNING nextIndex - 1 AS position");
  return (image?: string | null) => {
    if (image?.trim()) return image;
    const { position } = next.get() as { position: number };
    return images[position % images.length];
  };
}
