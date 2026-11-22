// Release 3 seed: master data, tags, custom fields, collaboration and admin settings.
// Everything is fictional demo data, built on top of the release 1–2 seed.
import { addDays } from '../../lib/dates';
import { FX_TO_DKK } from '../../lib/format';
import type { CustomFieldDef, Id, JobInfo, NotificationEvent, NotificationEventSetting, RecipientRule, NotificationChannel } from '../types';
import { seedId, type ContractRec, type Db, type DocumentRec } from './db';

export type R3Keys =
  | 'currencyTable'
  | 'tags'
  | 'watchers'
  | 'comments'
  | 'notifications'
  | 'preferences'
  | 'views'
  | 'dataRoom'
  | 'customFields'
  | 'fieldRules'
  | 'importSessions'
  | 'importHistory'
  | 'syncSources'
  | 'notificationSettings'
  | 'jobs'
  | 'appSettings'
  | 'syncConnections';
export type BaseDb = Omit<Db, R3Keys>;

const G = { tag: 20, field: 21, comment: 22, notification: 23, view: 24, sync: 25, run: 26, cp: 27, audit: 28, import: 29 };

const CURRENCY_NAMES: Record<string, string> = {
  DKK: 'Danish krone',
  EUR: 'Euro',
  SEK: 'Swedish krona',
  USD: 'US dollar',
  PLN: 'Polish złoty',
  CNY: 'Chinese yuan',
  GBP: 'Pound sterling',
  NOK: 'Norwegian krone',
};

interface EventDef {
  event: NotificationEvent;
  label: string;
  channels: NotificationChannel[];
  recipients: RecipientRule[];
  subject: string;
  body: string;
  placeholders: string[];
}

const COMMON = ['contract.title', 'contract.no', 'contract.url', 'contract.entity', 'contract.counterparty', 'recipient.name', 'actor.name', 'app.name'];

export const EVENT_DEFS: EventDef[] = [
  { event: 'NoticeReminder', label: 'Notice deadline reminder', channels: ['Email', 'InApp'], recipients: ['ReminderRecipient'], subject: 'Reminder: {{contract.title}} – notice in {{days}} days', body: 'Hi {{recipient.name}},\n\nThe notice deadline for {{contract.title}} ({{contract.no}}) is {{deadline}}. Record a renewal decision in {{app.name}}: {{contract.url}}', placeholders: [...COMMON, 'deadline', 'days'] },
  { event: 'Escalation', label: 'Escalation to the owner', channels: ['Email', 'InApp'], recipients: ['Owner'], subject: 'Escalation: no decision on {{contract.title}}', body: 'No renewal decision has been recorded {{days}} days before the notice deadline ({{deadline}}). {{contract.url}}', placeholders: [...COMMON, 'deadline', 'days'] },
  { event: 'ObligationDue', label: 'Obligation due', channels: ['Email', 'InApp'], recipients: ['Responsible'], subject: 'Obligation due {{deadline}}: {{contract.title}}', body: 'An obligation on {{contract.title}} is due {{deadline}}. {{contract.url}}', placeholders: [...COMMON, 'deadline', 'days'] },
  { event: 'AnnualReview', label: 'Annual review (open-ended)', channels: ['Email'], recipients: ['ReminderRecipient'], subject: 'Annual review: {{contract.title}}', body: 'This open-ended contract has its anniversary in {{days}} days. Check that it is still needed. {{contract.url}}', placeholders: [...COMMON, 'deadline', 'days'] },
  { event: 'ApprovalRequested', label: 'Approval requested', channels: ['Email', 'Teams', 'InApp'], recipients: ['Approver'], subject: 'Approval needed: {{contract.title}}', body: '{{actor.name}} asks for your approval as {{step.role}}. {{contract.url}}', placeholders: [...COMMON, 'step.role', 'comment'] },
  { event: 'ApprovalDecided', label: 'Approval decided', channels: ['Email', 'InApp'], recipients: ['Owner'], subject: '{{contract.title}} was {{decision}}', body: '{{actor.name}} {{decision}} the contract. {{comment}}', placeholders: [...COMMON, 'step.role', 'comment', 'decision'] },
  { event: 'ApprovalDelegated', label: 'Approval delegated', channels: ['Email', 'InApp'], recipients: ['Approver'], subject: 'Approval delegated to you: {{contract.title}}', body: '{{actor.name}} delegated their approval step to you. {{contract.url}}', placeholders: [...COMMON, 'step.role'] },
  { event: 'RenewalDecision', label: 'Renewal decision recorded', channels: ['Email', 'InApp'], recipients: ['Owner', 'Watchers'], subject: 'Renewal decision: {{contract.title}}', body: '{{actor.name}} recorded "{{decision}}". {{comment}}', placeholders: [...COMMON, 'decision', 'comment'] },
  { event: 'Mention', label: 'Mentioned in a comment', channels: ['Email', 'InApp'], recipients: ['Mentioned'], subject: '{{actor.name}} mentioned you on {{contract.title}}', body: '{{comment}}\n\n{{contract.url}}', placeholders: [...COMMON, 'comment'] },
  { event: 'WatchedContractChanged', label: 'Watched contract changed', channels: ['InApp'], recipients: ['Watchers'], subject: '{{contract.title}} changed', body: '{{actor.name}}: {{change}}', placeholders: [...COMMON, 'change'] },
  { event: 'DataCompletionRequested', label: 'Data completion requested', channels: ['Email', 'InApp'], recipients: ['Owner', 'Delegate'], subject: 'Please complete {{contract.title}}', body: '{{actor.name}} asks you to fill in: {{missing}}. {{message}} {{contract.url}}', placeholders: [...COMMON, 'missing', 'message'] },
  { event: 'ImportFinished', label: 'Import finished', channels: ['InApp'], recipients: ['Admins'], subject: 'Import of {{import.file}} finished', body: '{{import.created}} created, {{import.failed}} failed.', placeholders: ['import.file', 'import.created', 'import.failed', 'actor.name', 'app.name'] },
  { event: 'SyncFailed', label: 'Sync source failed', channels: ['Email', 'InApp'], recipients: ['Admins'], subject: 'Sync failed: {{source.name}}', body: 'The last run of {{source.name}} failed. Open Settings to see the errors.', placeholders: ['source.name', 'app.name'] },
];

