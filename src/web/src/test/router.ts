import type { NavigateFunction } from 'react-router-dom';

/** The router's current location and navigate function, for tests that check the URL or go back. */
export const router: { search: string; pathname: string; navigate: NavigateFunction | null } = { search: '', pathname: '', navigate: null };
export const urlParams = (): URLSearchParams => new URLSearchParams(router.search);
