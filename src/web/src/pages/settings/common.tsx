import type { ReactNode } from 'react';
import { Button, Modal } from '../../components';

export function SectionHead({ title, text, action }: { title: string; text: string; action?: ReactNode }) {
  return (
    <div className="settings-head">
      <div className="page-head__text">
        <h2 className="h2">{title}</h2>
        <p className="secondary settings-head__text">{text}</p>
      </div>
      {action ? <div className="settings-head__action">{action}</div> : null}
    </div>
  );
}

export function ConfirmDelete({ title, text, pending, onConfirm, onClose }: { title: string; text: ReactNode; pending: boolean; onConfirm: () => void; onClose: () => void }) {
  return (
    <Modal
      title={title}
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} data-autofocus="">
            Cancel
          </Button>
          <Button variant="danger" loading={pending} onClick={onConfirm}>
            Delete
          </Button>
        </>
      }
    >
      <p className="secondary">{text}</p>
    </Modal>
  );
}
