// Seeded demo data for the mock API. Everything is fictional test data. Dates are relative
// to "today" so the demo always has contracts close to their notice deadline.
import { addDays, addMonths } from '../../lib/dates';
import type { ApprovalRule, ContractStatus, Deviation, Id, ReminderRecipient, RenewalDecision, Supplier } from '../types';
import {
  seedId,
  type ApprovalRec,
  type AuditRec,
  type ContractRec,
  type Db,
  type DocumentRec,
  type LinkRec,
  type ObligationRec,
  type StepRec,
  type UserRec,
} from './db';

const G = { entity: 1, dept: 2, type: 3, user: 4, supplier: 5, contract: 6, link: 7, obligation: 8, document: 9, approval: 10, step: 11, rule: 12, retention: 13, template: 14, audit: 15, cover: 16 };

const ENTITY_NAMES = [
  'DAFA Group A/S',
  'DAFA A/S',
  'DAFA Building Solutions A/S',
  'DAFA Sverige AB',
  'DAFA Deutschland GmbH',
  'DAFA Sealing Technology (Tianjin) Co., Ltd.',
  'DAFA US Inc.',
  'DAFA Polska sp. z o.o.',
  'DAFA Italia S.r.l.',
];

const DEPARTMENT_NAMES = [
  'Management',
  'Finance',
  'Purchasing',
  'Sales',
  'Production',
  'Supply chain and logistics',
  'Quality',
  'R&D and engineering',
  'Maintenance and facilities',
  'HSE',
  'IT',
  'HR',
  'Marketing',
];

const TYPE_NAMES = [
  'Supplier – direct materials',
  'Supplier – indirect and services',
  'Logistics',
  'IT and SaaS',
  'Lease and property',
  'Finance and insurance',
  'NDA',
  'R&D and partnership',
  'Corporate (shareholder, M&A)',
  'Other',
];

export const ENT = { group: 0, dafa: 1, building: 2, sverige: 3, deutschland: 4, tianjin: 5, us: 6, polska: 7, italia: 8 } as const;
export const DEP = { management: 0, finance: 1, purchasing: 2, sales: 3, production: 4, logistics: 5, quality: 6, rnd: 7, maintenance: 8, hse: 9, it: 10, hr: 11, marketing: 12 } as const;
export const TYP = { direct: 0, indirect: 1, logistics: 2, it: 3, lease: 4, finance: 5, nda: 6, rnd: 7, corporate: 8, other: 9 } as const;

export const entityId = (i: number): Id => seedId(G.entity, i + 1);
export const departmentId = (i: number): Id => seedId(G.dept, i + 1);
export const typeId = (i: number): Id => seedId(G.type, i + 1);

type UserKey = 'ras' | 'kar' | 'met' | 'jon' | 'cam' | 'sor' | 'ann' | 'lou' | 'pet' | 'tho' | 'emm' | 'hen';
const USER_ORDER: UserKey[] = ['ras', 'kar', 'met', 'jon', 'cam', 'sor', 'ann', 'lou', 'pet', 'tho', 'emm', 'hen'];
/** Seeded demo user ids by short key (exported for tests). */
export const USER = Object.fromEntries(USER_ORDER.map((k, i) => [k, seedId(G.user, i + 1)])) as Record<UserKey, Id>;

function user(
  key: UserKey,
  name: string,
  title: string,
  roles: UserRec['roles'],
  departments: number[],
  extra: Partial<UserRec> = {},
): UserRec {
  const parts = name.split(' ');
  const initials = `${parts[0]?.[0] ?? ''}${parts[parts.length - 1]?.[0] ?? ''}`.toUpperCase();
  const email = `${name.toLowerCase().replace(/ø/g, 'o').replace(/æ/g, 'ae').replace(/å/g, 'a').replace(/[^a-z ]/g, '').replace(/ /g, '.')}@dafa.example`;
  return {
    id: USER[key],
    name,
    email,
    initials,
    title,
    roles,
    departmentIds: departments.map(departmentId),
    managedEntityIds: [],
    isGroupManagement: false,
    ...extra,
  };
}

function seedUsers(): UserRec[] {
  return [
    user('ras', 'Rasmus Holm', 'Group CFO · group management', ['ContractOwner', 'Approver'], [DEP.finance, DEP.management], { isGroupManagement: true }),
    user('kar', 'Karen Lindberg', 'Group CEO · group management', ['ContractOwner', 'Approver'], [DEP.management], { isGroupManagement: true }),
    user('met', 'Mette Kjær', 'Purchasing manager · department head', ['ContractOwner', 'Approver'], [DEP.purchasing]),
    user('jon', 'Jonas Bak', 'Category manager · Purchasing', ['ContractOwner'], [DEP.purchasing]),
    user('cam', 'Camilla Frost', 'Buyer · Purchasing', ['ContractOwner'], [DEP.purchasing]),
    user('sor', 'Søren Dahl', 'IT manager · department head', ['ContractOwner', 'Approver'], [DEP.it]),
    user('ann', 'Anna Svensson', 'Managing director · DAFA Sverige AB management', ['ContractOwner', 'Approver'], [], { managedEntityIds: [entityId(ENT.sverige)] }),
    user('lou', 'Louise Winther', 'Legal counsel · approver only', ['Reader', 'Approver'], []),
    user('pet', 'Peter Aagaard', 'System administrator · admin', ['Admin', 'ContractOwner'], [DEP.it]),
    user('tho', 'Thomas Riis', 'Logistics manager · department head', ['ContractOwner', 'Approver'], [DEP.logistics]),
    user('emm', 'Emma Nygaard', 'Quality engineer · reader', ['Reader'], [DEP.quality]),
    user('hen', 'Henrik Madsen', 'Operations manager · Production, Maintenance, HSE', ['ContractOwner', 'Approver'], [DEP.production, DEP.maintenance, DEP.hse]),
  ];
}

