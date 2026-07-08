// RFC 7807 problem details — the error body shape for every REST error.
//
// SPEC §5: `{ "type": "...", "title": "...", "status": 503, "traceId": "...",
// "code": "stream.unavailable", "message": "Stream is offline" }`.
//
// `type`/`title`/`status` are standard RFC 7807; `traceId`/`code`/`message` are
// the project's required extensions (SPEC §5). Only `status` is guaranteed present
// on any HTTP error; the rest are marked nullable/optional so a partial or
// non-conforming error body still parses instead of throwing over the real error.
import { z } from 'zod';

export const ProblemDetailsSchema = z.object({
  type: z.string().nullish(),
  title: z.string().nullish(),
  status: z.number().int(),
  traceId: z.string().nullish(),
  code: z.string().nullish(),
  message: z.string().nullish(),
});
export type ProblemDetails = z.infer<typeof ProblemDetailsSchema>;
