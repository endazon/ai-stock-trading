import { describe, expect, it } from 'vitest';
import {
  CONTRACT_RISK_SETTINGS,
  CONTRACT_RISK_STATUS,
  CONTRACT_SETTINGS_HISTORY,
  CONTRACT_SHORT_SELLING,
  CONTRACT_STAGE_GATE,
} from './contractFixtures';
import type { RiskLimitSettings, ShortSellingStatusView, StageSettings } from './contracts';
import {
  availabilityAmountText,
  availabilityCountText,
  availabilityRatioText,
  criterionLabel,
  equityAmountText,
  formatAmount,
  isNotSupplied,
  LIMIT_FIELD_KEYS,
  limitInputToWire,
  METRIC_AVAILABLE,
  METRIC_NOT_APPLICABLE,
  METRIC_NOT_APPLICABLE_TEXT,
  METRIC_NOT_SUPPLIED,
  METRIC_NOT_SUPPLIED_TEXT,
  positionSideLabel,
  ratioToPercentText,
  resolveEquityAmount,
  validateLimitInput,
  withdrawalReasonLabel,
  wireToLimitInput,
} from './contracts';

// FR-10, FR-20, SC-02, SC-03, #389, IADR-0146: **バックエンドの実応答に対する契約テスト。**
//
// ここで検証しているのは「フロントが自分で書いたモック」ではなく、バックエンドの xUnit が
// 実エンドポイントから採取した応答そのもの（`contract-fixtures/*.json`）である。
// 型の突合はコンパイル時（`contractFixtures.ts` の代入）が担うため、本ファイルは
//   - 画面が実際に読む値が **`undefined` にならない**こと（#389 の症状そのもの）
//   - 数値 enum → ラベル写像が実応答の値域を**取りこぼしていない**こと
//   - 旧い形（金額キー）が契約型を**満たさない**こと（`@ts-expect-error` によるコンパイル時の否定形）
// を固定する。
describe('リスク契約フィクスチャ（実応答）', () => {
  // ---- 正の確認: #389 の 3 キーが読める ----

  it('リスク上限は equity 比のキーで読め、計画（05_trading-assumptions §5）の既定値と一致する', () => {
    // FR-10, IADR-0130: 1 注文 25% / 1 日 150%。旧キーなら undefined になる（それが #389 の症状）。
    expect(CONTRACT_RISK_SETTINGS.limits.maxOrderAmountRatio).toBe(0.25);
    expect(CONTRACT_RISK_SETTINGS.limits.maxDailyOrderAmountRatio).toBe(1.5);
    // 比率であることの確認: 金額（数万〜数十万）ではあり得ない値域に収まる。
    expect(CONTRACT_RISK_SETTINGS.limits.maxOrderAmountRatio).toBeLessThan(1);
  });

  it('段階の発注可能額は総資金比のキーで読める', () => {
    // FR-20, IADR-0136: capitalCap（金額）ではなく capitalCapRatio（比率）。
    expect(CONTRACT_RISK_SETTINGS.stage.capitalCapRatio).toBeTypeOf('number');
    expect(CONTRACT_STAGE_GATE.currentSettings.capitalCapRatio).toBeTypeOf('number');
    expect(CONTRACT_RISK_SETTINGS.stage.capitalCapRatio).toBeLessThanOrEqual(1);
  });

  it('統制状態の上限は equity から解決済みの実額であり、設定の比率とは別物である', () => {
    // #389 で**改名してはいけない**側。SC-03 の上限表示・使用率表示・実弾切替モーダル③が使う。
    expect(CONTRACT_RISK_STATUS.maxOrderAmount).toBeGreaterThan(1);
    expect(CONTRACT_RISK_STATUS.maxOrderAmount).toBeCloseTo(
      CONTRACT_RISK_STATUS.capital * CONTRACT_RISK_SETTINGS.limits.maxOrderAmountRatio,
      5,
    );
    expect(CONTRACT_RISK_STATUS.maxDailyOrderAmount).toBeCloseTo(
      CONTRACT_RISK_STATUS.capital * CONTRACT_RISK_SETTINGS.limits.maxDailyOrderAmountRatio,
      5,
    );
  });

  it('画面が描画するキーがすべて実応答に存在する（undefined を描かない）', () => {
    // SC-03 の統制状態・段階ゲート・履歴のうち、画面が値として出す項目を実応答で確認する。
    for (const value of [
      CONTRACT_RISK_STATUS.activeControl,
      CONTRACT_RISK_STATUS.stage,
      CONTRACT_RISK_STATUS.brokerProvider,
      CONTRACT_RISK_STATUS.dailyOrderedAmount,
      CONTRACT_RISK_STATUS.maxDailyOrderAmount,
      CONTRACT_RISK_STATUS.drawdownRatio,
      CONTRACT_RISK_STATUS.maxDrawdownRatio,
      CONTRACT_RISK_STATUS.openPositionCount,
      CONTRACT_RISK_STATUS.maxOpenPositions,
      CONTRACT_STAGE_GATE.currentStage,
      CONTRACT_STAGE_GATE.currentSettings.mode,
      CONTRACT_STAGE_GATE.stage1Progress.qualifiedTradingDays,
      CONTRACT_STAGE_GATE.stage1Criteria.targetTradingDays,
    ]) {
      expect(value).toBeTypeOf('number');
    }
    expect(CONTRACT_STAGE_GATE.history.length).toBeGreaterThan(0);
    expect(CONTRACT_SETTINGS_HISTORY.length).toBeGreaterThan(0);
  });

  it('実応答に現れる数値 enum がラベル写像に存在する（不明(N) を出さない）', () => {
    // #389 実測: 既定応答の unmetCriteria は [9, 10] だが写像は 0〜8 しか無く、SC-03 は「不明(9)」を出していた。
    // ラベル写像の取りこぼしは型では捕まらないため、実応答の値域で押さえる。
    for (const criterion of CONTRACT_STAGE_GATE.promotion.unmetCriteria) {
      expect(criterionLabel(criterion)).not.toContain('不明');
    }
  });

  // ---- FR-10, SC-02, #362, IADR-0151: 実応答に接地した単位変換・値域・実額 ----

  it('実応答の比率が画面では百分率になり、往復してもワイヤの値が変わらない', () => {
    // IADR-0151 決定1: 画面は百分率、ワイヤは比率。**実応答の値**で往復を確かめる
    // （フロントが自分で選んだ都合のよい値ではなく、サーバが実際に返す値で確かめる）。
    expect(ratioToPercentText(CONTRACT_RISK_SETTINGS.limits.maxOrderAmountRatio)).toBe('25');
    expect(ratioToPercentText(CONTRACT_RISK_SETTINGS.limits.maxDailyOrderAmountRatio)).toBe('150');
    for (const key of LIMIT_FIELD_KEYS) {
      const wire = CONTRACT_RISK_SETTINGS.limits[key];
      expect(limitInputToWire(key, wireToLimitInput(key, wire))).toBe(wire);
    }
  });

  it('実応答の既定値は画面の値域をすべて満たす（保存し直せる）', () => {
    // 値域が実応答の既定値を弾いてしまうと、初期状態の設定を保存し直すことすらできない。
    for (const key of LIMIT_FIELD_KEYS) {
      expect(validateLimitInput(key, wireToLimitInput(key, CONTRACT_RISK_SETTINGS.limits[key]))).toBeNull();
    }
  });

  it('画面が計算する実額は、サーバが解決済みの実額と一致する', () => {
    // IADR-0151 決定4: 保存前の入力値に対する実額は画面が `capital × 比率` で計算する。
    // **同じ入力（＝現在の設定値）なら、サーバの解決結果と一致していなければならない**——
    // ずれていれば画面の実額併記が嘘になる（サーバの解決式と別物になっている）。
    expect(
      resolveEquityAmount(CONTRACT_RISK_STATUS.capital, CONTRACT_RISK_SETTINGS.limits.maxOrderAmountRatio),
    ).toBeCloseTo(CONTRACT_RISK_STATUS.maxOrderAmount, 5);
    expect(
      resolveEquityAmount(CONTRACT_RISK_STATUS.capital, CONTRACT_RISK_SETTINGS.limits.maxDailyOrderAmountRatio),
    ).toBeCloseTo(CONTRACT_RISK_STATUS.maxDailyOrderAmount, 5);
  });

  // ---- FR-10, SC-03, #340, IADR-0154: 空売りの現況（供給可否の宣言） ----

  it('実応答は維持率・借株料の累計・自動縮小の発動履歴を「未供給」として宣言している', () => {
    // **これが本機構の主眼である。** 供給元（ブローカー照会）が未実装であるという事実が、
    // 0 や空列ではなく `NotSupplied` として応答に現れることを実応答で固定する。
    // 供給が入って `Available` になれば本テストが落ちる——そのとき画面と blocked-tasks を追随させる。
    expect(CONTRACT_SHORT_SELLING.maintenanceMarginAvailability).toBe(METRIC_NOT_SUPPLIED);
    expect(CONTRACT_SHORT_SELLING.maintenanceMarginRatio).toBeNull();
    expect(CONTRACT_SHORT_SELLING.borrowFeeAvailability).toBe(METRIC_NOT_SUPPLIED);
    expect(CONTRACT_SHORT_SELLING.totalAccruedBorrowFeeUsd).toBeNull();
    expect(CONTRACT_SHORT_SELLING.reductionHistoryAvailability).toBe(METRIC_NOT_SUPPLIED);
  });

  it('実応答は設定値（維持率閾値・回復目標オフセット・空売り比率上限）を必ず載せている', () => {
    // 画面は閾値を直書きしない（Stage1GateCriteria と同じ方針）。供給が無くても設定値だけは要る。
    expect(CONTRACT_SHORT_SELLING.configuredMaintenanceMarginThreshold).toBe(0.4);
    expect(CONTRACT_SHORT_SELLING.maintenanceRecoveryTargetOffset).toBe(0.05);
    expect(CONTRACT_SHORT_SELLING.shortExposureRatioCap).toBe(0.5);
  });

  it('実応答の保有ポジションは方向を持ち、借株料は建玉単位でも未供給である', () => {
    // ADR-0016 決定15: 方向（ロング / ショート）は供給がある。借株料の累計は無い。
    const position = CONTRACT_SHORT_SELLING.positions[0];
    expect(position).toBeDefined();
    expect(positionSideLabel(position.side)).toBe('ショート');
    expect(position.borrowFeeAvailability).toBe(METRIC_NOT_SUPPLIED);
    expect(position.accruedBorrowFeeUsd).toBeNull();
  });

  it('未供給の文言そのものが「取得できていません」と述べている（— や 0 へ弱めない）', () => {
    // **定数の中身を固定する。** シンボル（`METRIC_NOT_SUPPLIED_TEXT`）越しにしか検証しないと、
    // 誰かが定数を `'—'` へ書き換えても全テストが緑のまま通る（画面は「値が無い」ではなく
    // 「値が空」に見えるようになり、#403 と同型の fail-open へ静かに戻る）。
    expect(METRIC_NOT_SUPPLIED_TEXT).toContain('取得できていません');
    expect(METRIC_NOT_SUPPLIED_TEXT).not.toBe('—');
    expect(METRIC_NOT_SUPPLIED_TEXT).not.toBe('0');
    expect(METRIC_NOT_SUPPLIED_TEXT).not.toBe('');
    // 「該当なし」（建玉なし）と同じ文言にしない（異常と正常が同じ見た目になる）。
    expect(METRIC_NOT_SUPPLIED_TEXT).not.toBe(METRIC_NOT_APPLICABLE_TEXT);
  });

  it('未供給の指標は表示文字列でも「取得できていません」になり、0 や — にならない', () => {
    // #403 と同型の fail-open を防ぐ核心。**値が無いことを値の不在として描く。**
    expect(availabilityRatioText(METRIC_NOT_SUPPLIED, null)).toBe(METRIC_NOT_SUPPLIED_TEXT);
    expect(availabilityAmountText(METRIC_NOT_SUPPLIED, null)).toBe(METRIC_NOT_SUPPLIED_TEXT);
    // 供給が無いのに値が入っていても、供給可否が優先される（サーバの宣言に従う）。
    expect(availabilityRatioText(METRIC_NOT_SUPPLIED, 0)).toBe(METRIC_NOT_SUPPLIED_TEXT);
    expect(availabilityAmountText(METRIC_NOT_SUPPLIED, 0)).toBe(METRIC_NOT_SUPPLIED_TEXT);
    // 「該当なし」（建玉なし）は異常ではないため未供給と同じ文言にしない。
    expect(availabilityRatioText(METRIC_NOT_APPLICABLE, null)).not.toBe(METRIC_NOT_SUPPLIED_TEXT);
    // 供給があるときだけ値を出す。
    expect(availabilityRatioText(METRIC_AVAILABLE, 0.4)).toBe('40.0%');
  });

  // ---- SC-01 / SC-02 / SC-03, #424, IADR-0162: 3 状態（未供給／対象なし／値が 0）の区別 ----

  it('実応答は強制買戻しの発生回数を「未供給」として宣言している（0 件と言わない）', () => {
    // ADR-0016 決定15・05_screens SC-03 の供給元の表: **「0 件と表示してはならない」**。
    // #419 で推定台帳は入ったが、台帳は推定が起きたときにしか行を書かないため、行数 0 は
    // 「観測が一度も届いていない」と「観測して 0 件だった」を区別できない。
    expect(CONTRACT_SHORT_SELLING.buyInCountAvailability).toBe(METRIC_NOT_SUPPLIED);
    expect(CONTRACT_SHORT_SELLING.buyInCount).toBeNull();
    expect(CONTRACT_SHORT_SELLING.buyInCount).not.toBe(0);
  });

  it('**正当な 0 は「0」として描かれる**（未供給へ倒さない・逆方向の否定形）', () => {
    // 05_screens「供給が無い値の表示規約」の 3 状態のうち **「値が 0」は正当な測定結果**である
    // （例: 当日の統制違反 0 件）。ここを未供給へ倒すと、供給されているのに「取得できていません」と
    // 嘘をつく——**「0 かどうかから供給有無を推測しない」は両方向に効く規律である。**
    expect(availabilityCountText(METRIC_AVAILABLE, 0)).toBe('0');
    expect(availabilityCountText(METRIC_AVAILABLE, 3)).toBe('3');
    expect(availabilityRatioText(METRIC_AVAILABLE, 0)).toBe('0.0%');
    expect(availabilityAmountText(METRIC_AVAILABLE, 0)).toBe('$0');
    // 0 が未供給・対象なしの文言へ化けないことを明示的に否定形で押さえる。
    for (const text of [
      availabilityCountText(METRIC_AVAILABLE, 0),
      availabilityRatioText(METRIC_AVAILABLE, 0),
      availabilityAmountText(METRIC_AVAILABLE, 0),
    ]) {
      expect(text).not.toBe(METRIC_NOT_SUPPLIED_TEXT);
      expect(text).not.toBe(METRIC_NOT_APPLICABLE_TEXT);
      expect(text).not.toBe('—');
    }
  });

  it('件数の未供給は「取得できていません」であり 0 でも — でもない', () => {
    expect(availabilityCountText(METRIC_NOT_SUPPLIED, null)).toBe(METRIC_NOT_SUPPLIED_TEXT);
    // 供給が無いのに値が入っていてもサーバの宣言が優先される（クライアントが推測しない）。
    expect(availabilityCountText(METRIC_NOT_SUPPLIED, 0)).toBe(METRIC_NOT_SUPPLIED_TEXT);
    expect(availabilityCountText(METRIC_NOT_SUPPLIED, 7)).toBe(METRIC_NOT_SUPPLIED_TEXT);
    expect(availabilityCountText(METRIC_NOT_SUPPLIED, null)).not.toBe('0');
    expect(availabilityCountText(METRIC_NOT_SUPPLIED, null)).not.toBe('—');
    // 対象なしは異常ではないため未供給と同じ文言にしない。
    expect(availabilityCountText(METRIC_NOT_APPLICABLE, null)).toBe(METRIC_NOT_APPLICABLE_TEXT);
    // 未知の供給可否は未供給へ倒す（値があるように見せない）。
    expect(availabilityCountText(99, 5)).toBe(METRIC_NOT_SUPPLIED_TEXT);
  });

  it('SC-02 の実額は equity が未供給なら「取得できていません」、入力が読めないだけなら「—」', () => {
    // #424, IADR-0162 決定3: **未供給を「—」で描かない。** 「—」は対象なし（実額が定義できない入力）専用。
    expect(equityAmountText(null, 0.25)).toBe(METRIC_NOT_SUPPLIED_TEXT);
    expect(equityAmountText(undefined, 0.25)).toBe(METRIC_NOT_SUPPLIED_TEXT);
    expect(equityAmountText(Number.NaN, 0.25)).toBe(METRIC_NOT_SUPPLIED_TEXT);
    expect(equityAmountText(null, 0.25)).not.toBe('—');
    expect(equityAmountText(CONTRACT_RISK_STATUS.capital, null)).toBe('—');
    // 供給があるなら実額を出す（実応答の equity と設定値で確かめる）。
    expect(equityAmountText(CONTRACT_RISK_STATUS.capital, CONTRACT_RISK_SETTINGS.limits.maxOrderAmountRatio))
      .toBe(formatAmount(CONTRACT_RISK_STATUS.maxOrderAmount));
    // **equity が 0 でも「未供給」ではない**（正当な 0 ＝ 実額も 0）。
    expect(equityAmountText(0, 0.25)).toBe('$0');
  });

  it('未知の供給可否は未供給へ倒れる（値があるように見せない）', () => {
    // サーバが将来メンバを追加したとき、画面が**値を持っているかのように**描かないことを固定する。
    expect(availabilityRatioText(99, 0.4)).toBe(METRIC_NOT_SUPPLIED_TEXT);
    expect(isNotSupplied(99)).toBe(true);
    expect(isNotSupplied(METRIC_AVAILABLE)).toBe(false);
    expect(isNotSupplied(METRIC_NOT_APPLICABLE)).toBe(false);
  });

  // ---- 否定形（写像・型が「効かない」方向に壊れていないこと） ----

  it('未知の enum 値は安全側フォールバックへ倒れる（写像を拡げすぎていない）', () => {
    // ラベル写像を「何でも返す」形に壊すと、上の正の確認が常に緑になり検査が死ぬ。
    expect(criterionLabel(9999)).toBe('不明(9999)');
    // WithdrawalReason の 1 は**欠番**（#333 で撤退事由から外され再利用しない）。誤ったラベルを出さない。
    expect(withdrawalReasonLabel(1)).toBe('不明(1)');
  });

  it('旧い金額キーの形は契約型を満たさない（コンパイル時の否定形）', () => {
    // #389 の退行そのもの。**この代入が通ってしまうなら @ts-expect-error が「エラー無し」として
    // `npm run typecheck` を落とす**——検査が効かなくなったことをコンパイラが教える。
    // 形だけを先に作る（`@ts-expect-error` は**直後の 1 行**にしか効かないため、代入の行で受ける）。
    const legacyLimitsShape = {
      maxOrderAmount: 100000,
      maxDailyOrderAmount: 300000,
      maxOpenPositions: 3,
      dailyLossLimitRatio: 0.02,
      perTradeRiskRatio: 0.01,
      maxDrawdownRatio: 0.1,
      losingStreakThreshold: 5,
      losingStreakSizeFactor: 0.5,
    };
    // @ts-expect-error 旧 API の形（金額キー）は equity 比の契約型を満たさない
    const legacyLimits: RiskLimitSettings = legacyLimitsShape;

    const legacyStageShape = { stage: 2, mode: 1, capitalCap: 1000000 };
    // @ts-expect-error 旧 API の形（capitalCap＝金額）は総資金比の契約型を満たさない
    const legacyStage: StageSettings = legacyStageShape;

    const retypedShape = { ...CONTRACT_RISK_SETTINGS.limits, maxOrderAmountRatio: '0.25' };
    // @ts-expect-error 比率キーに文字列は入らない（型変更も検出する）
    const retypedLimits: RiskLimitSettings = retypedShape;

    // #340, IADR-0154: **供給可否を落とした形は契約型を満たさない。**
    // 「値だけ運べばよい」に戻すと、未供給と 0 の区別が型の上から消える（#403 と同型の穴が復活する）。
    const withoutAvailabilityShape = (() => {
      const clone: Record<string, unknown> = { ...CONTRACT_SHORT_SELLING };
      delete clone.maintenanceMarginAvailability;
      return clone as Omit<ShortSellingStatusView, 'maintenanceMarginAvailability'>;
    })();
    // @ts-expect-error 供給可否（maintenanceMarginAvailability）を欠いた形は契約型を満たさない
    const withoutAvailability: ShortSellingStatusView = withoutAvailabilityShape;
    expect(
      (withoutAvailability as unknown as Record<string, unknown>).maintenanceMarginAvailability,
    ).toBeUndefined();

    // 実行時には「旧キーでは新しいキーが読めない」ことだけを確かめる（#389 の症状）。
    expect((legacyLimits as unknown as Record<string, unknown>).maxOrderAmountRatio).toBeUndefined();
    expect((legacyStage as unknown as Record<string, unknown>).capitalCapRatio).toBeUndefined();
    expect(retypedLimits.maxOrderAmountRatio).toBe('0.25');
  });
});
