import { useEffect } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { router } from './router';

/** Rendered next to the app in tests: keeps `router` up to date with the current location. */
export function RouterProbe() {
  const location = useLocation();
  const navigate = useNavigate();
  useEffect(() => {
    router.search = location.search;
    router.pathname = location.pathname;
    router.navigate = navigate;
  }, [location, navigate]);
  return null;
}
