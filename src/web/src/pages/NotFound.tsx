import { Link } from 'react-router-dom';
import { Card, Icon } from '../components';

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

/** Shown instead of a page the signed-in user may not open (the API enforces the same rule). */
export function NoAccess({ title, text }: { title: string; text: string }) {
  return (
    <div className="page rise" style={{ paddingTop: 10 }}>
      <Card className="coming" role="alert">
        <span className="tile tile--lg tone-amber" aria-hidden="true">
          <Icon name="alert" />
        </span>
        <h1 className="h2">{title}</h1>
        <p className="secondary" style={{ maxWidth: 560 }}>
          {text}
        </p>
        <Link to="/" className="btn btn--secondary btn--md">
          Go to My work
        </Link>
      </Card>
    </div>
  );
}
