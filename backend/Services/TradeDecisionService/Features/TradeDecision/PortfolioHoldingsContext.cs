namespace TradeDecisionService.Features.TradeDecision;

/// <summary>
/// FR-04, FR-10, #1397, IADR-0562 決定 3: 判断のプロンプトへ渡す「保有中の全銘柄（銘柄・方向・数量）」の材料。
/// <paramref name="Holdings"/> が null なら照会できなかった（プロンプトは「不明」と書く）。空の一覧は保有なし。
/// 方針の銘柄の組み合わせの条件（「COIN・MARA・MSTR・HOOD は同時に 1 銘柄まで」等）を AI が読めるようにするための補助であり、強制は審査のテーマの上限が行う。
/// </summary>
public sealed record PortfolioHoldingsContext(IReadOnlyList<HeldHolding>? Holdings);
