import { useRiskStatus } from '../risk/queries';

// FR-12, SC-01, SC-02, SC-03, INDEX 決定 46, #334:
// 内蔵 `paper` 稼働中の警告バナーが用いる**文言の定数**と、発注先の単独取得フック。
// 定数とフックをコンポーネントと同居させないのは、Fast Refresh の制約（1 ファイル 1 種）に加えて、
// **必須文言が定数として 1 か所にある**ことをテストから直接参照できるようにするためである。

/** FR-12: 必須文言その1。外部へ発注していないこと。 */
export const PAPER_BANNER_DEBUG_MESSAGE = 'デバッグモードです。外部へ発注していません';

/** FR-12: 必須文言その2。この期間が Stage 1 の実績に算入されないこと。 */
export const PAPER_BANNER_EXCLUSION_MESSAGE = 'この期間は Stage 1 の実績に算入されません';

/** 05_screens: 統制状態のカード類に付す `paper` ラベル（例: `paper・参考値`）。 */
export const PAPER_REFERENCE_LABEL = 'paper・参考値';

/**
 * FR-12, SC-01, #334: 現在の発注先を単独で取得する（発注先の表示・変更を持たない画面のバナー用）。
 *
 * SC-01 は発注先の表示も変更も持たないが、**`paper` 稼働中であることをどの画面からでも把握できる**よう
 * バナーだけは出す（05_screens SC-01）。取得できない場合は `null`（＝バナーを出さない）へ縮退し、
 * 本画面本来の機能（前提条件の閲覧・変更）を巻き込まない。
 *
 * IADR-0286: 取得は TanStack Query（`useRiskStatus`）に委ねる。**同じキーを購読する他の画面と
 * キャッシュを共有する**ため、1 画面が 2 つの発注先を見る状態を作れない。
 */
export function useBrokerProvider(): number | null {
  // 読み込み中・取得失敗はいずれも `undefined` であり、どちらもバナーを出さない（判らないことを
  // 断定に変えない）。別サービス（Risk）の取得不能を本画面の失敗にしない（領域独立の縮退）。
  const { data } = useRiskStatus();
  return data?.brokerProvider ?? null;
}
