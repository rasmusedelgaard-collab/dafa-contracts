import type { CSSProperties } from 'react';

export function Skeleton({ width = '100%', height = 14, radius, style }: { width?: number | string; height?: number | string; radius?: number; style?: CSSProperties }) {
  return <span className="skeleton" aria-hidden="true" style={{ width, height, borderRadius: radius, ...style }} />;
}

/** Placeholder rows for lists and tables while data loads. */
export function SkeletonRows({ rows = 5, label = 'Loading' }: { rows?: number; label?: string }) {
  return (
    <div role="status" aria-label={label}>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="list-row" style={{ borderTop: i === 0 ? 0 : undefined, padding: '14px 18px' }}>
          <Skeleton width={42} height={42} radius={12} />
          <div className="stack" style={{ flex: '1 1 auto', gap: 8 }}>
            <Skeleton width={`${55 - (i % 3) * 9}%`} height={14} />
            <Skeleton width={`${34 + (i % 2) * 8}%`} height={11} />
          </div>
          <Skeleton width={88} height={24} radius={999} />
        </div>
      ))}
    </div>
  );
}
