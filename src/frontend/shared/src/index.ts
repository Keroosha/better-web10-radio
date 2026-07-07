// Public entry point for @web10/shared — the ONLY module the app workspaces may
// import from. The package `exports` map in package.json enforces this boundary:
// deep paths (e.g. @web10/shared/src/domain/x) do not resolve.
//
// F0 exposes only a placeholder so the app workspaces can prove the wiring works.
// Domain types/DTOs, the API client, formatters, and design tokens arrive in F1+.
export const SHARED_PACKAGE = '@web10/shared' as const;
