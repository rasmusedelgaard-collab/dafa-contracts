// The access model from docs/BUILD_BRIEF.md section 3, enforced in ONE place for the mock.
// Every list, detail and document call in mockApi.ts goes through these functions.
import type { ApprovalRec, ContractRec, UserRec } from './db';

export function isAdmin(user: UserRec): boolean {
  return user.roles.includes('Admin');
}

/** Owner, delegate or responsible on the contract. */
export function isOnContract(user: UserRec, c: ContractRec): boolean {
  return c.ownerId === user.id || c.delegateId === user.id || c.responsibleId === user.id;
}

/** True while the user has an open (Pending) step on a pending approval of this contract. */
export function isCurrentApprover(user: UserRec, c: ContractRec, approvals: ApprovalRec[]): boolean {
  return approvals.some(
    (a) =>
      a.contractId === c.id &&
      a.status === 'Pending' &&
      a.stages.some(
        (s) => s.order === a.currentStage && s.steps.some((st) => st.status === 'Pending' && st.approverId === user.id),
      ),
  );
}

/**
 * A user sees a contract if ANY is true:
 * 1. member of the contract's department (departments cut across all legal entities);
 * 2. entity management for the contract's legal entity;
 * 3. group management (sees all);
 * 4. owner, delegate or responsible on the contract;
 * 5. admin.
 * The current approver of a pending step also gets read access while the step is open.
 */
export function canSee(user: UserRec, c: ContractRec, approvals: ApprovalRec[]): boolean {
  return (
    user.departmentIds.includes(c.departmentId) ||
    user.managedEntityIds.includes(c.entityId) ||
    user.isGroupManagement ||
    isOnContract(user, c) ||
    isAdmin(user) ||
    isCurrentApprover(user, c, approvals)
  );
}

/** ContractOwner may create contracts; Admin always may. */
export function canCreate(user: UserRec): boolean {
  return isAdmin(user) || user.roles.includes('ContractOwner');
}

/** ContractOwner edits their own contracts (owner, delegate or responsible); Admin edits all. */
export function canEdit(user: UserRec, c: ContractRec): boolean {
  if (isAdmin(user)) return true;
  return user.roles.includes('ContractOwner') && isOnContract(user, c);
}

/** The people on the contract (and Admin) record the renewal decision on an active contract with a deadline. */
export function canDecideRenewal(user: UserRec, c: ContractRec): boolean {
  if (c.status !== 'Active' || !c.endDate) return false;
  return isAdmin(user) || isOnContract(user, c);
}

/** Overview is for entity management, group management and admins. */
export function canSeeOverview(user: UserRec): boolean {
  return isAdmin(user) || user.isGroupManagement || user.managedEntityIds.length > 0;
}
