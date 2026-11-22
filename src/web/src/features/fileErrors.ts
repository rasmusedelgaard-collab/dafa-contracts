// Storage problems on file flows (CONTRACT_V4 §9a): "SharePoint is busy" always comes with a
// Retry button; "not configured" shows the server's sentence, and administrators get a link to
// Settings > Access mapping; a missing file has its own sentence.
import { permissions, useMe } from '../app/context';
import { useToast } from '../components';
import { storageProblem } from '../lib/errors';

export function useFileError() {
  const toast = useToast();
  const me = useMe();
  const admin = permissions(me).canSeeSettings;
  return (error: unknown, fallback: string, retry?: () => void) => {
    const kind = storageProblem(error);
    if (kind === 'unavailable' && retry) return toast.error(error, fallback, { label: 'Retry', onClick: retry });
    if (kind === 'notConfigured' && admin) return toast.error(error, fallback, { label: 'Open Access mapping', href: '/settings/access-mapping' });
    return toast.error(error, fallback);
  };
}
