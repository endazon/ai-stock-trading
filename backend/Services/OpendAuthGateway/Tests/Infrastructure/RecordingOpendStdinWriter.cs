using OpendAuthGateway.Infrastructure;

namespace OpendAuthGateway.Tests.Infrastructure;

/// <summary>
/// #722, IADR-0320: 書き込みの<b>観測点</b>。
/// <para>
/// この試験群で最も重要な主張は「棄却した要求では<b>1 バイトも書かれない</b>」である。
/// 実 FIFO では観測できない（Linux 専用・OpenD が要る）ため、口を差し替えて数える。
/// </para>
/// </summary>
public sealed class RecordingOpendStdinWriter : IOpendStdinWriter
{
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];

    /// <summary>次の書き込みで返す結果。503 経路の試験で差し替える。</summary>
    public StdinWriteOutcome Outcome { get; set; } = StdinWriteOutcome.Written;

    /// <summary>書き込みを求められた行（順序どおり）。棄却された要求はここへ 1 件も現れない。</summary>
    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return [.. _lines]; }
    }

    public StdinWriteOutcome WriteLine(string line)
    {
        lock (_gate) _lines.Add(line);
        return Outcome;
    }
}
