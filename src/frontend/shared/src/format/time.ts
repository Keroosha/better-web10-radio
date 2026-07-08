// UTC time formatting for API timestamps (ISO-8601 `…Z` strings, SPEC §5).

/** Returned for timestamps that cannot be parsed. */
export const INVALID_TIME_PLACEHOLDER = '—';

function pad2(value: number): string {
  return value.toString().padStart(2, '0');
}

/**
 * Format an ISO-8601 timestamp as `HH:MM` in UTC (24-hour). Invalid or unparseable
 * input yields {@link INVALID_TIME_PLACEHOLDER} rather than throwing, so widgets
 * stay resilient to malformed data.
 */
export function formatUtcTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return INVALID_TIME_PLACEHOLDER;
  return `${pad2(date.getUTCHours())}:${pad2(date.getUTCMinutes())}`;
}
