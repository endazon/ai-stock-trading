// SC-04, FR-09, FR-11, UC-06（代替フロー「ゲートウェイの有人認証」）, NFR-05, NFR-06,
// ADR-0002 前提条件1, ADR-0024 決定1・2, IADR-0321, planning#594:
// OpenD 認証操作画面（BFF `/bff/opend-auth/*`）の型。
//
// MSP/ADR-0031 §ディレクトリ構成: `api/` と `components/` の双方が要る型は `types/` に置く。

/**
 * ゲートウェイ状態の宣言（`GET /bff/opend-auth/state`）。
 *
 * 🔴 **本契約が運ぶ主役は値ではなく「値が供給されているか」である**（SC-03 の
 * `ShortSellingStatusView` と同じ性質）。**供給可否はサーバが宣言し、画面は従う** ——
 * 画面が「値が無いから未供給だろう」と推測する形にすると、
 * **「いま入力を待っていない（正常）」と「状態を取得できていない（ゲートウェイ不達）」が
 * 区別できなくなる。** 取り違えると、壊れたゲートウェイが正常に見える。
 */
export interface OpendAuthStateView {
  /** 待機中プロンプトの供給可否（`MetricAvailability` の序数）。 */
  promptAvailability: number;
  /** `phone`（SMS）/ `pic`（画像 CAPTCHA）。待機中でなければ `null`。 */
  prompt: string | null;
  /** `waiting` / `idle` / `unavailable`。 */
  connection: string;
  /** 画像 CAPTCHA を取得できるか。 */
  captchaAvailable: boolean;
  lastLoginAtAvailability: number;
  lastLoginAt: string | null;
  /** ADR-0024 決定1 の条件 (1)。**配備構成（PVC）から静的に決まる。** */
  deviceTrustAvailability: number;
  deviceTrustPersisted: boolean | null;
  /** ADR-0024 決定1 の条件 (2)。**配備構成（固定 NAT）から静的に決まる。** Pod IP は無関係である。 */
  egressStabilityAvailability: number;
  egressStable: boolean | null;
  /** 補足（サーバ由来の説明）。**検証コードは決して載らない。** */
  detail: string | null;
}

/**
 * 検証コードの投入本文（`POST /bff/opend-auth/code`）。
 *
 * 🔴 **コードだけである。** `command` / `kind` に相当する欄を**足さないこと** ——
 * 送信するコマンドは**サーバが待機中のプロンプトから決める**のであって、画面は選べない
 * （計画 05_screens SC-04「画面はコマンド名を持たない」）。
 */
export interface CodeSubmission {
  code: string;
}

/** 待機中プロンプトの種別。 */
export const PROMPT_PHONE = 'phone';
export const PROMPT_PIC = 'pic';

/** 接続状態。 */
export const CONNECTION_WAITING = 'waiting';
export const CONNECTION_IDLE = 'idle';
export const CONNECTION_UNAVAILABLE = 'unavailable';

/**
 * SC-04: 待機中プロンプトの表示ラベル。
 * 未知の種別は「不明」へ倒す（**種別を騙らない**）。
 */
export function promptLabel(prompt: string | null): string {
  if (prompt === PROMPT_PHONE) return 'SMS 検証コード';
  if (prompt === PROMPT_PIC) return '画像 CAPTCHA';
  return '不明';
}

/**
 * SC-04: 接続状態の表示ラベル。
 *
 * 🔴 **`unavailable` を「ログイン済み」と読める語にしない。** ここが本画面で最も高くつく
 * 取り違えである（到達できていないだけなのに、正常に見える）。
 */
export function connectionLabel(connection: string): string {
  if (connection === CONNECTION_WAITING) return '検証コード待ち';
  if (connection === CONNECTION_IDLE) return 'ログイン済み — API 稼働中';
  return '取得できていません（供給元がありません）';
}

/**
 * SC-04: 入力欄・送信を有効にしてよいか。
 *
 * **待機中（供給あり かつ プロンプト種別が判っている）ときだけ**である。
 * 供給が無い・対象なしのいずれでも無効にする（計画 05_screens SC-04
 * 「待機中でないときは入力欄と送信を無効にする」）。
 *
 * 🔴 **これは画面の親切さにすぎない。** サーバ側でも 409 で拒否される
 * （統制を画面に紐づけない・「設定変更の一般則」と同型）。
 */
export function canSubmitCode(state: OpendAuthStateView | null): boolean {
  if (state === null) return false;
  return state.prompt === PROMPT_PHONE || state.prompt === PROMPT_PIC;
}

/**
 * SC-04: SMS 検証コードの形式（6 桁の数字）。
 * 画像 CAPTCHA は桁数・文字種が定まらないため、空でないことだけを見る。
 */
export function isValidCodeFormat(prompt: string | null, code: string): boolean {
  const trimmed = code.trim();
  if (trimmed.length === 0) return false;
  if (prompt === PROMPT_PHONE) return /^\d{6}$/.test(trimmed);
  return true;
}

/**
 * SC-04: 画面から送れる操作の一覧（**許可リスト**。利用者への情報として提示する）。
 *
 * 🔴 **上表は「現時点で確認できたコマンドの一覧」ではなく「送ってよいコマンドの一覧」である**
 * （計画 05_screens SC-04）。未知のコマンドが増えても既定は拒否である。
 *
 * 🔴 **この配列は表示専用であり、送信経路ではない。** ここへ 1 行足しても送れるようにはならない
 * （送れるかどうかを決めるのはサーバ側の許可リストである）。**逆に、ここから消しても送れてしまう
 * わけではない** —— 画面が親切であることに統制を依存させない。
 */
export const ALLOWED_OPERATIONS: readonly {
  command: string;
  purpose: string;
  allowed: boolean;
  note: string;
}[] = [
  {
    command: 'input_phone_verify_code',
    purpose: 'SMS 検証コードの入力',
    allowed: true,
    note: '待機中プロンプトが SMS のときだけ',
  },
  {
    command: 'input_pic_verify_code',
    purpose: '画像 CAPTCHA の入力',
    allowed: true,
    note: '待機中プロンプトが画像のときだけ',
  },
  {
    command: 'req_phone_verify_code',
    purpose: 'SMS の再送要求',
    allowed: true,
    note: 'レート制限（暫定 60 秒に 1 回・実測待ち）',
  },
  {
    command: 'relogin -login_pwd= ／ exit ／ close_api_conn ／ set_log_level',
    purpose: '再ログイン・終了・接続切断・ログ水準変更',
    allowed: false,
    note: '統制の外から OpenD を止められてしまう',
  },
  {
    command: 'show_delay_report -detail_report_path= ／ show_sub_info -sub_info_path=',
    purpose: 'レポート出力（任意パス指定）',
    allowed: false,
    note: 'root 権限で任意パスへ書ける — デバイス信頼の実体や OpenD.xml を潰せる',
  },
];
