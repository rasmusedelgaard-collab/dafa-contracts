import type { ApprovalInstance } from '../api/types';
import { plural } from './format';

/** "Waiting 2 days" with the urgency of the wait: new today, a few days, a week or more. */
export function waitInfo(days: number): { text: string; level: 'new' | 'waiting' | 'late' } {
  if (days <= 0) return { text: 'New today', level: 'new' };
  return { text: `Waiting ${plural(days, 'day')}`, level: days >= 7 ? 'late' : 'waiting' };
}

/** "Step 2 of 3 · you and Legal in parallel" */
export function stepLabel(instance: ApprovalInstance, stepId: string): string {
  const total = instance.stages.length;
  const stage = instance.stages.find((s) => s.steps.some((x) => x.id === stepId));
  if (!stage) return `Step ${instance.currentStage} of ${total}`;
  const others = stage.steps.filter((s) => s.id !== stepId && s.status !== 'Delegated');
  const base = `Step ${stage.order} of ${total}`;
  if (others.length > 0) return `${base} · you and ${others.map((o) => splitRoleLabel(o.roleLabel).role).join(', ')} in parallel`;
  return total > 1 && stage.order === total ? `${base} · final approval` : base;
}

/**
 * Splits a role label into the role and the explanation the API appends in brackets:
 * "Department head (instead of Søren Dahl, who owns or submitted the contract)".
 */
export function splitRoleLabel(label: string): { role: string; note: string | null } {
  const at = label.indexOf(' (');
  if (at < 0 || !label.endsWith(')')) return { role: label, note: null };
  const note = label.slice(at + 2, -1);
  return { role: label.slice(0, at), note: note.charAt(0).toUpperCase() + note.slice(1) };
}

const joinAnd = (parts: string[]) => (parts.length <= 1 ? parts.join('') : `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}`);

/**
 * The route in one sentence, for the phone approval view (PhoneApprove.dc.html):
 * "Department head approved · You and Legal now · Group CEO next". `now: true` marks the part
 * that is the current stage.
 */
export function routeSummary(instance: ApprovalInstance, myStepId: string): { text: string; now: boolean }[] {
  return instance.stages.map((stage) => {
    const steps = stage.steps.filter((s) => s.status !== 'Delegated');
    const mine = steps.some((s) => s.id === myStepId);
    const names = [...(mine ? ['You'] : []), ...steps.filter((s) => s.id !== myStepId).map((s) => splitRoleLabel(s.roleLabel).role)];
    const who = joinAnd(names);
    if (steps.some((s) => s.status === 'Rejected')) return { text: `${who} rejected`, now: false };
    if (stage.order < instance.currentStage || (steps.length > 0 && steps.every((s) => s.status === 'Approved'))) return { text: `${who} approved`, now: false };
    if (stage.order === instance.currentStage) return { text: `${who} now`, now: true };
    return { text: `${who} next`, now: false };
  });
}
