// NFR / MSP/IADR-0569・IADR-0624 / IADR-0588（#1416 / E-33）: AST の live 層の opt-in と到達先・storageState の置き場（純関数）。
//
// - AST は**自分ではログインしない**。MSP の live の setup（MSP `src/platform/frontend/e2e/live/auth.setup.ts`）が試験アカウントごとに作った
//   storageState（BFF のセッション cookie）を `E2E_AUTH_DIR` から読む（MSP#2013 コメント 3 §2.3）。ファイル名は MSP の
//   `stateFile(authDir, role)` と同じ `<authDir>/<role>.json`（取引オーナーは `trading-owner.json`）。
// - `E2E_LIVE`: 未設定・空は opt-out（全 project を 0 件・exit 0）。ちょうど `1` だけが live、それ以外は例外（黙って 0 件の緑にしない）。
// - `E2E_LAYER`: `ro` / `trading` のカンマ区切り。未設定は `ro`。値域外、`E2E_LIVE=1` 無しの指定は例外。
// - `E2E_AUTH_DIR`: live のときは**必須**（AST の単独のチェックアウトから MSP の置き場は推測できない）。無ければ例外。
// - 資格情報（パスワード・TOTP）は AST では一切読まない（ログインしないため）。

const LAYERS = ['ro', 'trading'] as const;
export type Layer = (typeof LAYERS)[number];

/** MSP の試験アカウントの役割（realm `platform` の `e2e-<role>`。MSP/IADR-0570）。AST の spec が使うのは trading-owner と user。 */
export const ROLES = [
  'user',
  'operator',
  'admin',
  'noaccess',
  'revocable',
  'trading-owner',
] as const;
export type Role = (typeof ROLES)[number];

/** AST の画面に入るのに要るロール（MSP の BFF の `/bff/auth/me` が返す realm ロール）。 */
export const OWNER_ROLE = 'trading-owner';

const DEFAULT_EDGE_URL = 'https://localhost';

type Env = Record<string, string | undefined>;

/** live の config が読む環境変数（**空は未設定と同じ**）。 */
export const OPTION_NAMES = [
  'E2E_LIVE',
  'E2E_LAYER',
  'E2E_EDGE_URL',
  'E2E_INSECURE_TLS',
  'E2E_AUTH_DIR',
] as const;

export interface LiveEnv {
  live: boolean;
  layers: Layer[];
  edgeUrl: string;
  insecureTls: boolean;
  /** MSP の setup が書いた storageState の置き場。opt-out のときは空。 */
  authDir: string;
}

function originOf(name: string, value: string): string {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error(`${name} が URL ではない: ${value}`);
  }
  if (url.pathname !== '/' || url.search || url.hash) {
    throw new Error(`${name} はオリジンだけを書く（パス・クエリを付けない）: ${value}`);
  }
  return url.origin;
}

export function parseLiveEnv(env: Env): LiveEnv {
  const read = (name: (typeof OPTION_NAMES)[number]): string | undefined => {
    const v = env[name];
    return v === undefined || v === '' ? undefined : v;
  };
  const rawLive = read('E2E_LIVE') ?? '';
  if (rawLive !== '' && rawLive !== '1') {
    throw new Error(`E2E_LIVE の値域外: ${rawLive}（live にするなら 1、しないなら未設定か空）`);
  }
  const live = rawLive === '1';
  const rawLayer = read('E2E_LAYER');
  if (rawLayer !== undefined && !live) {
    throw new Error('E2E_LAYER が設定されているのに E2E_LIVE=1 が無い（live 層は明示の opt-in でだけ流す）');
  }
  const layers = (rawLayer ?? 'ro')
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s !== '');
  if (layers.length === 0) throw new Error('E2E_LAYER が空である（ro / trading のカンマ区切り）');
  for (const layer of layers) {
    if (!(LAYERS as readonly string[]).includes(layer)) {
      throw new Error(`E2E_LAYER の値域外: ${layer}（ro / trading のカンマ区切り）`);
    }
  }
  const authDir = read('E2E_AUTH_DIR') ?? '';
  if (live && authDir === '') {
    throw new Error(
      'E2E_LIVE=1 なのに E2E_AUTH_DIR が無い（MSP の live の setup が storageState を書いた置き場を渡す。AST は自分ではログインしない）',
    );
  }
  return {
    live,
    layers: [...new Set(layers)] as Layer[],
    edgeUrl: originOf('E2E_EDGE_URL', read('E2E_EDGE_URL') ?? DEFAULT_EDGE_URL),
    insecureTls: read('E2E_INSECURE_TLS') === '1',
    authDir,
  };
}

/** 役割の storageState のファイル（MSP の `stateFile` と同じ `<authDir>/<role>.json`）。 */
export function stateFile(authDir: string, role: Role): string {
  return `${authDir.replace(/\/+$/, '')}/${role}.json`;
}
