export function Avatar({ initials, size = 'md', solid }: { initials: string; size?: 'sm' | 'md' | 'lg'; solid?: boolean }) {
  const cls = ['avatar', size !== 'md' && `avatar--${size}`, solid && 'avatar--solid'].filter(Boolean).join(' ');
  return (
    <span className={cls} aria-hidden="true">
      {initials}
    </span>
  );
}
