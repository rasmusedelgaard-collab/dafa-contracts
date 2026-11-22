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
  if (others.length > 0) return `${base} · you and ${others.map((o) => o.roleLabel.replace(/ \(delegated by .*\)$/, '')).join(', ')} in parallel`;
  return total > 1 && stage.order === total ? `${base} · final approval` : base;
}