/** Text of a seeded document, so document search has something to find. */
function documentText(d: DocumentRec, c: ContractRec, entityName: string): string {
  const parts = [
    `${d.name.replace(/\.[a-z]+$/i, '')}.`,
    `This agreement is made between ${entityName} and ${c.counterpartyName}.`,
    c.paymentTerms ? `Payment terms: ${c.paymentTerms}.` : '',
    c.governingLaw ? `This agreement is governed by ${c.governingLaw}.` : '',
    c.priceAdjustment ? `Prices are adjusted ${c.priceAdjustment.toLowerCase()}.` : '',
    c.liabilityCap ? `Each party's liability is limited to ${c.liabilityCap}.` : '',
    c.exclusivity && c.exclusivity !== 'None' ? `Exclusivity: ${c.exclusivity}.` : '',
    c.changeOfControl ? 'Either party may terminate this agreement if the other party undergoes a change of control.' : '',
    c.noticePeriodDays ? `Termination requires ${c.noticePeriodDays} days' written notice.` : '',
  ];
  const extra: Record<string, string> = {
    'Packaging supply': 'The supplier delivers corrugated boxes and FSC certified cardboard packaging to the Jönköping warehouse, with deliveries every Tuesday.',
    'Raw material supply – rubber compounds': 'The supplier guarantees REACH compliance and provides a certificate of analysis with every batch of EPDM and NBR compounds.',
    'Freight framework – Nordics': 'Lane prices include a diesel surcharge reviewed monthly. Pallet exchange follows the EUR-pallet pool.',
    'IT support and hosting': 'Service desk hours 07:00–17:00 CET. Backups are kept for 35 days in a Danish data centre. A data processing agreement is attached as appendix 3.',
    'Warehouse lease – Jönköping': 'The lease covers 6,400 square metres of warehouse space and 18 parking spaces. Rent is indexed to the Swedish CPI.',
    'Group insurance programme': 'Cover includes property damage, product liability, cargo and directors and officers liability with a deductible of DKK 250,000.',
  };
  if (d.kind === 'Appendix') parts.push('Appendix: prices per article number, valid from the first of January. Volume rebate 2% above 500 tonnes.');
  if (d.kind === 'Amendment') parts.push('This amendment adds a minimum annual volume commitment in return for a price reduction.');
  if (d.kind === 'ApprovalLog') parts.push('Approval log generated by DAFA Contracts.');
  if (d.kind === 'Main' && extra[c.title]) parts.push(extra[c.title] ?? '');
  return parts.filter(Boolean).join(' ');
}

