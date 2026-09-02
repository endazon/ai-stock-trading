// FR-12, SC-01, SC-02, SC-03, INDEX 決定 46, #334, #529:
// 内蔵 `paper` 稼働中の警告バナーが用いる**文言の定数**。
//
// 定数をコンポーネント（`src/components/PaperModeBanner.tsx`）と同居させないのは、Fast Refresh の
// 制約（1 ファイル 1 種）に加えて、**必須文言が定数として 1 か所にある**ことをテストから直接
// 参照できるようにするためである。
//
// IADR-0290 / MSP/ADR-0066 決定 1: 3 画面すべてが引く語彙であり、feature ではなく shared 層（`lib/`）に置く。
// **React に依存しない値だけを置く**（同居していた `useBrokerProvider` は `src/hooks/` へ分けた）。

/** FR-12: 必須文言その1。外部へ発注していないこと。 */
export const PAPER_BANNER_DEBUG_MESSAGE = 'デバッグモードです。外部へ発注していません';

/** FR-12: 必須文言その2。この期間が Stage 1 の実績に算入されないこと。 */
export const PAPER_BANNER_EXCLUSION_MESSAGE = 'この期間は Stage 1 の実績に算入されません';

/** 05_screens: 統制状態のカード類に付す `paper` ラベル（例: `paper・参考値`）。 */
export const PAPER_REFERENCE_LABEL = 'paper・参考値';