const SUPPLIERS: [string, string][] = [
  ['Nordpolymer Compounds ApS', 'DK'], // 0
  ['ScanFreight Logistics A/S', 'DK'],
  ['Hosting Partner Nord A/S', 'DK'],
  ['Fastighets AB Vätterporten', 'SE'],
  ['Meridian ERP Systems B.V.', 'NL'],
  ['Technika Serwis Sp. z o.o.', 'PL'], // 5
  ['Brenner Dichtungstechnik GmbH', 'DE'],
  ['Jysk Energi Erhverv A/S', 'DK'],
  ['CADworks Software GmbH', 'DE'],
  ['Tianjin Huaxin Rubber Co., Ltd.', 'CN'],
  ['Baltic Adhesives OÜ', 'EE'], // 10
  ['Midtjysk Truckservice ApS', 'DK'],
  ['Nordlys Forsikring A/S', 'DK'],
  ['Sparekassen Østjylland', 'DK'],
  ['Clean & Care Facility A/S', 'DK'],
  ['Alpina Trasporti S.r.l.', 'IT'], // 15
  ['Great Lakes Warehousing LLC', 'US'],
  ['Foamtec Europe N.V.', 'BE'],
  ['SecureDocs Nordic ApS', 'DK'],
  ['Kvalitetsinstituttet A/S', 'DK'],
  ['Vestergaard & Lund Advokatpartnerselskab', 'DK'], // 20
  ['Printhuset Horsens ApS', 'DK'],
  ['Polfolia S.A.', 'PL'],
  ['Lindqvist Emballage AB', 'SE'],
  ['Rhein-Main Immobilien GmbH', 'DE'],
  ['NordTel Erhverv A/S', 'DK'], // 25
  ['CloudSuite Licensing Ireland Ltd', 'IE'],
  ['Nordrev Revisionspartnerselskab', 'DK'],
  ['Lakeshore Office Properties LLC', 'US'],
  ['Autonoleggio Lombarda S.p.A.', 'IT'],
];

interface Spec {
  /** Contract number; generated when omitted. */
  no?: string;
  t: string;
  /** Supplier index, or a free-text counterparty. */
  cp: number | string;
  ty: number;
  en: number;
  de: number;
  own: UserKey;
  del?: UserKey;
  resp?: UserKey;
  rem?: ReminderRecipient;
  st?: ContractStatus;
  /** Days from today to the notice deadline. null = open-ended contract. */
  ni: number | null;
  /** Notice period in days. null = missing data on an imported contract. */
  np: number | null;
  /** Auto-renewal months; 0 = fixed term. */
  ar?: number;
  av?: number | null;
  tv?: number | null;
  cur?: string;
  /** Term length in months (start = end − term). */
  term?: number;
  coc?: boolean;
  dec?: RenewalDecision;
  sum?: string;
  pay?: string;
  law?: string;
  cap?: string;
  excl?: string;
  price?: string;
  dev?: Deviation[];
  /** Days from today to the end date when there is no notice period to derive it from. */
  endIn?: number;
}

const ERP_DEVIATIONS: Deviation[] = [
  { severity: 'High', text: 'Liability cap at 6 months of fees', standard: '12 months' },
  { severity: 'Medium', text: 'Auto-renewal for 36 months', standard: '12 months' },
  { severity: 'Medium', text: 'Price increase up to 7% a year', standard: 'Index-linked' },
  { severity: 'Low', text: 'Governing law: Dutch law', standard: 'Danish law' },
];

