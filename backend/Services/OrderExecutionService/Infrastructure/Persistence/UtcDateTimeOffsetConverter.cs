using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace OrderExecutionService.Infrastructure.Persistence;

// 🔴 NFR, FR-10, #1473, IADR-0587: DB の境界で DateTimeOffset を UTC へ揃える（DbContext.ConfigureConventions が全 DateTimeOffset 列へ掛ける）。
// Npgsql は timestamptz へ UTC 以外のオフセットを書けない（`only offset 0 (UTC) is supported`）。市場の取引日の境界（米国東部 −04:00・JST +09:00）を
// 作る式は正しくてもオフセットを持つので、呼び出し側ごとに直すのではなく境界で揃える。EF は比較の相手の列の変換を**照会のパラメータにも**掛けるので、
// 書き込み（INSERT/UPDATE）と Where のパラメータの両方が UTC になる。瞬間は変えない（ToUniversalTime は同じ瞬間の表現を変えるだけ）。
// 読み戻しはそのまま（timestamptz は UTC で返る）。全サービスの DbContext が同じものを持つことは Architecture.Tests が検査する。
internal sealed class UtcDateTimeOffsetConverter()
    : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);
