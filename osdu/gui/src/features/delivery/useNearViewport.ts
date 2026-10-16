import { useEffect, useState } from "react";

/**
 * Whether an element has come within `margin` of the viewport, for a long page of sections that each fetch their own
 * rows: a section asks for them as the reader scrolls towards it, rather than every section at once when the page opens.
 * Once near it stays near, so scrolling past a section never discards what it fetched. A callback ref rather than a
 * `useRef`, so the observer follows the node across a remount.
 */
export function useNearViewport<T extends Element>(margin = "400px"): [(node: T | null) => void, boolean] {
  const [node, setNode] = useState<T | null>(null);
  const [near, setNear] = useState(false);

  useEffect(() => {
    if (node === null || near) {
      return;
    }

    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          setNear(true);
        }
      },
      { rootMargin: margin },
    );
    observer.observe(node);
    return () => observer.disconnect();
  }, [node, near, margin]);

  return [setNode, near];
}