const SPECS: Spec[] = [
  // Purchasing
  { no: 'C-2024-0187', t: 'Raw material supply – rubber compounds', cp: 0, ty: TYP.direct, en: ENT.dafa, de: DEP.purchasing, own: 'met', del: 'cam', resp: 'jon', rem: 'Responsible', ni: 23, np: 75, ar: 12, av: 8_400_000, tv: 25_200_000, term: 36, pay: '60 days net', law: 'Danish law', cap: '12 months of fees', excl: 'None', price: 'Annual, index-linked',
    sum: "Supply of rubber compounds at annual index-linked prices, with a minimum volume commitment added by amendment. Renews for 12 months unless DAFA gives 75 days' notice. No change-of-control clause." },
  { t: 'Amendment 1 – volume commitment', cp: 0, ty: TYP.direct, en: ENT.dafa, de: DEP.purchasing, own: 'met', resp: 'jon', rem: 'Responsible', ni: 23, np: 75, tv: 1_200_000, term: 18, law: 'Danish law',
    sum: 'Adds a minimum annual volume to the rubber compounds supply agreement in return for a 2% price reduction.' },
  { t: 'Quality agreement – Nordpolymer', cp: 0, ty: TYP.direct, en: ENT.dafa, de: DEP.quality, own: 'hen', resp: 'emm', rem: 'Responsible', ni: null, np: 90, av: 0, law: 'Danish law' },
  { t: 'NDA – Nordpolymer', cp: 0, ty: TYP.nda, en: ENT.dafa, de: DEP.purchasing, own: 'met', ni: 400, np: 0, av: 0, term: 60, law: 'Danish law' },
  { t: 'Raw material supply – EPDM granulate', cp: 9, ty: TYP.direct, en: ENT.tianjin, de: DEP.purchasing, own: 'jon', ni: 140, np: 90, ar: 12, av: 3_100_000, cur: 'CNY', term: 24, pay: '45 days net', law: 'Chinese law' },
  { t: 'Adhesive supply agreement', cp: 10, ty: TYP.direct, en: ENT.dafa, de: DEP.purchasing, own: 'jon', resp: 'cam', rem: 'Responsible', ni: 75, np: 60, ar: 12, av: 260_000, cur: 'EUR', term: 24, pay: '30 days net', law: 'Danish law' },
  { t: 'Foam raw materials – framework', cp: 17, ty: TYP.direct, en: ENT.building, de: DEP.purchasing, own: 'met', resp: 'cam', rem: 'Responsible', ni: 44, np: 90, ar: 24, av: 5_600_000, term: 36, coc: true, pay: '60 days net', law: 'Belgian law', excl: 'Exclusive for PE foam in the Nordics',
    sum: 'Framework for polyethylene foam with exclusivity in the Nordics. Renews for 24 months. Either party may terminate on a change of control.' },
  { t: 'Packaging supply', cp: 23, ty: TYP.indirect, en: ENT.sverige, de: DEP.purchasing, own: 'cam', ni: -5, np: 30, ar: 12, av: 640_000, cur: 'SEK', term: 12, law: 'Swedish law' },
  { t: 'Film and foil supply', cp: 22, ty: TYP.direct, en: ENT.polska, de: DEP.purchasing, own: 'jon', ni: 260, np: 90, ar: 12, av: 1_150_000, cur: 'PLN', term: 24, law: 'Polish law' },
  { t: 'Temporary staff – production', cp: 14, ty: TYP.indirect, en: ENT.dafa, de: DEP.production, own: 'hen', ni: 12, np: 30, av: 900_000, term: 12, law: 'Danish law' },
  // Logistics
  { t: 'Freight framework – Nordics', cp: 1, ty: TYP.logistics, en: ENT.dafa, de: DEP.logistics, own: 'tho', del: 'ras', ni: 59, np: 90, ar: 12, av: 4_200_000, term: 24, pay: '30 days net', law: 'Danish law', price: 'Annual, fuel index',
    sum: 'Road freight for Denmark, Sweden and Norway at fixed lane prices with a fuel surcharge. Renews for 12 months.' },
  { t: 'Road freight – Italy and the Alps', cp: 15, ty: TYP.logistics, en: ENT.italia, de: DEP.logistics, own: 'tho', ni: 120, np: 60, ar: 12, av: 310_000, cur: 'EUR', term: 24, law: 'Italian law' },
  { t: 'Warehousing – Midwest', cp: 16, ty: TYP.logistics, en: ENT.us, de: DEP.logistics, own: 'tho', ni: 200, np: 90, ar: 12, av: 420_000, cur: 'USD', term: 36, law: 'Illinois law' },
  { t: 'Forklift lease and service', cp: 11, ty: TYP.indirect, en: ENT.dafa, de: DEP.maintenance, own: 'hen', ni: 88, np: 90, ar: 12, av: 380_000, term: 48, law: 'Danish law' },
  // IT
  { t: 'IT support and hosting', cp: 2, ty: TYP.it, en: ENT.group, de: DEP.it, own: 'sor', resp: 'pet', rem: 'Responsible', ni: 71, np: 90, av: 1_450_000, term: 36, dec: 'Renegotiate', law: 'Danish law', cap: '12 months of fees' },
  { t: 'ERP software subscription', cp: 4, ty: TYP.it, en: ENT.group, de: DEP.it, own: 'sor', st: 'InApproval', ni: 930, np: 180, ar: 36, av: 2_600_000, tv: 7_800_000, term: 36, coc: true, law: 'Dutch law', cap: '6 months of fees', price: 'Up to 7% a year', dev: ERP_DEVIATIONS,
    sum: 'Cloud ERP subscription for all entities over 36 months. Renews for 36 months unless notice is given 6 months before expiry. Supplier may terminate on a change of control.' },
  { t: 'Software subscription – CAD', cp: 8, ty: TYP.it, en: ENT.deutschland, de: DEP.rnd, own: 'hen', ni: 266, np: 30, ar: 12, av: 48_000, cur: 'EUR', term: 12, law: 'German law' },
  { t: 'Productivity suite licenses', cp: 26, ty: TYP.it, en: ENT.group, de: DEP.it, own: 'pet', ni: 310, np: 60, ar: 12, av: 1_100_000, term: 36, law: 'Irish law' },
  { t: 'Mobile and data – group agreement', cp: 25, ty: TYP.it, en: ENT.group, de: DEP.it, own: 'sor', ni: 33, np: 90, ar: 12, av: 520_000, term: 24, law: 'Danish law' },
  { t: 'Document shredding and archive', cp: 18, ty: TYP.indirect, en: ENT.dafa, de: DEP.finance, own: 'ras', ni: 150, np: 30, ar: 12, av: 85_000, term: 12, law: 'Danish law' },
  // Lease, property, facilities
  { t: 'Warehouse lease – Jönköping', cp: 3, ty: TYP.lease, en: ENT.sverige, de: DEP.logistics, own: 'ann', resp: 'tho', rem: 'Owner', ni: 98, np: 180, ar: 36, av: 2_900_000, cur: 'SEK', term: 60, law: 'Swedish law', price: 'Annual, CPI' },
  { t: 'Office lease – Frankfurt', cp: 24, ty: TYP.lease, en: ENT.deutschland, de: DEP.finance, own: 'ras', ni: 290, np: 180, ar: 60, av: 96_000, cur: 'EUR', term: 60, law: 'German law' },
  { t: 'Energy supply', cp: 7, ty: TYP.indirect, en: ENT.dafa, de: DEP.maintenance, own: 'hen', ni: 312, np: 90, ar: 12, av: 6_300_000, term: 24, law: 'Danish law', price: 'Spot price plus fixed margin' },
  { t: 'Machine service agreement', cp: 5, ty: TYP.indirect, en: ENT.polska, de: DEP.maintenance, own: 'hen', ni: 220, np: 60, av: 310_000, cur: 'PLN', term: 36, law: 'Polish law' },
  { t: 'Cleaning and facility services', cp: 14, ty: TYP.indirect, en: ENT.building, de: DEP.maintenance, own: 'hen', ni: null, np: null, endIn: 210, av: 420_000, term: 24 },
  { t: 'Waste handling agreement', cp: 14, ty: TYP.indirect, en: ENT.building, de: DEP.hse, own: 'hen', ni: null, np: null, endIn: 320, av: 190_000, term: 36 },
  { t: 'Scaffolding and site services', cp: 11, ty: TYP.indirect, en: ENT.building, de: DEP.production, own: 'hen', ni: null, np: null, endIn: 400, av: null, tv: null, term: 24 },
  // Finance and insurance
  { t: 'Group insurance programme', cp: 12, ty: TYP.finance, en: ENT.group, de: DEP.finance, own: 'ras', ni: 27, np: 90, ar: 12, av: 3_400_000, term: 12, law: 'Danish law',
    sum: 'Property, liability, cargo and D&O cover for all nine entities. Renews for 12 months unless terminated with 90 days’ notice.' },
  { t: 'Banking and credit facilities', cp: 13, ty: TYP.finance, en: ENT.group, de: DEP.finance, own: 'ras', ni: 205, np: 180, av: 1_200_000, tv: 3_600_000, term: 36, coc: true, law: 'Danish law',
    sum: 'Committed credit facilities with financial covenants reported quarterly. The bank may cancel the facilities on a change of control.' },
  { t: 'Audit engagement – group', cp: 27, ty: TYP.indirect, en: ENT.group, de: DEP.finance, own: 'ras', ni: 64, np: 60, ar: 12, av: 1_850_000, term: 12, law: 'Danish law' },
  { t: 'Legal services framework', cp: 20, ty: TYP.indirect, en: ENT.group, de: DEP.management, own: 'ras', ni: null, np: 30, av: 600_000, law: 'Danish law' },
  { t: 'Office lease – Chicago', cp: 28, ty: TYP.lease, en: ENT.us, de: DEP.finance, own: 'ras', ni: 240, np: 120, ar: 12, av: 72_000, cur: 'USD', term: 36, law: 'Illinois law' },
  { t: 'Company car leasing – Italy', cp: 29, ty: TYP.lease, en: ENT.italia, de: DEP.finance, own: 'ras', ni: 175, np: 60, av: 54_000, cur: 'EUR', term: 36, law: 'Italian law' },
  // NDA, R&D, quality
  { t: 'Mutual NDA – development project', cp: 6, ty: TYP.nda, en: ENT.deutschland, de: DEP.rnd, own: 'hen', ni: null, np: 0, av: 0, law: 'German law' },
  { t: 'Joint development – acoustic damping', cp: 6, ty: TYP.rnd, en: ENT.deutschland, de: DEP.rnd, own: 'hen', ni: 340, np: 180, tv: 450_000, cur: 'EUR', term: 36, coc: true, law: 'German law', excl: 'Joint IP, field-of-use split' },
  { t: 'Testing and certification services', cp: 19, ty: TYP.indirect, en: ENT.dafa, de: DEP.quality, own: 'hen', resp: 'emm', rem: 'Responsible', ni: 110, np: 30, ar: 12, av: 240_000, term: 12, law: 'Danish law' },
  // Corporate and other
  { t: "Shareholders' agreement", cp: 'Shareholders of DAFA Group A/S', ty: TYP.corporate, en: ENT.group, de: DEP.management, own: 'kar', del: 'ras', ni: null, np: 0, av: 0, coc: true, law: 'Danish law' },
  { t: 'Marketing agency retainer', cp: 21, ty: TYP.other, en: ENT.dafa, de: DEP.marketing, own: 'kar', ni: 130, np: 30, ar: 12, av: 360_000, term: 12, law: 'Danish law' },
  // In flight
  { t: 'Pallet supply agreement', cp: 23, ty: TYP.indirect, en: ENT.dafa, de: DEP.purchasing, own: 'cam', st: 'Draft', ni: 700, np: 60, ar: 12, av: 310_000, term: 24, law: 'Danish law' },
  { t: 'Customs brokerage services', cp: 1, ty: TYP.logistics, en: ENT.dafa, de: DEP.logistics, own: 'tho', st: 'Draft', ni: 680, np: 90, av: 150_000, term: 24, law: 'Danish law' },
  { t: 'Stretch film supply – 2027', cp: 22, ty: TYP.direct, en: ENT.dafa, de: DEP.purchasing, own: 'jon', st: 'InApproval', ni: 330, np: 60, tv: 780_000, term: 12, law: 'Danish law',
    dev: [{ severity: 'Low', text: 'Payment terms 30 days net', standard: '60 days net' }] },
  { t: 'Raw material supply – silicone', cp: 17, ty: TYP.direct, en: ENT.building, de: DEP.purchasing, own: 'met', st: 'InApproval', ni: 1040, np: 90, ar: 12, av: 3_200_000, tv: 9_500_000, term: 36, law: 'Danish law',
    dev: [
      { severity: 'Medium', text: 'Minimum purchase commitment of 80% of forecast', standard: 'No commitment' },
      { severity: 'Low', text: 'Price review twice a year', standard: 'Annual' },
    ] },
  { t: 'Fire protection service agreement', cp: 14, ty: TYP.indirect, en: ENT.sverige, de: DEP.hse, own: 'ann', st: 'Approved', ni: 700, np: 30, ar: 12, av: 180_000, cur: 'SEK', term: 24, law: 'Swedish law' },
  { t: 'Courier services – Denmark', cp: 1, ty: TYP.logistics, en: ENT.dafa, de: DEP.logistics, own: 'tho', st: 'NoticeGiven', ni: -40, np: 90, ar: 12, av: 210_000, term: 24, dec: 'GiveNotice', law: 'Danish law' },
  // Ended
  { t: 'Website hosting (legacy)', cp: 2, ty: TYP.it, en: ENT.group, de: DEP.it, own: 'sor', st: 'Expired', ni: -290, np: 90, av: 95_000, term: 36, dec: 'LetExpire', law: 'Danish law' },
  { t: 'Trade fair stand rental 2025', cp: 21, ty: TYP.other, en: ENT.dafa, de: DEP.marketing, own: 'kar', st: 'Expired', ni: -400, np: 0, tv: 240_000, term: 6, law: 'Danish law' },
  { t: 'Truck lease – Horsens', cp: 11, ty: TYP.lease, en: ENT.dafa, de: DEP.logistics, own: 'tho', st: 'Terminated', ni: -180, np: 90, av: 330_000, term: 48, dec: 'GiveNotice', law: 'Danish law' },
];

