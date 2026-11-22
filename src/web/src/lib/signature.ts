// Who is offered which signature action. The API decides in the end; these rules keep the
// screens from offering a button that the API will refuse (review findings M4, M5, L1, L6).
import type { ContractDetail, ContractDocument, Me, SignatureRequest } from '../api/types';

type ESignConfig = { enabled: boolean; requireApproved?: boolean } | undefined;

/**
 * `SignatureRules.StatusAllowsSending` on the API: an approved contract can always be sent; with
 * "Only approved contracts can be sent" switched off in Settings > E-signature, drafts and active
 * contracts can be sent too.
 */
export function statusAllowsSending(status: ContractDetail['status'], requireApproved: boolean | undefined): boolean {
  if (status === 'Approved') return true;
  return requireApproved === false && (status === 'Draft' || status === 'Active');
}

/**
 * "Send for signature" on the record: `send` opens the dialog, `ask` shows who has signing rights,
 * null hides the action. `canSendForSignature` comes from the API and already includes the
 * contract's status, so it also works against an API that does not report `requireApproved`.
 */
export function sendForSignatureOffer(c: Pick<ContractDetail, 'status' | 'signature' | 'canSendForSignature'>, eSign: ESignConfig): 'send' | 'ask' | null {
  if (!eSign?.enabled || c.signature?.status === 'Sent') return null;
  if (c.canSendForSignature && (c.status === 'Approved' || c.status === 'Draft' || c.status === 'Active')) return 'send';
  return statusAllowsSending(c.status, eSign.requireApproved) ? 'ask' : null;
}

/**
 * "Record manual signature" is for the owner, the delegate and administrators, and the API also
 * asks for edit rights: an owner or delegate without the ContractOwner role cannot record it.
 */
export function canRecordManualSignature(c: Pick<ContractDetail, 'ownerId' | 'delegateId' | 'canEdit'>, me: Pick<Me, 'id' | 'roles'>): boolean {
  return c.canEdit && (c.ownerId === me.id || c.delegateId === me.id || me.roles.includes('Admin'));
}

/**
 * Resend, cancel and simulate: the sender, a colleague whose signing right covers the contract's
 * legal entity, or an administrator (`SignatureService.LoadManageableAsync`).
 */
export function canManageSignatureRequest(r: Pick<SignatureRequest, 'createdById' | 'createdBy'>, c: Pick<ContractDetail, 'entityId'>, me: Pick<Me, 'id' | 'name' | 'roles' | 'canSendForSignature' | 'signatureEntityIds'>): boolean {
  if (me.roles.includes('Admin')) return true;
  if (r.createdById ? r.createdById === me.id : r.createdBy === me.name) return true;
  const entities = me.signatureEntityIds ?? [];
  return !!me.canSendForSignature && (entities.length === 0 || entities.includes(c.entityId));
}

/** Documents that can be sent for signature: everything except the system's approval log. */
export function sendableDocuments<T extends Pick<ContractDocument, 'kind'>>(documents: T[]): T[] {
  return documents.filter((d) => d.kind !== 'ApprovalLog');
}
