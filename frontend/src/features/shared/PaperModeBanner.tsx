import { isInternalPaper } from '../risk/contracts';
import { PAPER_BANNER_DEBUG_MESSAGE, PAPER_BANNER_EXCLUSION_MESSAGE } from './paperMode';

// FR-12, SC-01, SC-02, SC-03, INDEX 決定 46, #334:
// 内蔵 `paper`（擬似約定・外部へ発注しない）で稼働している間、**全画面（SC-01 / SC-02 / SC-03）の上部に
// 常時表示する**警告バナー。
//
// 計画（05_screens「運用段階（Stage）と発注先（Broker Provider）の表示規約（共通）」・FR-12）は
// 文言に 2 点を**必ず含める**ことを求める。数字だけが独り歩きすると、擬似約定の成績を実績と
// 取り違えるためである。
//
// 注記（計画本文の明示的な警告）: バナーの見た目は SC-02 のモックアップの「状態例」区画にのみ描かれており、
// SC-01・SC-03 のモックアップ本体には描かれていない。**モックアップの見た目だけを頼りにすると実装を落とす。**

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
    <div role="alert" aria-label="内蔵 paper 稼働中の警告">
      <p>
        <strong>{PAPER_BANNER_DEBUG_MESSAGE}</strong>
      </p>
      <p>{PAPER_BANNER_EXCLUSION_MESSAGE}</p>
    </div>
  );
}
