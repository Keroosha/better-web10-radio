import type {
  DonationGoal,
  PlayerState,
  QueueState,
  StreamState,
  SuperChatState,
} from '@web10/shared';

/**
 * The five ways stage state changes, one per SSE event (SPEC §5/§6):
 *   • `snapshot` — full `player.state` (or a poll-fallback `getPlayerState`) → replace all.
 *   • `queue`/`say`/`donation`/`health` — a single branch delta → merge that branch.
 */
export type StageAction =
  | { readonly type: 'snapshot'; readonly state: PlayerState }
  | { readonly type: 'queue'; readonly queue: QueueState }
  | { readonly type: 'say'; readonly superChat: SuperChatState }
  | { readonly type: 'donation'; readonly donationGoal: DonationGoal }
  | { readonly type: 'health'; readonly stream: StreamState };

/** Pure, immutable merge of one delta into the current snapshot. */
export function reducePlayerState(state: PlayerState, action: StageAction): PlayerState {
  switch (action.type) {
    case 'snapshot':
      return action.state;
    case 'queue':
      return { ...state, queue: action.queue };
    case 'say':
      return { ...state, superChat: action.superChat };
    case 'donation':
      return { ...state, donationGoal: action.donationGoal };
    case 'health':
      return { ...state, stream: action.stream };
  }
}
