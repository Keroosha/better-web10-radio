/**
 * Human-readable message for a rejected promise / thrown value. Uses a generic
 * parameter rather than `unknown` (SPEC §10 bans authored `unknown`), mirroring the
 * `toError` pattern in the shared hooks.
 */
export function errorMessage<TCause>(cause: TCause, fallback: string): string {
  return cause instanceof Error && cause.message.length > 0 ? cause.message : fallback;
}
