/* eslint-disable react-refresh/only-export-components */
import { useQuery } from '@tanstack/react-query';
import { useApi } from '../app/context';
import type { Branding } from '../api/types';
import { Icon } from '../components';

/**
 * The logo at the top of the menu and on the sign-in screen (owner item F). An uploaded logo is
 * shown as an <img> (never inlined, so an SVG cannot run anything), on a transparent background
 * with no tile behind it, max 40 px high, aspect ratio kept. A dark-theme logo is used in the dark
 * theme when there is one (item I); otherwise the normal one.
 */
export function BrandMark({ branding, subtitle = 'Group workspace' }: { branding?: Branding | null; subtitle?: string }) {
  const api = useApi();
  const logo = branding?.logoUrl ? api.assetUrl(branding.logoUrl) : null;
  const dark = branding?.darkLogoUrl ? api.assetUrl(branding.darkLogoUrl) : logo;
  const compact = branding?.compactLogoUrl ? api.assetUrl(branding.compactLogoUrl) : null;
  if (!logo) {
    return (
      <>
        <span className="brand__mark" aria-hidden="true">
          <Icon name="doc" size={18} />
        </span>
        <span className="stack">
          <span className="brand__name">DAFA Contracts</span>
          <span className="caption">{subtitle}</span>
        </span>
      </>
    );
  }
  return (
    <span className="brand__logo-wrap">
      <img className="brand__logo brand__logo--light" src={logo} alt="DAFA Contracts" />
      <img className="brand__logo brand__logo--dark" src={dark ?? logo} alt="" aria-hidden="true" />
      {compact ? <img className="brand__logo brand__logo--compact" src={compact} alt="" aria-hidden="true" /> : null}
      <span className="caption brand__sub">{subtitle}</span>
    </span>
  );
}

/** Branding for screens outside the session (sign-in): anonymous `GET branding`. */
export function useBranding(): Branding | null {
  const api = useApi();
  const q = useQuery({ queryKey: ['branding'], queryFn: () => api.getBranding(), retry: false, staleTime: 5 * 60_000 });
  return q.data ?? null;
}
