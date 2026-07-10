// Admin endpoints (SPEC §5, `/api/v0/admin/*`). Require admin authentication.
//
// F1 scope is intentionally minimal: only the GET routes whose response reuses an
// already-specified player DTO are implemented here. SPEC §5 lists the admin routes
// but does NOT define their request/response bodies (playlists, storage, say-message
// moderation, stream-node control, and every PUT/POST). Those land in Milestone F4
// once the backend pins the admin contract — do not invent shapes here.
import { z } from 'zod';

import { API_V0_PREFIX, apiFetch, type RequestOptions } from './client';
import {
  DonationGoalSchema,
  SocialLinkSchema,
  type DonationGoal,
  type SocialLink,
} from '../domain/player-state';

const SocialLinkListSchema = z.array(SocialLinkSchema);

/** `GET /api/v0/admin/donation-goal` — current goal, validated. Requires the admin token. */
export function getDonationGoal(opts: RequestOptions = {}): Promise<DonationGoal> {
  return apiFetch(`${API_V0_PREFIX}/admin/donation-goal`, {
    schema: DonationGoalSchema,
    admin: true,
    ...opts,
  });
}

/** `GET /api/v0/admin/social-links` — configured social links, validated. Requires the admin token. */
export function getSocialLinks(opts: RequestOptions = {}): Promise<SocialLink[]> {
  return apiFetch(`${API_V0_PREFIX}/admin/social-links`, {
    schema: SocialLinkListSchema,
    admin: true,
    ...opts,
  });
}