function step(n: number, approver: UserKey, roleLabel: string, status: StepRec['status'], decidedAt: string | null, pendingSince: string | null, comment: string | null = null): StepRec {
  return { id: seedId(G.step, n), approverId: USER[approver], roleLabel, status, decidedAt, comment, pendingSince };
}

function seedApprovalRules(): ApprovalRule[] {
  const role = (r: NonNullable<ApprovalRule['stages'][number]['approvers'][number]['role']>) => ({ userId: null, role: r });
  const m = 1_000_000;
  return [
    { id: seedId(G.rule, 1), name: 'Up to DKK 1m', entityId: null, typeId: null, departmentId: null, minValue: null, maxValue: 1 * m, priority: 0, stages: [{ order: 1, approvers: [role('DepartmentHead')] }] },
    { id: seedId(G.rule, 2), name: 'DKK 1–5m', entityId: null, typeId: null, departmentId: null, minValue: 1 * m, maxValue: 5 * m, priority: 0, stages: [{ order: 1, approvers: [role('DepartmentHead')] }, { order: 2, approvers: [role('GroupCFO')] }] },
    { id: seedId(G.rule, 3), name: 'DKK 5–20m', entityId: null, typeId: null, departmentId: null, minValue: 5 * m, maxValue: 20 * m, priority: 0, stages: [{ order: 1, approvers: [role('DepartmentHead')] }, { order: 2, approvers: [role('Legal'), role('GroupCFO')] }, { order: 3, approvers: [role('GroupCEO')] }] },
    { id: seedId(G.rule, 4), name: 'Above DKK 20m', entityId: null, typeId: null, departmentId: null, minValue: 20 * m, maxValue: null, priority: 0, stages: [{ order: 1, approvers: [role('DepartmentHead'), role('EntityManager')] }, { order: 2, approvers: [role('Legal'), role('GroupCFO')] }, { order: 3, approvers: [role('GroupCEO')] }] },
    { id: seedId(G.rule, 5), name: 'NDA', entityId: null, typeId: typeId(TYP.nda), departmentId: null, minValue: null, maxValue: null, priority: 10, stages: [{ order: 1, approvers: [role('DepartmentHead')] }] },
    { id: seedId(G.rule, 6), name: 'Corporate contracts', entityId: null, typeId: typeId(TYP.corporate), departmentId: null, minValue: null, maxValue: null, priority: 20, stages: [{ order: 1, approvers: [role('Legal'), role('GroupCFO')] }, { order: 2, approvers: [role('GroupCEO')] }] },
  ];
}

