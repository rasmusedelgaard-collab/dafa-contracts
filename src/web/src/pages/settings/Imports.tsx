import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { qk, useApi } from '../../app/context';
import type { ImportKind, ImportMapping, ImportOptions, ImportPreview, ImportResult, ImportRowAction, ImportSession } from '../../api/types';
import { Button, Card, EmptyState, ErrorState, Field, Icon, Pill, Segmented, Select, SkeletonRows, Table, useToast } from '../../components';
import { formatDateTime } from '../../lib/dates';
import { plural, saveBlob } from '../../lib/format';
import { useInvalidateContracts } from '../../app/context';
import { useRefreshReference } from './Reference';
import { SectionHead } from './common';

const KINDS: { value: ImportKind; label: string }[] = [
  { value: 'counterparties', label: 'Counterparties' },
  { value: 'contracts', label: 'Contracts' },
  { value: 'users', label: 'Users' },
];
const KIND_TEXT: Record<ImportKind, string> = {
  counterparties: 'Suppliers and other counterparties. Rows are matched to existing records on external id, VAT number and name.',
  contracts: 'Existing contracts from a spreadsheet, as drafts or as active records that skip approval. Missing notice periods and values are allowed and show up as missing data. Rows are matched on the contract number.',
  users: 'People, their roles, departments and managed legal entities. Rows are matched on e-mail.',
};
const ACTION_TONE: Record<ImportRowAction, 'green' | 'blue' | 'gray' | 'red'> = { Create: 'green', Update: 'blue', Skip: 'gray', Error: 'red' };
const MODES: { value: ImportOptions['mode']; label: string }[] = [
  { value: 'SkipDuplicates', label: 'Create new, skip existing' },
  { value: 'CreateAndUpdate', label: 'Create new, update existing' },
  { value: 'CreateOnly', label: 'Create only (existing = error)' },
];

type Step = 'upload' | 'map' | 'preview' | 'done';

function Steps({ step }: { step: Step }) {
  const all: { key: Step; label: string }[] = [
    { key: 'upload', label: 'Upload' },
    { key: 'map', label: 'Map columns' },
    { key: 'preview', label: 'Check' },
    { key: 'done', label: 'Result' },
  ];
  const at = all.findIndex((s) => s.key === step);
  return (
    <ol className="wizard-steps" aria-label="Import steps">
      {all.map((s, i) => (
        <li key={s.key} data-state={i < at ? 'done' : i === at ? 'current' : 'upcoming'} aria-current={i === at ? 'step' : undefined}>
          <span className="wizard-steps__n">{i < at ? <Icon name="check" size={12} stroke={3} /> : i + 1}</span>
          {s.label}
        </li>
      ))}
    </ol>
  );
}

