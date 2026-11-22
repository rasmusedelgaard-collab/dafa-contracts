/// <reference types="vite/client" />
declare const __API_MODE__: string;

interface ImportMetaEnv {
  /** Mock only: when set, the mock asks for this access code before the demo-user picker. */
  readonly VITE_MOCK_ACCESS_CODE?: string;
}
interface ImportMeta {
  readonly env: ImportMetaEnv;
}
