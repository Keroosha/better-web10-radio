// Test seams for driving the shared SSE client without a real EventSource (jsdom has
// none). `makeFakeConnector` captures the `SseConnectorSpec` the client wires up and
// lets a test emit named events or force disconnects.
import type {
  FetchImpl,
  PlayerEventName,
  PlayerState,
  SseConnection,
  SseConnector,
  SseConnectorSpec,
} from '@web10/shared';

export interface FakeConnector {
  /** Pass to `useStageState`/`createPlayerEventsClient` as the transport factory. */
  readonly connector: SseConnector;
  /** Deliver a named event with a JSON-serialisable payload. */
  emit<TPayload>(name: PlayerEventName, payload: TPayload): void;
  /** Simulate a transport disconnect. */
  fail(): void;
  isClosed(): boolean;
  isConnected(): boolean;
}

export function makeFakeConnector(): FakeConnector {
  let spec: SseConnectorSpec | null = null;
  let closed = false;

  const connector: SseConnector = (openedSpec) => {
    spec = openedSpec;
    const connection: SseConnection = {
      close: () => {
        closed = true;
      },
    };
    return connection;
  };

  return {
    connector,
    emit(name, payload) {
      if (spec === null) {
        throw new Error('fake connector was never opened');
      }
      spec.onMessage(name, JSON.stringify(payload));
    },
    fail() {
      if (spec === null) {
        throw new Error('fake connector was never opened');
      }
      spec.onError();
    },
    isClosed: () => closed,
    isConnected: () => spec !== null,
  };
}

/** A `FetchImpl` that always returns the given player snapshot (for seed + poll). */
export function fakeStateFetch(state: PlayerState): FetchImpl {
  return () =>
    Promise.resolve(
      new Response(JSON.stringify(state), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
}
