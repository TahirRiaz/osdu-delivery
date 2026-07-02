// Reads the branding tokens (theme/branding.css) off the document, so the MUI theme and the charts are built
// from the same custom properties the rest of the app's CSS uses: branding.css stays the single source of truth.

const fallbacks: Record<string, string> = {
  // Matches the light block in branding.css, used only before the stylesheet is applied (a frame at boot).
  "--sf-primary": "#3481e5",
  "--sf-secondary": "#5b6371",
};

export function brandToken(name: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value !== "" ? value : fallbacks[name] ?? "#3481e5";
}

/** The chart series palette (the old GUI's Radzen series colors), cycled for any series index. */
export function seriesColor(index: number): string {
  return brandToken(`--sf-series-${(((index % 8) + 8) % 8) + 1}`);
}

/** Applies the mode attribute branding.css keys its dark tokens on. Call before reading tokens for a theme. */
export function applyBrandMode(mode: "light" | "dark"): void {
  document.documentElement.setAttribute("data-sf-theme", mode);
}
