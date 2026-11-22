import { useLookups, useScope } from '../app/context';
import { Icon } from '../components';

/**
 * The top-bar entity scope as a removable chip, on pages that have no entity filter of their own
 * (Home, Renewals, Approvals). Nothing when no scope is set.
 */
export function ScopeChip() {
  const scope = useScope();
  const lookups = useLookups();
  if (scope.entityIds.length === 0) return null;
  const names = scope.entityIds.map((id) => lookups.entities.find((e) => e.id === id)?.name ?? 'Unknown entity');
  const label = names.length === 1 ? names[0] : `${names.length} legal entities`;
  return (
    <nav className="crumbs scope-chip" aria-label="Entity scope">
      <span className="caption">Showing</span>
      <ol>
        <li>
          <span className="crumb" title={names.join(', ')}>
            {label}
            <button type="button" aria-label={`Remove entity scope ${label}`} onClick={() => scope.setEntityId('')}>
              <Icon name="x" size={12} stroke={2.6} />
            </button>
          </span>
        </li>
      </ol>
    </nav>
  );
}
