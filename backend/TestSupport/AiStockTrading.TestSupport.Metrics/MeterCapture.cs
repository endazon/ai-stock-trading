using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace AiStockTrading.TestSupport.Metrics;

/// <summary>
/// NFR-07, #287, IADR-0255: 指定した <see cref="Meter"/> 名の計測値を <see cref="MeterListener"/> で捕まえる。
/// <para>
/// **「計器を定義した」と「計器が発火した」は別の事実である。** 定義だけを見るテストは、
/// 計上の行が消えても緑のまま通る。本クラスは実際に流れた測定値（計器名・値・タグ）を記録し、
/// テストが後者を表明できるようにする。
/// </para>
/// <para>
/// <see cref="Meter"/> はプロセス全体で観測されるため、テストの並行実行では他のテストの測定値も
/// 混ざり得る。表明は「含む（Contain）」の形で書き、件数の厳密一致を要求しない場合は
/// <see cref="ValuesOf(string)"/> ではなく <see cref="TagValuesOf(string, string)"/> を用いる。
/// </para>
/// </summary>
public sealed class MeterCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<Measurement> _measurements = [];
    private readonly Lock _gate = new();

    /// <param name="meterName">観測対象の Meter 名。</param>
    public MeterCapture(string meterName)
    {
        ArgumentNullException.ThrowIfNull(meterName);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName) listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Add(instrument, value, tags));

        _listener.Start();
    }

    /// <summary>
    /// 否定形の表明（「計器が発火しなかった」）のために、テストごとに一意な Meter 名を作る。
    /// <para>
    /// 🔴 <b>否定形の表明では既定の Meter 名を使ってはならない</b>（#695）。<see cref="Meter"/> は
    /// プロセス全体で観測されるため、既定名のままだと<b>同時に走っている別テストの測定値まで拾い</b>、
    /// <c>ValuesOf(...).Should().BeEmpty()</c> が**他人の発火で偽陽性になる**。2026-09-04 の
    /// Integration E2E で実際に起きた（`ast.finnhub.daily_request_estimate` に値 24.0 が混入して赤）。
    /// </para>
    /// <para>
    /// 使い方: 本メソッドで作った名前を <see cref="MeterCapture(string)"/> と、計器を持つ側
    /// （<c>BusinessMetrics</c> の <c>meterName</c> 引数）の<b>両方へ同じ値で渡す</b>。
    /// 肯定形（「発火した」）の表明は既定名のままでよい——他人の測定値が混ざっても
    /// 「含む」の表明は壊れないためである。
    /// </para>
    /// </summary>
    /// <param name="caller">呼び出し元のテストメソッド名（自動）。名前を読める形にするためだけに使う。</param>
    public static string NewIsolatedMeterName([CallerMemberName] string? caller = null) =>
        $"AiStockTrading.Business.Test.{caller}.{Guid.NewGuid():N}";

    /// <summary>捕まえた測定値（記録順）。</summary>
    public IReadOnlyList<Measurement> Measurements
    {
        get { lock (_gate) return [.. _measurements]; }
    }

    /// <summary>指定した計器名の測定値。</summary>
    public IReadOnlyList<Measurement> ValuesOf(string instrumentName) =>
        [.. Measurements.Where(m => m.InstrumentName == instrumentName)];

    /// <summary>指定した計器名について、指定したタグの値を出現順に返す（欠けている測定値は除く）。</summary>
    public IReadOnlyList<string> TagValuesOf(string instrumentName, string tagName) =>
        [.. ValuesOf(instrumentName)
            .Select(m => m.Tags.TryGetValue(tagName, out var v) ? v : null)
            .Where(v => v is not null)
            .Select(v => v!)];

    /// <summary>指定した計器名の測定値の合計。</summary>
    public double SumOf(string instrumentName) => ValuesOf(instrumentName).Sum(m => m.Value);

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copied[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        lock (_gate) _measurements.Add(new Measurement(instrument.Name, value, copied));
    }

    /// <summary>1 件の測定値（計器名・値・タグ）。</summary>
    /// <param name="InstrumentName">計器名。</param>
    /// <param name="Value">測定値（long も double へ寄せて保持する）。</param>
    /// <param name="Tags">タグ（値は文字列化して保持する）。</param>
    public sealed record Measurement(
        string InstrumentName,
        double Value,
        IReadOnlyDictionary<string, string> Tags);
}
