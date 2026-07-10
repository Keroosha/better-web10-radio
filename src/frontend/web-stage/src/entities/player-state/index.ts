// Public API of the `player-state` entity: the pure state model consumed by the
// stage-state feature and the overlay widgets. No React here.
export { createEmptyPlayerState } from './emptyPlayerState';
export { reducePlayerState, type StageAction } from './reducePlayerState';
export { detectNewDonation } from './detectNewDonation';
export {
  selectApprovedMessages,
  selectDonationPercent,
  selectStreamStatus,
  selectIsLive,
  selectSceneTrack,
} from './selectors';
