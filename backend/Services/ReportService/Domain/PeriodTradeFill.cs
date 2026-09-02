using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-16: 損益集計の入力となる 1 約定。取引台帳（#63）の約定に対応する（実データ源の連携は #22 後続）。
// Quantity は約定数量（>0）、Price は約定単価（基準通貨〔USD〕へ換算済みの参照価格）。
// PositionEffect は集計ロジックでは未使用（在庫は Side の符号で増減判定する）が、台帳の LedgerFill との整合・監査・
// 将来の両建て（別ロット）会計のため保持する（IADR-0018/0025 と同方針）。
// FR-06, FR-16, #563, IADR-0269: DecisionId は取引判断（監査台帳の TradeDecisionMade）と突き合わせる相関キー。
// 日報 §2「判断根拠（要約）」は**記録済みの根拠をこの鍵で引いてそのまま提示する**（LLM に書かせない）。
// 既定 `default`（Guid.Empty）＝相関できない（供給元が鍵を運ばない・レガシー行）＝判断根拠は未供給。
public sealed record PeriodTradeFill(
    string Symbol,
    Market Market,
    TradeSide Side,
    PositionEffect PositionEffect,
    int Quantity,
    decimal Price,
    DateTimeOffset ExecutedAt,
    Guid DecisionId = default,
    // FR-06, FR-15, FR-20, #569, IADR-0149 決定1, IADR-0271: **実際に発注したアダプタの発注先**。
    // 月報 §5 の三者比較が SIMULATE 列と実弾列を分ける鍵である（承認 Intent の Mode では代用できない
    // ——実行アダプタの発注先は構成から解決され、段階が定める既定とは独立に決まる）。
    // 既定 `null`＝**発注先不明**（供給元が運ばない・列追加前のレガシー行）＝**どちらの段にも算入しない**。
    // 🔴 **推定で埋めない**——寄せた先の列の実績が水増しされ、比較の意味が壊れる。
    BrokerProvider? Provider = null,
    // FR-06, FR-16, #611, IADR-0282 決定1: 承認時点の**認識時レート**（基準通貨〔USD〕1 単位あたりの表示通貨〔JPY〕額
    // ＝1 USD あたりの円）。為替差損益（FxTranslationBuilder）が USD 建て約定を円へ再測定する起点。
    // 既定 `null`＝**未記録**（供給元が運ばない・列追加前の行・承認時に為替レート源が解決できなかった行）。
    // 🔴 **推定で埋めない**——未記録の対象約定を含む期間は節ごと未供給とし、件数を明記する。
    decimal? FxRateBaseToDisplay = null);
