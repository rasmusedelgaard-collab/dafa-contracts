import { Link } from 'react-router-dom';
import { ButtonLink, Card, Icon, Pill, type IconName } from '../components';

/** Routed stand-in for a screen that is built in the next phase. */
export function Placeholder({ title, sub, icon, points }: { title: string; sub: string; icon: IconName; points: string[] }) {
  return (
    <div className="page rise" style={{ paddingTop: 10 }}>
      <div className="page-head">
        <div className="page-head__text">
          <h1 className="h1">{title}</h1>
          <span className="page-sub">{sub}</span>
        </div>
      </div>
      <Card className="coming">
        <span className="tile tile--lg" aria-hidden="true">
          <Icon name={icon} />
        </span>
        <Pill tone="amber">Coming next</Pill>
        <h2 className="h2">This screen is not built yet</h2>
        <p className="secondary" style={{ maxWidth: 560 }}>
          It is part of the next build phase. The data behind it is already available from the API, so it will show:
        </p>
        <ul className="stack" style={{ gap: 6, listStyle: 'disc', paddingLeft: 20, fontSize: 14 }}>
          {points.map((p) => (
            <li key={p}>{p}</li>
          ))}
        </ul>
        <ButtonLink to="/contracts" variant="secondary" size="md" style={{ marginTop: 8 }}>
          Go to contracts
        </ButtonLink>
      </Card>
    </div>
  );
}

export const ApprovalsPage = () => (
  <Placeholder
    title="Approvals"
    sub="Contracts waiting for your decision"
    icon="check"
    points={['Your queue of contracts to approve, longest waiting first', 'Key terms and what differs from the DAFA standard', 'The approval route, with approve, reject and delegate']}
  />
);
export const RenewalsPage = () => (
  <Placeholder
    title="Renewals"
    sub="Notice deadlines for the next 12 months, by legal entity"
    icon="calendar"
    points={['A 12-month timeline with one lane per legal entity', 'Markers colored by how close the notice deadline is', 'The selected contract with a link to open it']}
  />
);
export const OverviewPage = () => (
  <Placeholder
    title="Overview"
    sub="The contract portfolio across the entities you manage"
    icon="chart"
    points={['Active contracts, annual value, deadlines within 90 days', 'Value by legal entity and contracts by type', 'Change-of-control clauses and contracts expiring without renewal']}
  />
);
export const SettingsPage = () => (
  <Placeholder
    title="Settings"
    sub="Administration"
    icon="gear"
    points={['Legal entities, departments and contract types', 'Approval rules, reminders and retention', 'Templates, audit log, outbox and holiday cover']}
  />
);

export function NotFound() {
  return (
    <div className="page rise" style={{ paddingTop: 10 }}>
      <Card className="coming">
        <h1 className="h2">Page not found</h1>
        <p className="secondary">The address does not match a page in DAFA Contracts.</p>
        <Link to="/" className="btn btn--md">
          Go to My work
        </Link>
      </Card>
    </div>
  );
}
