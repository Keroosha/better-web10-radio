import { useEffect, useState } from 'react';

/**
 * Rotating index over the social links for the FOLLOW US "featured" slot. Advances on
 * an interval; resets and idles when there are 0 or 1 links. The returned index is
 * clamped to `count` so it stays valid if the list shrinks between renders.
 */
export function useSocialRotation(count: number, intervalMs = 4200): number {
  const [index, setIndex] = useState(0);

  useEffect(() => {
    if (count <= 1) {
      setIndex(0);
      return;
    }
    const id = setInterval(() => {
      setIndex((current) => (current + 1) % count);
    }, intervalMs);
    return () => {
      clearInterval(id);
    };
  }, [count, intervalMs]);

  return count > 0 ? index % count : 0;
}
