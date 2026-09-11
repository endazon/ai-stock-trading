import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Alert } from '@platform/ui';
import { isInternalPaper } from '@ai-stock-trading/lib/risk/contracts';
import { PAPER_BANNER_DEBUG_MESSAGE, PAPER_BANNER_EXCLUSION_MESSAGE } from '@ai-stock-trading/lib/paperMode';

// FR-12, SC-01, SC-02, SC-03, SC-04, INDEX 決定 46, #334, planning#594:
// 内蔵 `paper`（擬似約定・外部へ発注しない）で稼働している間、**全画面（SC-01 / SC-02 / SC-03 / SC-04）の
// 上部に常時表示する**警告バナー（SC-04 は 2026-09-09 に対象へ加わった。内蔵 `paper` の稼働中は
// OpenD を経由しないため認証操作そのものが不要であり、バナーが無いと「認証できないから発注できない」と
// 誤読される）。
//
// 計画（05_screens「運用段階（Stage）と発注先（Broker Provider）の表示規約（共通）」・FR-12）は
// 文言に 2 点を**必ず含める**ことを求める。数字だけが独り歩きすると、擬似約定の成績を実績と
// 取り違えるためである。
//
// 注記（計画本文の明示的な警告）: バナーの見た目は SC-02 のモックアップの「状態例」区画にのみ描かれており、
// SC-01・SC-03・SC-04 のモックアップ本体には描かれていない。**モックアップの見た目だけを頼りにすると実装を落とす。**
//
// UI/UX 改善 2026-09-12: 見た目を `@platform/ui` の `Alert`（色 ＋ アイコン ＋ ラベルの 3 点セット。
// INDEX 決定 21）へ載せ替えた。モックの `.note`（err 配色）に相当する。
//
// 🔴 **本バナーは `role="alert"` を保つ。** 「静的な注記は `Note`」の一般則の例外である——
// 出る条件が「内蔵 paper で稼働している」という**運用状態の通知**であり、画面を開いた利用者へ
// 割り込んで伝える必要がある（外部へ発注していないことを知らずに成績を読むのが最も高くつく誤りである）。

/**
 * 発注先が内蔵 `paper` のときだけバナーを描く。それ以外（不明を含む）は何も描かない。
 *
 * **発注先が判らない場合にバナーを出さない**のは、出すと「外部へ発注していません」という**事実でない断定**を
 * 画面が行うことになるためである（moomoo SIMULATE / REAL で稼働中に誤って出せば、実弾稼働を
 * デバッグ稼働と誤認させる）。判らないことを断定に変えない。
 */
export function PaperModeBanner({ provider }: { provider: number | null | undefined }) {
  if (!isInternalPaper(provider)) {
    return null;
  }
  return (
    <Alert
      tone="danger"
      role="alert"
      aria-label={i18n._(msg`内蔵 paper 稼働中の警告`)}
      label={i18n._(msg`デバッグ稼働`)}
      className="mb-3"
    >
      <strong>{PAPER_BANNER_DEBUG_MESSAGE}</strong>
      <span className="ml-1">{PAPER_BANNER_EXCLUSION_MESSAGE}</span>
    </Alert>
  );
}
