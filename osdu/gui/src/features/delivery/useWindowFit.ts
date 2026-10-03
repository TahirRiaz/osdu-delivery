import { useEffect, type RefObject } from "react";

/**
 * Keeps a scrolling element as tall as the window leaves below where it starts, less `below` (what stays under it: a
 * footer, the page's padding, the workbench's status bar), and never shorter than `min`, so its rows scroll inside it and
 * the page itself does not scroll for them. The height is set on the element, so rows never render at a height the next
 * frame changes.
 *
 * `target` picks the element to size inside the one `ref` holds (a table's own scrolling container); a module-level
 * function keeps the fit from being set up again on every render.
 */
export function useWindowFit(
  ref: RefObject<HTMLElement | null>,
  below: number,
  min: number,
  target?: (element: HTMLElement) => HTMLElement | null,
): void {
  useEffect(() => {
    const element = ref.current;
    if (element === null) {
      return undefined;
    }

    const fit = () => {
      const fitted = target === undefined ? element : target(element);
      if (fitted !== null) {
        const room = window.innerHeight - fitted.getBoundingClientRect().top - below;
        const height = `${Math.max(min, Math.floor(room))}px`;
        if (fitted.style.maxHeight !== height) {
          fitted.style.maxHeight = height;
        }
      }
    };

    // Where the element starts moves when the window does, or when something laid out before it changes height (a strip
    // that wraps, a filter bar that grows): those are the elements before it and before each of its ancestors. None of
    // them is sized by the element, so fitting it never sets the observer off again; the fit waits for the next frame all
    // the same, so a burst of changes is fitted once.
    let frame = 0;
    const schedule = () => {
      window.cancelAnimationFrame(frame);
      frame = window.requestAnimationFrame(fit);
    };

    fit();
    const observer = new ResizeObserver(schedule);
    observer.observe(document.body);
    for (let node: Element | null = element; node !== null && node !== document.body; node = node.parentElement) {
      for (let before = node.previousElementSibling; before !== null; before = before.previousElementSibling) {
        observer.observe(before);
      }
    }

    window.addEventListener("resize", schedule);
    return () => {
      window.cancelAnimationFrame(frame);
      observer.disconnect();
      window.removeEventListener("resize", schedule);
    };
  }, [ref, below, min, target]);
}
