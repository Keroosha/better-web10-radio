import type { ReactElement, ReactNode } from 'react';

import type { ApiResource } from '../lib/useApiResource';

interface ResourceViewProps<T> {
  readonly resource: ApiResource<T>;
  readonly children: (data: T) => ReactNode;
}

/** Renders loading / error / ready states for an {@link ApiResource}. */
export function ResourceView<T>({ resource, children }: ResourceViewProps<T>): ReactElement {
  if (resource.status === 'loading') {
    return <p className="admin-muted">Loading…</p>;
  }
  if (resource.status === 'error') {
    return (
      <p role="alert" className="admin-error">
        Failed to load: {resource.error.message}
      </p>
    );
  }
  return <>{children(resource.data)}</>;
}