export function ImportWizard({ kind }: { kind: ImportKind }) {
  const api = useApi();
  const toast = useToast();
  const client = useQueryClient();
  const refresh = useRefreshReference();
  const invalidate = useInvalidateContracts();
  const fileRef = useRef<HTMLInputElement>(null);
  const [step, setStep] = useState<Step>('upload');
  const [session, setSession] = useState<ImportSession | null>(null);
  const [mapping, setMapping] = useState<ImportMapping>({});
  const [options, setOptions] = useState<ImportOptions>({ mode: 'SkipDuplicates', contractStatus: 'Active', createMissingCounterparties: false, dateFormat: 'auto', decimalSeparator: ',' });
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [rowFilter, setRowFilter] = useState<'all' | ImportRowAction>('all');
  const fields = useQuery({ queryKey: qk.admin('import-fields', kind), queryFn: () => api.admin.getImportFields(kind) });

  const reset = () => {
    setStep('upload');
    setSession(null);
    setPreview(null);
    setResult(null);
    setRowFilter('all');
  };
  const upload = useMutation({
    mutationFn: (file: File) => api.admin.uploadImport(kind, file),
    onSuccess: (s) => {
      setSession(s);
      setMapping(s.suggestedMapping);
      setStep('map');
      toast.show(`${plural(s.rowCount, 'row')} read from ${s.fileName}`);
    },
    onError: (e) => toast.error(e),
  });
  const template = useMutation({ mutationFn: () => api.admin.getImportTemplate(kind), onSuccess: (f) => saveBlob(f.blob, f.fileName), onError: (e) => toast.error(e) });
  const check = useMutation({
    mutationFn: () => api.admin.previewImport(session?.id ?? '', { mapping, options }),
    onSuccess: (p) => {
      setPreview(p);
      setRowFilter(p.error ? 'Error' : 'all');
      setStep('preview');
    },
    onError: (e) => toast.error(e),
  });
  const run = useMutation({
    mutationFn: () => api.admin.runImport(session?.id ?? '', { mapping, options }),
    onSuccess: async (r) => {
      setResult(r);
      setStep('done');
      await Promise.all([refresh(), invalidate(), client.invalidateQueries({ queryKey: qk.admin('import-history') })]);
      toast.show(`Import finished: ${r.created} created, ${r.updated} updated`);
    },
    onError: (e) => toast.error(e),
  });

  const visibleRows = (preview?.rows ?? []).filter((r) => rowFilter === 'all' || r.action === rowFilter);
  const mapped = Object.values(mapping).filter(Boolean).length;

  return (
    <Card pad className="stack import" style={{ gap: 18 }}>
      <Steps step={step} />
      {step === 'upload' ? (
        <div className="stack" style={{ gap: 14 }}>
          <p className="secondary prose">{KIND_TEXT[kind]}</p>
          <div
            className="import-drop"
            onDragOver={(e) => e.preventDefault()}
            onDrop={(e) => {
              e.preventDefault();
              const f = e.dataTransfer.files[0];
              if (f) upload.mutate(f);
            }}
          >
            <Icon name="upload" size={22} />
            <span>
              <strong>Drop a CSV or Excel file here</strong>
              <br />
              <span className="secondary">.csv or .xlsx, first row with column names, up to 5000 rows and 10 MB</span>
            </span>
            <input ref={fileRef} type="file" accept=".csv,.xlsx,.txt" className="sr-only" aria-label={`Choose a ${kind} file to import`} onChange={(e) => { const f = e.target.files?.[0]; if (f) upload.mutate(f); e.target.value = ''; }} />
            <Button loading={upload.isPending} onClick={() => fileRef.current?.click()}>
              Choose file
            </Button>
          </div>
          <div className="row-flex" style={{ gap: 12 }}>
            <Button variant="ghost" size="sm" icon="download" loading={template.isPending} onClick={() => template.mutate()}>
              Download a template
            </Button>
            <span className="caption">The template has every column with an example row.</span>
          </div>
        </div>
      ) : null}

      {step === 'map' && session ? (
        <div className="stack" style={{ gap: 16 }}>
          <p className="secondary">
            <strong>{session.fileName}</strong> · {plural(session.rowCount, 'row')} · {session.columns.length} columns. Match each field to a column of the file; columns were matched by name where possible.
          </p>
          {fields.isLoading ? (
            <SkeletonRows rows={6} />
          ) : fields.isError ? (
            <ErrorState text="The import fields could not be loaded." onRetry={() => void fields.refetch()} />
          ) : (
            <div className="mapping">
              {(fields.data ?? []).map((f) => {
                const col = mapping[f.key] ?? '';
                const idx = session.columns.indexOf(col);
                const example = idx >= 0 ? session.sample.map((r) => r[idx]).find((x) => x) : undefined;
                return (
                  <div key={f.key} className={`mapping__row${f.required && !col ? ' mapping__row--missing' : ''}`}>
                    <div className="mapping__field">
                      <strong>{f.label}</strong>
                      {f.required ? <Pill tone="amber" small>Required</Pill> : null}
                      {f.isKey ? <Pill tone="blue" small>Match key</Pill> : null}
                      {f.help ? <span className="caption">{f.help}</span> : null}
                    </div>
                    <Select aria-label={`Column for ${f.label}`} value={col} placeholder="Not imported" options={session.columns.map((c) => ({ value: c, label: c }))} onChange={(e) => setMapping({ ...mapping, [f.key]: e.target.value || null })} />
                    <span className="mapping__example caption" title={example}>
                      {example ? `e.g. ${example}` : col ? 'Empty in the first rows' : ''}
                    </span>
                  </div>
                );
              })}
            </div>
          )}
          <div className="form-grid form-grid--flat">
            <Field label="Existing records">{(p) => <Select {...p} value={options.mode} options={MODES} onChange={(e) => setOptions({ ...options, mode: e.target.value as ImportOptions['mode'] })} />}</Field>
            {kind === 'contracts' ? (
              <Field label="Create contracts as" hint="Active records skip approval; use this for contracts that are already signed.">
                {(p) => <Select {...p} value={options.contractStatus ?? 'Active'} options={[{ value: 'Active', label: 'Active contracts (already signed)' }, { value: 'Draft', label: 'Drafts' }]} onChange={(e) => setOptions({ ...options, contractStatus: e.target.value as 'Active' | 'Draft' })} />}
              </Field>
            ) : null}
            <Field label="Dates in the file">
              {(p) => (
                <Select
                  {...p}
                  value={options.dateFormat ?? 'auto'}
                  options={[
                    { value: 'auto', label: 'Detect (31-12-2027, 31.12.2027, 2027-12-31)' },
                    { value: 'dd-MM-yyyy', label: '31-12-2027' },
                    { value: 'dd.MM.yyyy', label: '31.12.2027' },
                    { value: 'dd/MM/yyyy', label: '31/12/2027' },
                    { value: 'yyyy-MM-dd', label: '2027-12-31' },
                    { value: 'MM/dd/yyyy', label: '12/31/2027 (US)' },
                  ]}
                  onChange={(e) => setOptions({ ...options, dateFormat: e.target.value as ImportOptions['dateFormat'] })}
                />
              )}
            </Field>
            <Field label="Decimal separator">
              {(p) => <Select {...p} value={options.decimalSeparator ?? ','} options={[{ value: ',', label: 'Comma (1.250.000,50)' }, { value: '.', label: 'Point (1,250,000.50)' }]} onChange={(e) => setOptions({ ...options, decimalSeparator: e.target.value as ',' | '.' })} />}
            </Field>
            {kind === 'contracts' ? (
              <label className="check form-grid__full">
                <input type="checkbox" checked={!!options.createMissingCounterparties} onChange={(e) => setOptions({ ...options, createMissingCounterparties: e.target.checked })} />
                Create counterparties that are not in the master data yet
              </label>
            ) : null}
          </div>
          <div className="row-flex" style={{ gap: 10 }}>
            <Button variant="secondary" onClick={reset}>
              Back
            </Button>
            <Button loading={check.isPending} disabled={mapped === 0} onClick={() => check.mutate()}>
              Check {plural(session.rowCount, 'row')}
            </Button>
          </div>
        </div>
      ) : null}

      {step === 'preview' && preview ? (
        <div className="stack" style={{ gap: 14 }}>
          {preview.mappingErrors.length ? (
            <div className="banner tone-red" role="alert">
              {preview.mappingErrors.join(' ')}
            </div>
          ) : null}
          <div className="kpis kpis--5">
            {(['Create', 'Update', 'Skip', 'Error'] as const).map((a) => (
              <button key={a} type="button" className={`kpi kpi--btn${rowFilter === a ? ' kpi--on' : ''}`} onClick={() => setRowFilter(rowFilter === a ? 'all' : a)}>
                <span className="kpi__label">{a === 'Create' ? 'New' : a === 'Update' ? 'Updated' : a === 'Skip' ? 'Skipped' : 'With errors'}</span>
                <span className={`kpi__value${a === 'Error' && preview.error ? ' fg-red' : ''}`}>{preview[a.toLowerCase() as 'create']}</span>
              </button>
            ))}
          </div>
          <Card clip className="import__rows">
            {visibleRows.length === 0 ? (
              <EmptyState compact title="No rows" />
            ) : (
              <Table
                caption="Rows to import"
                rows={visibleRows.slice(0, 300)}
                rowKey={(r) => String(r.rowNo)}
                columns={[
                  { key: 'n', header: 'Row', width: '64px', render: (r) => r.rowNo },
                  { key: 'a', header: 'Action', width: '104px', render: (r) => <Pill tone={ACTION_TONE[r.action]}>{r.action}</Pill> },
                  { key: 'v', header: 'Values', render: (r) => <span className="cell-clip" title={Object.values(r.values).join(' · ')}>{Object.values(r.values).slice(0, 4).join(' · ')}</span> },
                  {
                    key: 'm',
                    header: 'Messages',
                    render: (r) => (
                      <span className="stack" style={{ gap: 2 }}>
                        {r.errors.map((e, i) => (
                          <span key={i} className="fg-red" style={{ fontSize: 13 }}>
                            {e.message}
                          </span>
                        ))}
                        {r.warnings.map((w, i) => (
                          <span key={`w${i}`} className="caption">
                            {w}
                          </span>
                        ))}
                        {r.match && r.action !== 'Error' ? <span className="caption">Matches {r.match}</span> : null}
                      </span>
                    ),
                  },
                ]}
              />
            )}
            {visibleRows.length > 300 ? <p className="caption" style={{ padding: '8px 18px' }}>Showing the first 300 rows.</p> : null}
          </Card>
          <div className="row-flex" style={{ gap: 10 }}>
            <Button variant="secondary" onClick={() => setStep('map')}>
              Change mapping
            </Button>
            <Button loading={run.isPending} disabled={preview.create + preview.update === 0} onClick={() => run.mutate()}>
              Import {plural(preview.create + preview.update, 'row')}
            </Button>
            {preview.error ? <span className="caption">Rows with errors are not imported.</span> : null}
          </div>
        </div>
      ) : null}

      {step === 'done' && result ? (
        <div className="stack" style={{ gap: 14 }} role="status">
          <div className="banner tone-green">
            Import of <strong>{result.fileName}</strong> finished: {result.created} created, {result.updated} updated, {result.skipped} skipped, {result.failed} failed.
          </div>
          {result.errors.length ? (
            <Card clip>
              <Table caption="Rows that failed" rows={result.errors} rowKey={(r) => String(r.rowNo)} columns={[{ key: 'n', header: 'Row', width: '64px', render: (r) => r.rowNo }, { key: 'm', header: 'Error', render: (r) => r.message }]} />
            </Card>
          ) : null}
          <div>
            <Button onClick={reset}>Import another file</Button>
          </div>
        </div>
      ) : null}
    </Card>
  );
}