const SUPPLY_TEMPLATE = `SUPPLY AGREEMENT

between {{entity}} ("DAFA") and {{counterparty}} ("the Supplier")

1. Scope. The Supplier supplies the products described in appendix 1 to DAFA: {{title}}.
2. Term. This agreement starts on {{startDate}} and runs until {{endDate}}.
3. Termination. Either party may terminate with {{noticePeriodDays}} days' written notice.
4. Prices and payment. Prices are stated in {{currency}}. Payment terms: {{paymentTerms}}.
5. Liability. Each party's liability is capped at 12 months of fees.
6. Governing law. {{governingLaw}}.

Signed for DAFA: ____________________    Signed for the Supplier: ____________________`;

const NDA_TEMPLATE = `MUTUAL NON-DISCLOSURE AGREEMENT

between {{entity}} and {{counterparty}}

1. Purpose. The parties wish to exchange confidential information for: {{title}}.
2. Confidentiality. Each party keeps the other party's information confidential and uses it only for the purpose.
3. Term. This agreement starts on {{startDate}} and runs until {{endDate}}. Confidentiality survives for five years.
4. Governing law. {{governingLaw}}.

Signed for DAFA: ____________________    Signed for the other party: ____________________`;

/** Builds the full demo database relative to `today` (ISO date). */
export function createSeed(today: string): Db {
  const entities = ENTITY_NAMES.map((name, i) => ({ id: entityId(i), name }));
  const departments = DEPARTMENT_NAMES.map((name, i) => ({ id: departmentId(i), name }));
  const contractTypes = TYPE_NAMES.map((name, i) => ({ id: typeId(i), name }));
  const users = seedUsers();
  const suppliers: Supplier[] = SUPPLIERS.map(([name, country], i) => ({
    id: seedId(G.supplier, i + 1),
    name,
    country,
    vatNo: `${country}${String(10_000_000 + i * 734_561).slice(0, 8)}`,
  }));

  const contracts: ContractRec[] = [];
  const documents: DocumentRec[] = [];
  const audit: AuditRec[] = [];
  let docN = 0;
  let auditN = 0;
  const log = (at: string, who: UserKey | null, action: string, contractId: Id | null, text: string) => {
    auditN += 1;
    audit.push({ id: seedId(G.audit, auditN), at: `${at}T09:00:00Z`, userId: who ? USER[who] : null, action, contractId, text });
  };
  const addDoc = (contractId: Id, name: string, kind: DocumentRec['kind'], version: number, by: UserKey, at: string, size: number) => {
    docN += 1;
    documents.push({ id: seedId(G.document, docN), contractId, name, kind, storeKey: `seed/${docN}`, version, size, uploadedBy: USER[by], uploadedAt: `${at}T10:00:00Z`, blob: null });
  };

  SPECS.forEach((s, i) => {
    const id = seedId(G.contract, i + 1);
    const supplier = typeof s.cp === 'number' ? suppliers[s.cp] : undefined;
    const endDate = s.ni !== null && s.np !== null ? addDays(today, s.ni + s.np) : s.endIn !== undefined ? addDays(today, s.endIn) : null;
    const startDate = endDate && s.term ? addDays(addMonths(endDate, -s.term), 1) : addDays(today, -(400 + i * 23));
    const status: ContractStatus = s.st ?? 'Active';
    const inFlight = status === 'Draft' || status === 'InApproval';
    const createdAt = inFlight ? addDays(today, -(6 + (i % 5))) : status === 'Approved' ? addDays(today, -14) : addDays(startDate, -45);
    const approvedAt = inFlight ? null : status === 'Approved' ? addDays(today, -3) : addDays(startDate, -20);
    const signedDate = inFlight || status === 'Approved' ? null : addDays(startDate, -15);
    const year = createdAt.slice(0, 4);
    const contractNo = s.no ?? `C-${year}-${String(100 + i * 7).padStart(4, '0')}`;
    const missingImport = s.np === null;

    contracts.push({
      id,
      contractNo,
      title: s.t,
      typeId: typeId(s.ty),
      supplierId: supplier?.id ?? null,
      counterpartyName: supplier?.name ?? String(s.cp),
      entityId: entityId(s.en),
      departmentId: departmentId(s.de),
      ownerId: USER[s.own],
      delegateId: s.del ? USER[s.del] : null,
      responsibleId: s.resp ? USER[s.resp] : null,
      reminderRecipient: s.rem ?? 'Owner',
      startDate,
      endDate,
      noticePeriodDays: s.np,
      autoRenew: (s.ar ?? 0) > 0,
      renewalMonths: s.ar ? s.ar : null,
      annualValue: s.av === undefined ? null : s.av,
      totalValue: s.tv === undefined ? null : s.tv,
      currency: s.cur ?? 'DKK',
      paymentTerms: s.pay ?? null,
      priceAdjustment: s.price ?? null,
      priceAdjustmentDate: s.price && endDate ? addMonths(startDate, 12 * Math.max(1, Math.ceil((s.term ?? 12) / 12) - 1)) : null,
      governingLaw: s.law ?? null,
      liabilityCap: s.cap ?? null,
      exclusivity: s.excl ?? null,
      changeOfControl: s.coc ?? false,
      summary: s.sum ?? null,
      status,
      signedDate: missingImport ? null : signedDate,
      signingMethod: signedDate && !missingImport ? 'Signed by both parties' : null,
      renewalDecision: s.dec ?? null,
      renewalDecidedBy: s.dec ? USER[s.own] : null,
      renewalDecidedAt: s.dec ? addDays(today, s.st === 'Expired' || s.st === 'Terminated' ? -300 : -9) : null,
      renewalComment: s.dec === 'Renegotiate' ? 'Benchmarking against two other providers before we commit.' : s.dec ? 'Agreed with the department head.' : null,
      createdAt,
      createdBy: USER[s.own],
      approvedAt,
      deviations: s.dev ?? [],
    });

    log(createdAt, s.own, 'ContractCreated', id, missingImport ? 'Contract imported from the old register' : 'Draft created');
    if (inFlight || status === 'Approved') {
      addDoc(id, `${s.t} – draft.pdf`, 'Main', 1, s.own, createdAt, 180_000 + i * 9_100);
    } else {
      // Only the flagship contract has its approval on record here (below); the rest were approved before they were imported.
      if (approvedAt) log(approvedAt, null, 'ApprovalCompleted', id, s.no ? 'Approved by all approvers' : 'Registered as approved (approval on file outside DAFA Contracts)');
      if (signedDate && !missingImport) log(signedDate, s.own, 'ContractSigned', id, 'Marked as signed by both parties');
      addDoc(id, `${s.t} – signed.pdf`, 'Main', 1 + (i % 3), s.own, signedDate ?? createdAt, 420_000 + i * 31_700);
      if (i % 4 === 1) addDoc(id, 'Appendix 1 – price list.pdf', 'Appendix', 1 + (i % 2), s.own, addDays(startDate, 30), 96_000 + i * 2_300);
    }
  });

  const cid = (title: string): Id => {
    const c = contracts.find((x) => x.title === title);
    if (!c) throw new Error(`Seed contract not found: ${title}`);
    return c.id;
  };

  // Extra documents on the flagship contract (as in the approved design).
  const rubber = cid('Raw material supply – rubber compounds');
  addDoc(rubber, 'Appendix 2 – price list 2026.pdf', 'Appendix', 5, 'jon', addDays(today, -323), 88_000);
  addDoc(rubber, 'Amendment 1 – volume commitment.pdf', 'Amendment', 1, 'met', addDays(today, -446), 132_000);
  addDoc(rubber, 'Approval log.pdf', 'ApprovalLog', 1, 'met', addDays(today, -1017), 24_000);
  log(addDays(today, -446), 'met', 'DocumentUploaded', rubber, 'Amendment 1 signed');
  log(addDays(today, -323), 'jon', 'DocumentUploaded', rubber, 'Appendix 2 – price list 2026 uploaded');
  const erp = cid('ERP software subscription');
  addDoc(erp, 'Order form and service levels.pdf', 'Appendix', 1, 'sor', addDays(today, -6), 310_000);

  const links: LinkRec[] = [
    { id: seedId(G.link, 1), fromId: cid('Amendment 1 – volume commitment'), toId: rubber, kind: 'Amendment' },
    { id: seedId(G.link, 2), fromId: rubber, toId: cid('Quality agreement – Nordpolymer'), kind: 'Related' },
    { id: seedId(G.link, 3), fromId: rubber, toId: cid('NDA – Nordpolymer'), kind: 'Related' },
    { id: seedId(G.link, 4), fromId: cid('Joint development – acoustic damping'), toId: cid('Mutual NDA – development project'), kind: 'Related' },
    { id: seedId(G.link, 5), fromId: cid('Courier services – Denmark'), toId: cid('Freight framework – Nordics'), kind: 'Framework' },
    { id: seedId(G.link, 6), fromId: cid('Raw material supply – silicone'), toId: cid('Foam raw materials – framework'), kind: 'Related' },
  ];

  let obN = 0;
  const ob = (contract: string, description: string, dueIn: number | null, frequencyMonths: number | null, resp: UserKey, done = false): ObligationRec => {
    obN += 1;
    return {
      id: seedId(G.obligation, obN),
      contractId: cid(contract),
      description,
      dueDate: dueIn === null ? null : addDays(today, dueIn),
      frequencyMonths,
      responsibleId: USER[resp],
      status: done ? 'Done' : 'Open',
      lastCompletedAt: done ? `${addDays(today, -20)}T12:00:00Z` : null,
    };
  };
  const obligations: ObligationRec[] = [
    ob('Raw material supply – rubber compounds', 'Report minimum annual volume against the commitment', 39, 12, 'jon'),
    ob('Raw material supply – rubber compounds', 'Price adjustment notice expected from the supplier', 9, 12, 'jon'),
    ob('Raw material supply – rubber compounds', 'Quality audit of the supplier', 190, 24, 'emm'),
    ob('Freight framework – Nordics', 'Annual price adjustment – freight', 40, 12, 'tho'),
    ob('Group insurance programme', 'Send the renewal questionnaire to the broker', 11, 12, 'ras'),
    ob('Banking and credit facilities', 'Send the covenant compliance certificate to the bank', 38, 3, 'ras'),
    ob('Banking and credit facilities', 'Deliver the audited annual report', 130, 12, 'ras'),
    ob('Warehouse lease – Jönköping', 'Annual fire safety inspection with the landlord', 75, 12, 'ann'),
    ob('IT support and hosting', 'Review the service level report', 6, 3, 'pet'),
    ob('Foam raw materials – framework', 'Share the rolling 12-month forecast', 21, 3, 'cam'),
    ob('Testing and certification services', 'Renew product certificates', 95, 12, 'emm'),
    ob('Energy supply', 'Report consumption for the green certificate', -3, 12, 'hen'),
    ob('Audit engagement – group', 'Sign the engagement letter for next year', null, null, 'ras', true),
  ];

  // Approvals in progress.
  const d = (n: number) => `${addDays(today, n)}T08:30:00Z`;
  const approvals: ApprovalRec[] = [
    {
      id: seedId(G.approval, 1), contractId: erp, status: 'Pending', currentStage: 2, startedAt: d(-5),
      stages: [
        { order: 1, steps: [step(1, 'pet', 'Department head (instead of Søren Dahl, who owns or submitted the contract)', 'Approved', d(-2), addDays(today, -5), 'Fits the IT roadmap and budget.')] },
        { order: 2, steps: [step(2, 'lou', 'Legal', 'Pending', null, addDays(today, -2)), step(3, 'ras', 'Group CFO', 'Pending', null, addDays(today, -2))] },
        { order: 3, steps: [step(4, 'kar', 'Group CEO', 'Waiting', null, null)] },
      ],
    },
    {
      id: seedId(G.approval, 2), contractId: cid('Stretch film supply – 2027'), status: 'Pending', currentStage: 1, startedAt: d(-1),
      stages: [{ order: 1, steps: [step(5, 'met', 'Department head', 'Pending', null, addDays(today, -1))] }],
    },
    {
      id: seedId(G.approval, 3), contractId: cid('Raw material supply – silicone'), status: 'Pending', currentStage: 3, startedAt: d(-9),
      stages: [
        { order: 1, steps: [step(6, 'pet', 'Department head (instead of Mette Kjær, who owns or submitted the contract)', 'Approved', d(-8), addDays(today, -9))] },
        { order: 2, steps: [step(7, 'lou', 'Legal', 'Approved', d(-5), addDays(today, -8), 'Commitment clause accepted after supplier added a force majeure carve-out.'), step(8, 'ras', 'Group CFO', 'Approved', d(-4), addDays(today, -8))] },
        { order: 3, steps: [step(9, 'kar', 'Group CEO', 'Pending', null, addDays(today, -4))] },
      ],
    },
    {
      id: seedId(G.approval, 4), contractId: cid('Fire protection service agreement'), status: 'Approved', currentStage: 1, startedAt: d(-10),
      stages: [{ order: 1, steps: [step(10, 'hen', 'Department head', 'Approved', d(-3), addDays(today, -10))] }],
    },
  ];
  // The flagship contract's finished approval, so its approval log and "Approved" date have a route behind them.
  const rubberRec = contracts.find((c) => c.id === rubber);
  if (rubberRec?.approvedAt) {
    const at = (n: number) => `${addDays(rubberRec.approvedAt ?? today, n)}T09:00:00Z`;
    approvals.push({
      id: seedId(G.approval, 5), contractId: rubber, status: 'Approved', currentStage: 3, startedAt: at(-6),
      stages: [
        { order: 1, steps: [step(11, 'pet', 'Department head (instead of Mette Kjær, who owns or submitted the contract)', 'Approved', at(-5), addDays(rubberRec.approvedAt, -6))] },
        { order: 2, steps: [step(12, 'lou', 'Legal', 'Approved', at(-3), addDays(rubberRec.approvedAt, -5)), step(13, 'ras', 'Group CFO', 'Approved', at(-2), addDays(rubberRec.approvedAt, -5))] },
        { order: 3, steps: [step(14, 'kar', 'Group CEO', 'Approved', at(0), addDays(rubberRec.approvedAt, -2))] },
      ],
    });
  }
  for (const a of approvals) {
    const submitted = contracts.find((c) => c.id === a.contractId);
    if (submitted) submitted.submittedBy = submitted.ownerId;
    const owner = USER_ORDER.find((k) => USER[k] === contracts.find((c) => c.id === a.contractId)?.ownerId) ?? null;
    log(a.startedAt.slice(0, 10), owner, 'ApprovalStarted', a.contractId, 'Submitted for approval');
    for (const st of a.stages.flatMap((x) => x.steps)) {
      if (st.status === 'Approved' && st.decidedAt) {
        const who = USER_ORDER.find((k) => USER[k] === st.approverId) ?? null;
        log(st.decidedAt.slice(0, 10), who, 'ApprovalDecision', a.contractId, `Approved as ${st.roleLabel}`);
      }
    }
  }
  addDoc(cid('Fire protection service agreement'), 'Approval log.pdf', 'ApprovalLog', 1, 'hen', addDays(today, -3), 21_000);

  const retentionYears = [5, 5, 5, 5, 10, 10, 5, 10, null, 5];
  const retentionReviewer = [DEP.purchasing, DEP.purchasing, DEP.logistics, DEP.it, DEP.finance, DEP.finance, DEP.management, DEP.rnd, DEP.management, DEP.finance];

  const recentFor = (titles: string[]) => titles.map(cid);

  return {
    entities,
    departments,
    contractTypes,
    currencies: ['DKK', 'EUR', 'SEK', 'USD', 'PLN', 'CNY', 'GBP', 'NOK'],
    users,
    suppliers,
    contracts,
    links,
    obligations,
    documents,
    approvals,
    approvalRules: seedApprovalRules(),
    reminders: [],
    reminderSettings: { noticeDays: [120, 60, 30], escalationDays: 14, obligationDays: 14 },
    retentionRules: contractTypes.map((t, i) => ({
      id: seedId(G.retention, i + 1),
      typeId: t.id,
      years: retentionYears[i] ?? 5,
      action: i === TYP.corporate ? 'Keep' : i === TYP.nda ? 'DeleteAutomatically' : 'ReviewThenDelete',
      reviewerDepartmentId: departmentId(retentionReviewer[i] ?? DEP.finance),
    })),
    templates: [
      { id: seedId(G.template, 1), name: 'Supply agreement', description: 'DAFA standard terms for buying materials and services.', typeId: typeId(TYP.direct), fileName: 'Supply agreement.docx', placeholders: ['title', 'entity', 'counterparty', 'startDate', 'endDate', 'noticePeriodDays', 'currency', 'paymentTerms', 'governingLaw'], body: SUPPLY_TEMPLATE },
      { id: seedId(G.template, 2), name: 'Mutual NDA', description: 'Two-way confidentiality agreement for early discussions.', typeId: typeId(TYP.nda), fileName: 'Mutual NDA.docx', placeholders: ['title', 'entity', 'counterparty', 'startDate', 'endDate', 'governingLaw'], body: NDA_TEMPLATE },
    ],
    audit,
    outbox: [],
    holidayCover: [
      { id: seedId(G.cover, 1), userId: USER.tho, deputyId: USER.hen, from: addDays(today, 20), to: addDays(today, 34) },
    ],
    uploads: [],
    roleHolders: { GroupCFO: USER.ras, GroupCEO: USER.kar, Legal: USER.lou },
    departmentHeads: {
      [departmentId(DEP.management)]: USER.kar,
      [departmentId(DEP.finance)]: USER.ras,
      [departmentId(DEP.purchasing)]: USER.met,
      [departmentId(DEP.sales)]: USER.kar,
      [departmentId(DEP.production)]: USER.hen,
      [departmentId(DEP.logistics)]: USER.tho,
      [departmentId(DEP.quality)]: USER.hen,
      [departmentId(DEP.rnd)]: USER.hen,
      [departmentId(DEP.maintenance)]: USER.hen,
      [departmentId(DEP.hse)]: USER.hen,
      [departmentId(DEP.it)]: USER.sor,
      [departmentId(DEP.hr)]: USER.kar,
      [departmentId(DEP.marketing)]: USER.kar,
    },
    recent: {
      [USER.ras]: recentFor(['Raw material supply – rubber compounds', 'Freight framework – Nordics', 'Warehouse lease – Jönköping']),
    },
    nextContractSeq: 400,
  };
}
