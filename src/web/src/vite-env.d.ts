/// <reference types="vite/client" />
declare const __API_MODE__: string;
/** Build time (UTC) and API mode, shown in the sign-in diagnostics so a stale cached bundle can be told apart. */
declare const __BUILD_ID__: string;

interface ImportMetaEnv {
  /** Mock only: when set, the mock asks for this access code before the demo-user picker. */
  readonly VITE_MOCK_ACCESS_CODE?: string;
}
interface ImportMeta {
  readonly env: ImportMetaEnv;
}
