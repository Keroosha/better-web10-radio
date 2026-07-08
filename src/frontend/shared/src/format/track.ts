// Track duration / progress / label formatting for the NOW PLAYING widget.

/** Em-dash separator used in `artist — title` labels (SPEC §... `/song` fallback). */
const LABEL_SEPARATOR = ' — ';

function pad2(value: number): string {
  return value.toString().padStart(2, '0');
}

/**
 * Format a millisecond duration as `M:SS`, or `H:MM:SS` once it reaches an hour.
 * Non-finite or negative input yields `"0:00"`.
 */
export function formatDuration(ms: number): string {
  const totalSeconds = Number.isFinite(ms) && ms > 0 ? Math.floor(ms / 1000) : 0;
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (hours > 0) {
    return `${hours}:${pad2(minutes)}:${pad2(seconds)}`;
  }
  return `${minutes}:${pad2(seconds)}`;
}

/**
 * Playback progress as a fraction in `[0, 1]`, clamped. Returns `0` when the
 * duration is unknown or non-positive (avoids divide-by-zero / `NaN` in the UI).
 */
export function formatProgress(positionMs: number, durationMs: number): number {
  if (!Number.isFinite(durationMs) || durationMs <= 0) return 0;
  if (!Number.isFinite(positionMs) || positionMs <= 0) return 0;
  return Math.min(positionMs / durationMs, 1);
}

/**
 * Build the display label for a track. Prefers `artist — title`; degrades to
 * whichever part is present, and returns `""` when neither is. Inputs are trimmed.
 */
export function formatTrackLabel(artist: string, title: string): string {
  const a = artist.trim();
  const t = title.trim();
  if (a && t) return `${a}${LABEL_SEPARATOR}${t}`;
  return a || t;
}
