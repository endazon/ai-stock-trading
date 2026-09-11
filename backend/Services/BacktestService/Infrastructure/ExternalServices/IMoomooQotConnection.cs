using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;

namespace BacktestService.Infrastructure.ExternalServices;

// #743, FR-15, ADR-0002, ADR-0023 決定5, IADR-0157, IADR-0327: moomoo SDK の相場接続オブジェクト（MMAPI_Qot）への薄いシーム。
//
// なぜ要るか: OpenD が停止中に一度 `Connection refused` を受けた接続オブジェクトは、以後 InitConnect を
// 呼んでも TCP を張り直さない（true を返すだけで SYN も出さない・#732 の実測）。固着した接続オブジェクトを
// **捨てて作り直す**しか復旧手段が無いが、従来は readonly フィールドで直接生成しており作り直せなかった。
// 発注経路（MMAPI_Trd）は #732 / IADR-0327 で同じ形に直しており、本ファイルはその**同型の適用**である。
//
// **これは SDK 非依存のポートではない。** protobuf 型はそのまま通す（SDK 非依存の境界は
// IMoomooHistoryKLineClient が既に持っており、二重に作らない）。面は MMApiMoomooHistoryKLineClient が
// 実際に使うものだけに限る（使っていない SDK メソッドを足さない）。
//
// **発注経路の IMoomooTradeConnection と実体を共有しない。** サービス境界を跨ぐ横断参照になるためであり、
// MoomooBarDataPreflight が MoomooPreflight と別実体であるのと同じ理由である。
public interface IMoomooQotConnection : IDisposable
{
    void SetClientInfo(string clientId, int clientVersion);

    void SetConnCallback(MMSPI_Conn callback);

    void SetQotCallback(MMSPI_Qot callback);

    // 鍵の内容（PKCS#1 PEM 文字列）を渡す（パスではない）。**ログへ出さないこと。**
    void SetRsaPrivateKey(string privateKeyPem);

    bool InitConnect(string host, ushort port, bool encrypt);

    uint RequestHistoryKL(QotRequestHistoryKL.Request request);

    void Close();
}

// #743, IADR-0327: 接続オブジェクトの生成点。接続試行が失敗するたびに Create() し直す。
public interface IMoomooQotConnectionFactory
{
    IMoomooQotConnection Create();
}

// 本番実装。SDK の MMAPI_Qot を 1 つ包むだけで、状態も判断も持たない。
public sealed class MMApiQotConnectionFactory : IMoomooQotConnectionFactory
{
    public IMoomooQotConnection Create() => new MMApiQotConnection();

    private sealed class MMApiQotConnection : IMoomooQotConnection
    {
        private readonly MMAPI_Qot _qot = new();

        public void SetClientInfo(string clientId, int clientVersion) => _qot.SetClientInfo(clientId, clientVersion);

        public void SetConnCallback(MMSPI_Conn callback) => _qot.SetConnCallback(callback);

        public void SetQotCallback(MMSPI_Qot callback) => _qot.SetQotCallback(callback);

        public void SetRsaPrivateKey(string privateKeyPem) => _qot.SetRSAPrivateKey(privateKeyPem);

        public bool InitConnect(string host, ushort port, bool encrypt) => _qot.InitConnect(host, port, encrypt);

        public uint RequestHistoryKL(QotRequestHistoryKL.Request request) => _qot.RequestHistoryKL(request);

        public void Close() => _qot.Close();

        public void Dispose() => _qot.Dispose();
    }
}