export function ImportHistory({ kind }: { kind?: ImportKind }) {
  const api = useApi();
  const q = useQuery({ queryKey: qk.admin('import-history', kind), queryFn: () => api.admin.getImportHistory(kind) });
  return (
    <Card clip>
      <div className="card-list-head">
        <h2 className="card-title">History</h2>
      </div>
      {q.isLoading ? (
        <SkeletonRows rows={3} />
      ) : q.isError ? (
        <ErrorState text="The import history could not be loaded." onRetry={() => void q.refetch()} />
      ) : (q.data ?? []).length === 0 ? (
        <EmptyState compact title="No imports yet" />
      ) : (
        <Table<ImportResult>
          caption="Import history" minWidth={760}
          rows={q.data ?? []}
          rowKey={(r) => r.id}
          columns={[
            { key: 'at', header: 'When', render: (r) => formatDateTime(r.ranAt) },
            { key: 'f', header: 'File or source', render: (r) => <span className="cell-clip" title={r.fileName}>{r.fileName}</span> },
            { key: 'k', header: 'What', render: (r) => KINDS.find((k) => k.value === r.kind)?.label ?? r.kind },
            { key: 'by', header: 'By', render: (r) => r.ranBy },
            { key: 'r', header: 'Result', render: (r) => `${r.created} new · ${r.updated} updated · ${r.skipped} skipped` },
            { key: 'e', header: 'Failed', render: (r) => (r.failed ? <Pill tone="red">{r.failed}</Pill> : <Pill tone="green">0</Pill>) },
          ]}
        />
      )}
    </Card>
  );
}

export function Imports() {
  const [params, setParams] = useSearchParams();
  const kind = (KINDS.find((k) => k.value === params.get('kind'))?.value ?? 'counterparties') as ImportKind;
  return (
    <>
      <SectionHead title="Imports" text="Upload a CSV or Excel file, match its columns to the fields, check every row, then import. The bulk contract import registers existing contracts without approval." />
      <Segmented ariaLabel="What to import" options={KINDS} value={kind} onChange={(v) => setParams({ kind: v }, { replace: true })} />
      <ImportWizard key={kind} kind={kind} />
      <ImportHistory />
    </>
  );
}
