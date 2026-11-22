import { useEffect, useId, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from 'react-router-dom';
import { qk, useApi } from '../app/context';
import type { AppNotification } from '../api/types';
import { Icon, SkeletonRows } from '../components';
import { formatDateTime } from '../lib/dates';

const EVENT_ICON: Record<string, 'chat' | 'eye' | 'calendar' | 'check' | 'alert' | 'bell' | 'upload'> = {
  Mention: 'chat',
  WatchedContractChanged: 'eye',
  NoticeReminder: 'calendar',
  Escalation: 'alert',
  ObligationDue: 'calendar',
  AnnualReview: 'calendar',
  ApprovalRequested: 'check',
  ApprovalDecided: 'check',
  ApprovalDelegated: 'check',
  RenewalDecision: 'calendar',
  DataCompletionRequested: 'alert',
  ImportFinished: 'upload',
  SyncFailed: 'alert',
};

/** Bell in the top bar with the unread count, and the in-app notification centre. */
export function NotificationBell() {
  const api = useApi();
  const client = useQueryClient();
  const navigate = useNavigate();
  const panelId = useId();
  const [open, setOpen] = useState(false);
  const [unreadOnly, setUnreadOnly] = useState(false);
  const boxRef = useRef<HTMLDivElement>(null);
  const count = useQuery({ queryKey: qk.unread, queryFn: () => api.getUnreadCount(), refetchInterval: 60_000, retry: false });
  const list = useQuery({ queryKey: [...qk.notifications, 'list', unreadOnly], queryFn: () => api.getNotifications({ unreadOnly, pageSize: 30 }), enabled: open });
  const refresh = () => client.invalidateQueries({ queryKey: qk.notifications });
  const read = useMutation({ mutationFn: (id: string) => api.markNotificationRead(id), onSettled: refresh });
  const readAll = useMutation({ mutationFn: () => api.markAllNotificationsRead(), onSettled: refresh });
  const unread = count.data?.count ?? 0;

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!boxRef.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const pick = (n: AppNotification) => {
    if (!n.readAt) read.mutate(n.id);
    setOpen(false);
    if (n.link) navigate(n.link);
  };

  return (
    <div className="bell" ref={boxRef}>
      <button type="button" className="bell__btn" aria-expanded={open} aria-controls={panelId} aria-label={unread ? `Notifications, ${unread} unread` : 'Notifications'} onClick={() => setOpen((o) => !o)}>
        <Icon name="bell" size={20} stroke={1.9} />
        {unread ? <span className="bell__badge" aria-hidden="true">{unread > 99 ? '99+' : unread}</span> : null}
      </button>
      {open ? (
        <div className="bell__panel" id={panelId} role="dialog" aria-label="Notifications">
          <div className="bell__head">
            <h2 className="card-title">Notifications</h2>
            <div className="spacer" />
            <label className="check" style={{ fontSize: 13 }}>
              <input type="checkbox" checked={unreadOnly} onChange={(e) => setUnreadOnly(e.target.checked)} />
              Unread only
            </label>
            <button type="button" className="link-btn" disabled={!unread} onClick={() => readAll.mutate()}>
              Mark all as read
            </button>
          </div>
          {list.isLoading ? (
            <SkeletonRows rows={4} label="Loading notifications" />
          ) : (list.data?.items.length ?? 0) === 0 ? (
            <p className="secondary bell__empty">{unreadOnly ? 'Nothing unread.' : 'No notifications yet. Mentions, approvals, reminders and changes on contracts you watch show up here.'}</p>
          ) : (
            <ul className="bell__list">
              {list.data?.items.map((n) => (
                <li key={n.id}>
                  <button type="button" className={`bell__item${n.readAt ? '' : ' bell__item--unread'}`} onClick={() => pick(n)}>
                    <span className="tile" aria-hidden="true">
                      <Icon name={EVENT_ICON[n.event] ?? 'bell'} size={16} />
                    </span>
                    <span className="kv" style={{ minWidth: 0 }}>
                      <span className="bell__title">{n.title}</span>
                      <span className="bell__body">{n.body}</span>
                      <span className="caption">{formatDateTime(n.createdAt)}</span>
                    </span>
                    {n.readAt ? null : <span className="sr-only">Unread</span>}
                  </button>
                </li>
              ))}
            </ul>
          )}
          <div className="bell__foot">
            <Link to="/preferences" onClick={() => setOpen(false)}>
              Notification settings
            </Link>
          </div>
        </div>
      ) : null}
    </div>
  );
}
