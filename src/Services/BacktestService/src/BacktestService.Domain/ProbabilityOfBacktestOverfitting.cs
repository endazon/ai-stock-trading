namespace AiStockTrading.Backtest.Domain;

// FR-15, ADR-0008, 06_daytrading-review §3.2, IADR-0044: Probability of Backtest Overfitting（CSCV）。
// 観測を S 個の部分行列に分割し、S/2 を IS・残りを OOS とする全組合せで「IS 最良戦略の OOS 相対順位」の
// ロジット λ を求め、PBO = P(λ ≤ 0) を推定する。戦略のパフォーマンス指標は平均（順位付けの一貫性のみを要する）。
public static class ProbabilityOfBacktestOverfitting
{
    // ロジット発散を防ぐための順位クランプ。
    private const double RankEpsilon = 1e-9;

    // performanceByBlock[block][strategy] のパフォーマンス指標行列と分割数 partitions（偶数・2 以上）。
    public static double Compute(double[][] performanceByBlock, int partitions)
    {
        ArgumentNullException.ThrowIfNull(performanceByBlock);
        if (partitions < 2 || partitions % 2 != 0)
            throw new ArgumentException("分割数は 2 以上の偶数である必要があります。", nameof(partitions));

        var blockCount = performanceByBlock.Length;
        if (blockCount < partitions)
            throw new ArgumentException("ブロック数は分割数以上である必要があります。", nameof(performanceByBlock));

        var strategyCount = performanceByBlock[0].Length;
        if (strategyCount < 2)
            throw new ArgumentException("戦略は 2 つ以上である必要があります。", nameof(performanceByBlock));
        // 全ブロック行の戦略数は揃っている必要がある（不揃いは素の IndexOutOfRange ではなく一貫した検証で弾く・#100 レビュー指摘）。
        if (performanceByBlock.Any(row => row is null || row.Length != strategyCount))
            throw new ArgumentException("全ブロック行の戦略数は一致している必要があります。", nameof(performanceByBlock));

        // ブロックを S 個のグループへ連続分割（サイズ差は最大 1）。
        var groups = PartitionBlocks(blockCount, partitions);

        var overfitCount = 0;
        var splitCount = 0;
        foreach (var isGroups in Combinations(partitions, partitions / 2))
        {
            var isSet = new bool[partitions];
            foreach (var g in isGroups)
                isSet[g] = true;

            var isMean = GroupMean(performanceByBlock, groups, isSet, inSample: true, strategyCount);
            var oosMean = GroupMean(performanceByBlock, groups, isSet, inSample: false, strategyCount);

            var best = ArgMax(isMean);
            var omega = RelativeRank(oosMean, best);
            var logit = Math.Log(omega / (1.0 - omega));
            if (logit <= 0.0)
                overfitCount++;
            splitCount++;
        }

        return splitCount == 0 ? 0d : (double)overfitCount / splitCount;
    }

    // ブロック番号を partitions 個のグループへ連続割当する（各グループのブロック番号リスト）。
    private static List<int>[] PartitionBlocks(int blockCount, int partitions)
    {
        var groups = new List<int>[partitions];
        for (var g = 0; g < partitions; g++)
            groups[g] = [];
        for (var b = 0; b < blockCount; b++)
        {
            var g = b * partitions / blockCount; // 連続・ほぼ均等
            groups[g].Add(b);
        }
        return groups;
    }

    // IS または OOS のグループに属するブロックで、各戦略の平均パフォーマンスを求める。
    private static double[] GroupMean(double[][] perf, List<int>[] groups, bool[] isSet, bool inSample, int strategyCount)
    {
        var sums = new double[strategyCount];
        var count = 0;
        for (var g = 0; g < groups.Length; g++)
        {
            if (isSet[g] != inSample)
                continue;
            foreach (var b in groups[g])
            {
                for (var s = 0; s < strategyCount; s++)
                    sums[s] += perf[b][s];
                count++;
            }
        }
        if (count > 0)
        {
            for (var s = 0; s < strategyCount; s++)
                sums[s] /= count;
        }
        return sums;
    }

    private static int ArgMax(double[] values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best])
                best = i;
        }
        return best;
    }

    // OOS パフォーマンスにおける戦略 index の相対順位 ω ∈ (0,1)。タイは平均順位、ω=avgRank/(N+1)。
    private static double RelativeRank(double[] oos, int index)
    {
        var value = oos[index];
        var less = 0;
        var equal = 0;
        foreach (var v in oos)
        {
            if (v < value)
                less++;
            else if (v == value)
                equal++;
        }
        var avgRank = less + (equal + 1) / 2.0; // 1-based 平均順位
        var omega = avgRank / (oos.Length + 1);
        return Math.Clamp(omega, RankEpsilon, 1.0 - RankEpsilon);
    }

    // n 個から k 個を選ぶ組合せ（インデックスの昇順列）を列挙する。
    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var indices = new int[k];
        for (var i = 0; i < k; i++)
            indices[i] = i;

        while (true)
        {
            yield return (int[])indices.Clone();

            var pos = k - 1;
            while (pos >= 0 && indices[pos] == n - k + pos)
                pos--;
            if (pos < 0)
                yield break;
            indices[pos]++;
            for (var i = pos + 1; i < k; i++)
                indices[i] = indices[i - 1] + 1;
        }
    }
}
