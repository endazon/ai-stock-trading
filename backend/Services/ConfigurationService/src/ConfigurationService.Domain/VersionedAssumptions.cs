namespace AiStockTrading.Configuration.Domain;

// FR-17: 現在の全体前提条件とそのバージョン。報告書は生成時 Version を凍結参照でき、消費側サービス（費用統制・損益集計・
// AI 判断）は Version つきで共通参照する（IADR-0021/0063）。消費側が設定サービスの Application 層に依存せず参照できるよう
// Domain に置く（IADR-0063 決定 3）。
public sealed record VersionedAssumptions(TradingAssumptions Assumptions, int Version)
{
    /// <summary>
    /// 未解決を表す番兵バージョン（IADR-0063 決定 5）。設定サービスから一度も取得できず既定値へ倒れたことを示す。
    /// 実在する版は 1 から始まる（未設定時に既定をシードした時点で 1）。
    /// </summary>
    public const int UnresolvedVersion = 0;

    /// <summary>設定サービスの値を解決できているか（false ＝既定値へのフェイルセーフ）。</summary>
    public bool IsResolved => Version > UnresolvedVersion;
}
