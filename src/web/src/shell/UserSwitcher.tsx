import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from 'react-router-dom';
import { permissions, qk, useApi, useMe } from '../app/context';
import { readAccessCode } from '../app/signin';
import { Avatar, Button, ErrorState, Modal, SkeletonRows, useToast } from '../components';

/**
 * Dev-mode user switcher (from `dev/users`): pick a demo user to test the access model.
 * In Entra mode it only shows who is signed in and the sign-out button.
 */
export function UserSwitcher({ onClose }: { onClose: () => void }) {
  const api = useApi();
  const me = useMe();
  const client = useQueryClient();
  const navigate = useNavigate();
  const toast = useToast();
  const isDev = me.authMode === 'Dev';
  const perms = permissions(me);

  const users = useQuery({ queryKey: qk.devUsers, queryFn: () => api.getDevUsers(readAccessCode()), enabled: isDev });

  const reset = async () => {
    onClose();
    navigate('/');
    client.clear();
    await client.invalidateQueries();
  };

  const login = useMutation({
    mutationFn: (userId: string) => api.devLogin(userId, readAccessCode()),
    onSuccess: async (user) => {
      await reset();
      toast.show(`You are now ${user.name}`);
    },
    onError: (e) => toast.error(e),
  });
  const logout = useMutation({ mutationFn: () => api.logout(), onSuccess: reset, onError: (e) => toast.error(e) });

  return (
    <Modal
      title={isDev ? 'Switch demo user' : me.name}
      subtitle={isDev ? 'Dev mode. Each demo user has different access, so you can check who sees what.' : me.email}
      onClose={onClose}
      footer={
        <Button variant="secondary" size="md" icon="logout" loading={logout.isPending} onClick={() => logout.mutate()}>
          Sign out
        </Button>
      }
    >
      {isDev ? (
        users.isLoading ? (
          <SkeletonRows rows={4} label="Loading demo users" />
        ) : users.isError ? (
          <ErrorState text="The demo users could not be loaded." onRetry={() => void users.refetch()} />
        ) : (
          <div className="user-list">
            {(users.data ?? []).map((u) => (
              <button key={u.id} type="button" className="user-opt" aria-current={u.id === me.id ? 'true' : undefined} disabled={login.isPending} data-autofocus={u.id === me.id ? '' : undefined} onClick={() => (u.id === me.id ? onClose() : login.mutate(u.id))}>
                <Avatar initials={u.initials} solid={u.id === me.id} />
                <span className="kv">
                  <span className="user-opt__name">{u.name}</span>
                  <span className="kv__k">{u.title}</span>
                </span>
              </button>
            ))}
          </div>
        )
      ) : null}
      {perms.canSeeOverview || perms.canSeeSettings ? (
        <div className="row-flex" aria-label="Manage">
          {perms.canSeeOverview ? (
            <Link to="/overview" className="btn btn--secondary btn--md" onClick={onClose}>
              Overview
            </Link>
          ) : null}
          {perms.canSeeSettings ? (
            <Link to="/settings" className="btn btn--secondary btn--md" onClick={onClose}>
              Settings
            </Link>
          ) : null}
        </div>
      ) : null}
    </Modal>
  );
}
