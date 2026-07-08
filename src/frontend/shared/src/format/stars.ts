// Telegram Stars formatting. Amounts are integer Stars, never cents (SPEC §5/§7),
// so we render whole numbers with thousands grouping and no decimals.
const STARS_FORMAT = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });

/**
 * Format an integer Stars amount for display, e.g. `3820` → `"3,820"`.
 * Non-finite input is treated as `0`; fractional input is truncated toward zero.
 * The star glyph/label is left to the widget.
 */
export function formatStars(amount: number): string {
  const safe = Number.isFinite(amount) ? Math.trunc(amount) : 0;
  return STARS_FORMAT.format(safe);
}
