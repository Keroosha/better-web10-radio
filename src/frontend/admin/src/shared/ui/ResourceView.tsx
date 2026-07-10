import type { ReactElement, ReactNode } from 'react';

import type { ApiResource } from '../lib/useApiResource';

interface ResourceViewProps<T> {
  readonly resource: ApiResource<T>;
  readonly children: (data: T) => ReactNode;
}

/** Renders loading / error / ready states for an {@link ApiResource}. */
export function ResourceView<T>({ resource, children }: ResourceViewProps<T>): ReactElement {
  if (resource.status === 'loading') {
    return <p style={{ opacity: 0.7 }}>Loading…</p>;
  }
  if (resource.status === 'error') {
    return (
      <p role="alert" style={{ color: '#b00020' }}>
        Failed to load: {resource.error.message}
      </p>
    );
  }
  return <>{children(resource.data)}</>;
}
