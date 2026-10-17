namespace AiStockTrading.Shared.Contracts.Trading;

// FR-04, FR-10, FR-11, #1384, IADR-0558 決定 2: 新規建ての判断が**どの条件で建てたか**の記録（建てた条件の記号と基準価格）。
// 取引判断が新規建て（買い増し・売り増しを含む）の発注意図にだけ載せ、取引台帳の承認行へ永続化され、保有中の判断へ建玉（ロット）ごとに渡る。
//
// - Condition: 方針の「条件 X:」行の記号（英大文字 1 字）。判断が答えた記号が**方針の条件集合に含まれる**ときだけ入る（PolicyEntryConditions）。
// - ReferencePrice: その条件の判定に使った価格（銘柄の市場の通貨。例: 急落反発なら日中安値）。正かつ当日の価格の範囲内のときだけ入る。
//   🔴 **null＝不明**（判断が答えなかった・範囲を確かめられなかった）。0 で埋めない。
//
// 🔴 記号が不明なら記録全体を作らない（発注意図の EntryBasis は null）——記号の無い基準価格は、どの条件の出口にも当てはまらない。
// 判断・数量・損切りには一切効かせない（記録だけ。不正・欠落で見送りにしない）。
public sealed record EntryBasis(string Condition, decimal? ReferencePrice);
