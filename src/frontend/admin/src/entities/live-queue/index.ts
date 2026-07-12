// Public API of the `live-queue` entity: the live now-playing + queue data source
// consumed by the Queue page. Wraps the shared player-events client for the admin.
export {
  useLiveQueue,
  type LiveQueueValue,
  type UseLiveQueueOptions,
} from './useLiveQueue';
