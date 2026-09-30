using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-02, ADR-0048 決定 3, ADR-0023 決定 5, #1117, IADR-0464: K 線の検証口が使う、moomoo SDK の相場接続オブジェクト
// （MMAPI_Qot）への薄いシーム。形は BacktestService の IMoomooQotConnection（#743 / IADR-0327）に合わせる
// （サービス間の直接参照はできないため、同じ形をこちらに置く）。
//
// 🔴 **面は相場（Qot）の 2 要求と接続の管理だけに限る。** 発注・訂正・取消・口座の要求はここに無く、
// 検証口のプロセスは発注の接続（MMAPI_Trd）を 1 本も作らない（試験で面を固定する）。
// **これは SDK 非依存のポートではない。** protobuf 型はそのまま通す（SDK 非依存の境界は IKLineQuotaQuery が持つ）。
public interface IMoomooQotProbeConnection : IDisposable
{
    void SetClientInfo(string clientId, int clientVersion);

    void SetConnCallback(MMSPI_Conn callback);

    void SetQotCallback(MMSPI_Qot callback);

    // 鍵の内容（PKCS#1 PEM 文字列）を渡す（パスではない）。**出力・ログへ出さないこと。**
    void SetRsaPrivateKey(string privateKeyPem);

    bool InitConnect(string host, ushort port, bool encrypt);

    uint RequestHistoryKL(QotRequestHistoryKL.Request request);

    uint RequestHistoryKLQuota(QotRequestHistoryKLQuota.Request request);

    void Close();
}

// 接続オブジェクトの生成点。検証口は 1 回実行のため作り直さない（生成は 1 度だけ）。
public interface IMoomooQotProbeConnectionFactory
{
    IMoomooQotProbeConnection Create();
}

// 本番実装。SDK の MMAPI_Qot を 1 つ包むだけで、状態も判断も持たない。
public sealed class MMApiQotProbeConnectionFactory : IMoomooQotProbeConnectionFactory
{
    public IMoomooQotProbeConnection Create() => new MMApiQotProbeConnection();

    private sealed class MMApiQotProbeConnection : IMoomooQotProbeConnection
    {
        private readonly MMAPI_Qot _qot = new();

        public void SetClientInfo(string clientId, int clientVersion) => _qot.SetClientInfo(clientId, clientVersion);

        public void SetConnCallback(MMSPI_Conn callback) => _qot.SetConnCallback(callback);

        public void SetQotCallback(MMSPI_Qot callback) => _qot.SetQotCallback(callback);

        public void SetRsaPrivateKey(string privateKeyPem) => _qot.SetRSAPrivateKey(privateKeyPem);

        public bool InitConnect(string host, ushort port, bool encrypt) => _qot.InitConnect(host, port, encrypt);

        public uint RequestHistoryKL(QotRequestHistoryKL.Request request) => _qot.RequestHistoryKL(request);

        public uint RequestHistoryKLQuota(QotRequestHistoryKLQuota.Request request) => _qot.RequestHistoryKLQuota(request);

        public void Close() => _qot.Close();

        public void Dispose() => _qot.Dispose();
    }
}
