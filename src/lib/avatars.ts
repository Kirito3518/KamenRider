// Fixed shuffled pool: assignment is persisted, and the pool repeats after 12 users.
export const defaultAvatars = [7, 2, 11, 4, 9, 1, 12, 5, 3, 10, 6, 8].map(
  number => `/avatars/robot-${String(number).padStart(2, "0")}.svg`,
);

export function getUserAvatar(user: { id: string; image?: string | null }) {
  if (user.image && (defaultAvatars.includes(user.image) || /^https:\/\//i.test(user.image))) return user.image;
  // Stable fallback for old sessions and failed provider images.
  let hash = 0;
  for (const character of user.id) hash = (Math.imul(hash, 31) + character.charCodeAt(0)) >>> 0;
  return defaultAvatars[hash % defaultAvatars.length];
}
