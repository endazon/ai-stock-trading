using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, #840, IADR-0352 決定 2: 報告書の生成 1 回ぶんについて、**依存先への要求がなぜ失敗したか**を集める観測点。
//
// 各供給元（Http*Source）は不達を `null`（未供給）へ倒して返す契約であり、生成器からは
// 「届かなかった」ことしか見えない。**待てば直る失敗（依存先がまだ起動していない）**と
// **待っても直らない失敗（ロール未付与の 403）**を分けるには理由が要る。
//
// 供給元のポート（`T?` を返す 11 本）を作り替えず、HTTP の鎖（`ReportDependencyHandler`）が 1 か所で
// 分類して本観測点へ記録する。供給元は singleton・生成器は scoped のため、受け渡しは AsyncLocal で行う
// （生成器が開いた観測は、その async の流れの下で呼ばれた HTTP 送信からだけ見える。
// 観測が開かれていない経路＝手動の API・確定後の KB 保存では何も記録しない）。
public sealed class ReportDependencyProbe
{
    private readonly AsyncLocal<ReportDependencyObservation?> _current = new();

    /// <summary>観測を開く。返り値を <c>using</c> で閉じる（閉じた後の記録は捨てる）。</summary>
    public ReportDependencyObservation Begin()
    {
        var observation = new ReportDependencyObservation(this);
        _current.Value = observation;
        return observation;
    }

    /// <summary>失敗を記録する。観測が開かれていなければ何もしない。</summary>
    public void Record(string dependency, ReportDependencyFailureKind kind, bool transient, string detail) =>
        _current.Value?.Add(dependency, kind, transient, detail);

    internal void End(ReportDependencyObservation observation)
    {
        if (ReferenceEquals(_current.Value, observation))
            _current.Value = null;
    }
}

// 生成 1 回ぶんの観測。**どの入力を取りに行っている最中の失敗か**を生成器が `Enter` で教える
// （供給元は逐次に呼ばれるため、現在の入力は 1 つに定まる）。
public sealed class ReportDependencyObservation : IDisposable
{
    private readonly ReportDependencyProbe _owner;
    private readonly List<ReportDependencyFailure> _failures = [];
    private readonly Lock _gate = new();
    private ReportInput? _currentInput;
    private bool _closed;

    internal ReportDependencyObservation(ReportDependencyProbe owner) => _owner = owner;

    /// <summary>これから取りに行く入力を宣言する。</summary>
    public void Enter(ReportInput input)
    {
        lock (_gate)
            _currentInput = input;
    }

    /// <summary>記録された失敗（記録順）。</summary>
    public IReadOnlyList<ReportDependencyFailure> Failures
    {
        get
        {
            lock (_gate)
                return [.. _failures];
        }
    }

    /// <summary>その入力の取得中に失敗（一過性・恒常を問わない）を観測したか。</summary>
    public bool HasFailure(ReportInput input) => Failures.Any(f => f.Input == input);

    /// <summary>その入力の取得中に**一過性**の失敗を観測したか。</summary>
    public bool HasTransientFailure(ReportInput input) => Failures.Any(f => f.Input == input && f.Transient);

    internal void Add(string dependency, ReportDependencyFailureKind kind, bool transient, string detail)
    {
        lock (_gate)
        {
            if (_closed)
                return;

            _failures.Add(new ReportDependencyFailure(_currentInput, dependency, kind, transient, detail));
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _closed = true;

        _owner.End(this);
    }
}

// 失敗の種類。Transient は種類から一意には決まらない（タイムアウトは依存先によって扱いが違う）ため別に持つ。
public enum ReportDependencyFailureKind
{
    /// <summary>サービストークンを取得できず、**送信しなかった**。</summary>
    ServiceTokenUnavailable,

    /// <summary>接続できなかった（名前解決・接続拒否など）。</summary>
    Unreachable,

    /// <summary>応答が時間内に返らなかった。</summary>
    Timeout,

    /// <summary>非 2xx の応答。</summary>
    HttpStatus,
}

public sealed record ReportDependencyFailure(
    ReportInput? Input,
    string Dependency,
    ReportDependencyFailureKind Kind,
    bool Transient,
    string Detail);