function field(n: number, def: Omit<CustomFieldDef, 'id' | 'order' | 'isActive'>): CustomFieldDef {
  return { ...def, id: seedId(G.field, n), order: n - 1, isActive: true };
}

/** Adds the release 3 tables to the base seed. */
export function extendSeed(base: BaseDb, today: string): Db {
  const byTitle = (t: string) => base.contracts.find((c) => c.title === t);
  const user = (email: string): Id => base.users.find((u) => u.email.startsWith(email))?.id ?? base.users[0]?.id ?? '';
  const ras = user('rasmus');
  const met = user('mette');
  const jon = user('jonas');
  const typeOf = (name: string) => base.contractTypes.find((t) => t.name === name)?.id ?? '';

  // The one free-text counterparty gets a master record too.
  const shareholders = byTitle("Shareholders' agreement");
  if (shareholders && !shareholders.supplierId) {
    const id = seedId(G.cp, 1);
    base.suppliers.push({ id, name: shareholders.counterpartyName, country: 'DK', vatNo: null, kind: 'Other', externalId: null, city: 'Horsens', email: null, phone: null, isActive: true, source: 'Manual', updatedAt: `${addDays(today, -300)}T08:00:00Z` });
    shareholders.supplierId = id;
  }
  // One user shows how an Entra ID account looks in Users and access (synced fields are read-only).
  const louise = base.users.find((u) => u.email.startsWith('louise'));
  if (louise) {
    louise.source = 'Entra';
    louise.externalId = '7d3c1e52-0b7a-4f0e-9d1f-6a2b8c4e1f90';
    louise.lastSignInAt = `${addDays(today, -1)}T07:42:00Z`;
  }
  // Two near-duplicates for the merge demo.
  base.suppliers.push(
    { id: seedId(G.cp, 2), name: 'Nordpolymer Compounds', country: 'DK', vatNo: null, kind: 'Supplier', externalId: null, city: 'Kolding', email: 'sales@nordpolymer.example', phone: null, isActive: true, source: 'Import', updatedAt: `${addDays(today, -40)}T08:00:00Z` },
    { id: seedId(G.cp, 3), name: 'ScanFreight Logistics', country: 'DK', vatNo: base.suppliers[1]?.vatNo ?? null, kind: 'Supplier', externalId: null, city: null, email: null, phone: null, isActive: true, source: 'Import', updatedAt: `${addDays(today, -40)}T08:00:00Z` },
  );

  // Document text for search.
  for (const d of base.documents) {
    const c = base.contracts.find((x) => x.id === d.contractId);
    if (c) d.text = documentText(d, c, base.entities.find((e) => e.id === c.entityId)?.name ?? 'DAFA');
  }

  const tags = [
    { id: seedId(G.tag, 1), name: 'Critical supplier', color: 'red' as const, description: 'Production stops without it.' },
    { id: seedId(G.tag, 2), name: 'Renegotiate 2027', color: 'amber' as const, description: null },
    { id: seedId(G.tag, 3), name: 'ESG review', color: 'green' as const, description: 'Part of the supplier sustainability review.' },
    { id: seedId(G.tag, 4), name: 'Single source', color: 'purple' as const, description: null },
    { id: seedId(G.tag, 5), name: 'Due diligence', color: 'blue' as const, description: 'Flagged for the vendor due diligence.' },
  ];
  const tag = (title: string, ...ids: number[]) => {
    const c = byTitle(title);
    if (c) c.tagIds = ids.map((i) => seedId(G.tag, i));
  };
  tag('Raw material supply – rubber compounds', 1, 4, 5);
  tag('Foam raw materials – framework', 1, 5);
  tag('Freight framework – Nordics', 2);
  tag('IT support and hosting', 2);
  tag('ERP software subscription', 5);
  tag('Banking and credit facilities', 5);
  tag('Energy supply', 3);
  tag('Packaging supply', 3);
  tag('Raw material supply – EPDM granulate', 1, 4);

  const direct = typeOf('Supplier – direct materials');
  const indirect = typeOf('Supplier – indirect and services');
  const it = typeOf('IT and SaaS');
  const customFields: CustomFieldDef[] = [
    field(1, { key: 'erpVendorNo', label: 'ERP vendor no.', type: 'Text', contractTypeIds: [], required: false, helpText: 'The supplier number in the ERP system.', options: [], showInRegister: true, showInFilters: false }),
    field(2, { key: 'riskLevel', label: 'Risk level', type: 'Choice', contractTypeIds: [], required: false, helpText: 'Supply or compliance risk if the contract ends.', options: ['Low', 'Medium', 'High'], showInRegister: true, showInFilters: true }),
    field(3, { key: 'soleSupplier', label: 'Sole supplier', type: 'YesNo', contractTypeIds: [direct, indirect], required: false, helpText: 'No qualified alternative supplier.', options: [], showInRegister: false, showInFilters: true }),
    field(4, { key: 'dpaSigned', label: 'Data processing agreement', type: 'YesNo', contractTypeIds: [it], required: false, helpText: 'A GDPR data processing agreement is signed.', options: [], showInRegister: false, showInFilters: true }),
    field(5, { key: 'nextAudit', label: 'Next supplier audit', type: 'Date', contractTypeIds: [direct], required: false, helpText: null, options: [], showInRegister: false, showInFilters: false }),
    field(6, { key: 'certifications', label: 'Certifications', type: 'MultiChoice', contractTypeIds: [direct, indirect], required: false, helpText: null, options: ['ISO 9001', 'ISO 14001', 'IATF 16949', 'REACH'], showInRegister: false, showInFilters: true }),
  ];
  const cf = (title: string, values: ContractRec['customFields']) => {
    const c = byTitle(title);
    if (c) c.customFields = values;
  };
  cf('Raw material supply – rubber compounds', { erpVendorNo: 'V10040', riskLevel: 'High', soleSupplier: false, nextAudit: addDays(today, 190), certifications: ['ISO 9001', 'REACH'] });
  cf('Foam raw materials – framework', { erpVendorNo: 'V10261', riskLevel: 'High', soleSupplier: true, certifications: ['ISO 9001', 'ISO 14001'] });
  cf('Raw material supply – EPDM granulate', { erpVendorNo: 'V10157', riskLevel: 'Medium', soleSupplier: true });
  cf('Adhesive supply agreement', { erpVendorNo: 'V10170', riskLevel: 'Low' });
  cf('IT support and hosting', { riskLevel: 'Medium', dpaSigned: true });
  cf('ERP software subscription', { riskLevel: 'High', dpaSigned: false });
  cf('Freight framework – Nordics', { erpVendorNo: 'V10053', riskLevel: 'Medium' });
  cf('Energy supply', { erpVendorNo: 'V10131', riskLevel: 'High', soleSupplier: false });

  // Version history on the flagship contract: what the key terms were before today's values.
  const rubber = byTitle('Raw material supply – rubber compounds');
  const audit = [...base.audit];
  if (rubber) {
    audit.push(
      { id: seedId(G.audit, 1), at: `${addDays(today, -446)}T09:30:00Z`, userId: met, action: 'ContractUpdated', contractId: rubber.id, text: 'Details updated (amendment 1: volume commitment)', changes: [
        { field: 'annualValue', from: '7900000', to: '8400000' },
        { field: 'totalValue', from: '23700000', to: '25200000' },
        { field: 'paymentTerms', from: '45 days net', to: '60 days net' },
      ] },
      { id: seedId(G.audit, 2), at: `${addDays(today, -323)}T09:30:00Z`, userId: jon, action: 'ContractUpdated', contractId: rubber.id, text: 'Details updated (price list 2026)', changes: [
        { field: 'priceAdjustment', from: 'Annual, fixed', to: 'Annual, index-linked' },
        { field: 'liabilityCap', from: '6 months of fees', to: '12 months of fees' },
      ] },
    );
  }

  const comments = rubber
    ? [
        { id: seedId(G.comment, 1), contractId: rubber.id, authorId: jon, body: '@Mette Kjær the supplier wants to move the price review to March. Fine with you?', mentionIds: [met], createdAt: `${addDays(today, -6)}T10:12:00Z`, editedAt: null },
        { id: seedId(G.comment, 2), contractId: rubber.id, authorId: met, body: 'Yes, as long as the index stays the same. Let us decide on renewal before the notice deadline.', mentionIds: [], createdAt: `${addDays(today, -5)}T08:40:00Z`, editedAt: null },
      ]
    : [];

  const notifications = [
    ...(rubber ? [{ id: seedId(G.notification, 1), userId: met, event: 'Mention' as const, title: 'Jonas Bak mentioned you on Raw material supply – rubber compounds', body: 'the supplier wants to move the price review to March. Fine with you?', contractId: rubber.id, link: `/contracts/${rubber.id}#comments`, actorId: jon, createdAt: `${addDays(today, -6)}T10:12:00Z`, readAt: null }] : []),
    { id: seedId(G.notification, 2), userId: ras, event: 'WatchedContractChanged' as const, title: 'ERP software subscription changed', body: 'Peter Aagaard approved as department head.', contractId: byTitle('ERP software subscription')?.id ?? null, link: `/contracts/${byTitle('ERP software subscription')?.id ?? ''}`, actorId: user('peter'), createdAt: `${addDays(today, -2)}T08:31:00Z`, readAt: null },
    { id: seedId(G.notification, 3), userId: ras, event: 'NoticeReminder' as const, title: 'Notice in 27 days: Group insurance programme', body: 'Record a renewal decision before the notice deadline.', contractId: byTitle('Group insurance programme')?.id ?? null, link: `/contracts/${byTitle('Group insurance programme')?.id ?? ''}`, actorId: null, createdAt: `${addDays(today, -3)}T07:00:00Z`, readAt: `${addDays(today, -3)}T09:00:00Z` },
  ];

  const watchers = [
    ...(rubber ? [{ contractId: rubber.id, userId: ras }] : []),
    ...[byTitle('ERP software subscription'), byTitle('Group insurance programme')].filter((c): c is ContractRec => !!c).map((c) => ({ contractId: c.id, userId: ras })),
    ...[byTitle('Freight framework – Nordics')].filter((c): c is ContractRec => !!c).map((c) => ({ contractId: c.id, userId: met })),
  ];

  const purchasing = base.departments.find((d) => d.name === 'Purchasing')?.id ?? '';
  const views = [
    { id: seedId(G.view, 1), name: 'Purchasing – renewals this year', scope: 'Shared' as const, ownerId: met, columns: ['contract', 'counterparty', 'entity', 'owner', 'notice', 'annualValue', 'status'] as const, query: { departmentId: purchasing, status: 'Active' as const, sort: 'notice' as string }, isDefault: false, updatedAt: `${addDays(today, -20)}T08:00:00Z` },
    { id: seedId(G.view, 2), name: 'Large contracts', scope: 'Personal' as const, ownerId: ras, columns: ['contract', 'counterparty', 'entity', 'annualValue', 'totalValue', 'end', 'tags'] as const, query: { valueBand: ['5to20m' as const, 'gte20m' as const], sort: '-value' as string }, isDefault: false, updatedAt: `${addDays(today, -10)}T08:00:00Z` },
  ].map((v) => ({ ...v, columns: [...v.columns] }));

  const lastSync = `${addDays(today, -1)}T05:00:00Z`;
  const syncSources = [
    {
      id: seedId(G.sync, 1),
      name: 'ERP vendors (Power BI)',
      target: 'counterparties' as const,
      kind: 'PowerBi' as const,
      powerBi: { workspaceId: 'dafa-finance', datasetId: 'erp-vendors', daxQuery: "EVALUATE SELECTCOLUMNS(Vendors, \"VendorNo\", Vendors[No], \"Name\", Vendors[Name], \"VAT\", Vendors[VAT], \"Country\", Vendors[Country])", table: null },
      sql: null,
      mapping: { externalId: 'VendorNo', name: 'Name', vatNo: 'VAT', country: 'Country' },
      options: { mode: 'CreateAndUpdate' as const },
      schedule: { frequency: 'Daily' as const, time: '06:00', dayOfWeek: null },
      isActive: true,
      runs: [
        { id: seedId(G.run, 2), sourceId: seedId(G.sync, 1), trigger: 'Schedule' as const, startedAt: lastSync, finishedAt: lastSync.replace('05:00:00', '05:00:04'), status: 'Succeeded' as const, read: 31, created: 0, updated: 2, skipped: 29, failed: 0, errors: [] },
        { id: seedId(G.run, 1), sourceId: seedId(G.sync, 1), trigger: 'Schedule' as const, startedAt: `${addDays(today, -2)}T05:00:00Z`, finishedAt: `${addDays(today, -2)}T05:00:09Z`, status: 'PartiallySucceeded' as const, read: 31, created: 1, updated: 0, skipped: 29, failed: 1, errors: ['Row 17: country "Danmark" is not a two-letter code'] },
      ],
    },
  ];

  const job = (key: JobInfo['key'], label: string, description: string, frequency: JobInfo['schedule']['frequency'], time = '06:00', status: 'Succeeded' | 'Failed' = 'Succeeded', message: string | null = null): JobInfo => ({
    key,
    label,
    description,
    schedule: { frequency, time, dayOfWeek: frequency === 'Weekly' ? 1 : null },
    lastRun: frequency === 'Off' ? null : { at: `${addDays(today, frequency === 'Weekly' ? -4 : 0)}T${frequency === 'Hourly' ? '07' : time.slice(0, 2)}:00:00Z`, status, message, durationMs: 1200 },
    nextRunAt: null,
  });

  const events: NotificationEventSetting[] = EVENT_DEFS.map((d) => ({ ...d, enabled: true, extraRecipientIds: [], placeholders: [...d.placeholders], channels: [...d.channels], recipients: [...d.recipients] }));

  return {
    ...base,
    audit,
    currencyTable: base.currencies.map((code) => ({ code, name: CURRENCY_NAMES[code] ?? code, rateToDkk: FX_TO_DKK[code] ?? 1, isActive: true })),
    tags,
    watchers,
    comments,
    notifications,
    preferences: {},
    views,
    dataRoom: [],
    customFields,
    fieldRules: {
      types: [
        { typeId: it, required: ['responsibleId' as const], defaults: { currency: 'DKK', noticePeriodDays: 90, reminderRecipient: 'Responsible' as const, autoRenew: true, renewalMonths: 12 } },
        { typeId: typeOf('NDA'), required: [], defaults: { currency: 'DKK', noticePeriodDays: 0, reminderRecipient: 'Owner' as const, autoRenew: false, renewalMonths: null } },
        { typeId: typeOf('Lease and property'), required: ['endDate' as const, 'annualValue' as const], defaults: { currency: null, noticePeriodDays: 180, reminderRecipient: 'Owner' as const, autoRenew: true, renewalMonths: 36 } },
      ],
    },
    importSessions: [],
    importHistory: [
      { id: seedId(G.import, 1), kind: 'counterparties', fileName: 'vendors-2026-q3.csv', ranAt: `${addDays(today, -30)}T13:20:00Z`, ranBy: 'Peter Aagaard', options: { mode: 'SkipDuplicates' }, created: 2, updated: 0, skipped: 28, failed: 0, errors: [], source: 'File' },
    ],
    syncSources,
    notificationSettings: {
      events,
      digestDefault: 'Weekly',
      reminderSchedules: [{ typeId: typeOf('Lease and property'), noticeDays: [270, 180, 120], escalationDays: 60 }],
    },
    jobs: [
      job('Reminders', 'Notice reminders and escalation', 'Sends notice reminders and escalates undecided renewals.', 'Hourly'),
      job('Obligations', 'Obligation reminders', 'Reminds the responsible person before an obligation is due.', 'Daily', '06:00'),
      job('ContractEnd', 'Contract end and auto-renewal', 'Expires ended contracts and rolls auto-renewing ones forward.', 'Daily', '01:00'),
      job('Digest', 'Digest e-mails', 'Sends the daily and weekly digests.', 'Daily', '07:00'),
      job('CounterpartySync', 'Counterparty sync', 'Runs the sync sources that are due.', 'Daily', '05:00'),
      job('DirectorySync', 'Directory sync', 'Reads users from Entra ID (production only).', 'Off'),
      job('UploadCleanup', 'Upload cleanup', 'Removes intake uploads older than 7 days.', 'Daily', '02:00'),
      job('DataRoomCleanup', 'Data room cleanup', 'Deletes expired data room exports.', 'Weekly', '03:00', 'Failed', 'Timed out after 60 seconds; retried on the next run.'),
    ],
    appSettings: {
      appName: 'DAFA Contracts',
      dateFormat: 'd MMM yyyy',
      numberFormat: 'en-GB',
      landingPageByRole: { Admin: 'home', Approver: 'home', ContractOwner: 'home', Reader: 'contracts' },
      features: { aiSuggestions: true, templates: true, eSign: false },
      sessionTimeoutMinutes: 480,
      dataQualityFields: ['noticePeriodDays', 'contractValue', 'signedDate', 'changeOfControl'],
      ddThresholdDkk: 5_000_000,
    },
    syncConnections: [
      { name: 'erp-dw', kind: 'Sql', description: 'ERP data warehouse, read-only login (server configuration)' },
      { name: 'finance-dw', kind: 'Sql', description: 'Finance data warehouse on Azure SQL (server configuration)' },
      { name: 'powerbi', kind: 'PowerBi', description: 'Power BI service through the app registration' },
    ],
  };
}
