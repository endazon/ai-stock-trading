import { useRiskStatus } from '@ai-stock-trading/lib/risk/queries';

// FR-12, SC-01, SC-02, SC-03, #334, #529:
// 現在の発注先を単独で取得する共有フック（発注先の表示・変更を持たない画面のバナー用）。
//
// IADR-0290 / MSP/ADR-0067 決定 5: 2 つ以上の画面が引く React フックであり、計画ツリーの
// shared 層 `hooks/` に置く。文言の定数は `src/lib/paperMode.ts` にある。

/**
 * FR-12, SC-01, #334: 現在の発注先を単独で取得する。
 *
 * SC-01 は発注先の表示も変更も持たないが、**`paper` 稼働中であることをどの画面からでも把握できる**よう
 * バナーだけは出す（05_screens SC-01）。取得できない場合は `null`（＝バナーを出さない）へ縮退し、
 * 本画面本来の機能（前提条件の閲覧・変更）を巻き込まない。
 *
 * IADR-0288: 取得は TanStack Query（`useRiskStatus`）に委ねる。**同じキーを購読する他の画面と
 * キャッシュを共有する**ため、1 画面が 2 つの発注先を見る状態を作れない。
 */
export function useBrokerProvider(): number | null {
  // 読み込み中・取得失敗はいずれも `undefined` であり、どちらもバナーを出さない（判らないことを
  // 断定に変えない）。別サービス（Risk）の取得不能を本画面の失敗にしない（領域独立の縮退）。
  const { data } = useRiskStatus();
  return data?.brokerProvider ?? null;
}
