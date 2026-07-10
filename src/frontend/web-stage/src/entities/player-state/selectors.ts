import type { NowPlaying, PlayerState, StreamStatus, SuperChatMessage } from '@web10/shared';

/**
 * Approved `/say` messages only (SPEC §5/§7 — the stage never shows pending/rejected),
 * capped at the layout's limit. Encodes the approved-only contract in one place.
 */
export function selectApprovedMessages(state: PlayerState, limit: number): SuperChatMessage[] {
  return state.superChat.messages.filter((m) => m.status === 'approved').slice(0, limit);
}

/** Donation progress as a 0–100 percentage, guarding a zero/absent goal. */
export function selectDonationPercent(state: PlayerState): number {
  const { raisedStars, goalStars } = state.donationGoal;
  if (goalStars <= 0) {
    return 0;
  }
  return Math.max(0, Math.min(100, (raisedStars / goalStars) * 100));
}

export function selectStreamStatus(state: PlayerState): StreamStatus {
  return state.stream.status;
}

export function selectIsLive(state: PlayerState): boolean {
  return state.stream.status === 'live';
}

/**
 * The 3D scene's track, or `undefined` when there is no real track (offline / fallback)
 * so `StageScene` renders its neutral placeholder instead of empty strings.
 */
export function selectSceneTrack(nowPlaying: NowPlaying): NowPlaying | undefined {
  const hasTrack = nowPlaying.title.trim() !== '' || nowPlaying.artist.trim() !== '';
  return hasTrack ? nowPlaying : undefined;
}
