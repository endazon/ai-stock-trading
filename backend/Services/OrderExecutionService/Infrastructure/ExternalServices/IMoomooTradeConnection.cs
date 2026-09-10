using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// #732, FR-05, ADR-0002, IADR-0327: moomoo SDK の取引接続オブジェクト（MMAPI_Trd）への薄いシーム。
//
// なぜ要るか: OpenD が停止中に一度 `Connection refused` を受けた MMAPI_Trd は、以後 InitConnect を
// 呼んでも TCP を張り直さない（true を返すだけで SYN も出さない・#732 の実測）。固着した接続オブジェクトを
// **捨てて作り直す**しか復旧手段が無いが、従来は readonly フィールドで直接生成しており作り直せなかった
// （テストで差し替える口も無かった＝IADR-0153 の残余リスク）。
//
// **これは SDK 非依存のポートではない。** protobuf 型はそのまま通す（SDK 非依存の境界は IMoomooTradeClient が
// 既に持っており、二重に作らない）。MMAPI_Trd のメソッドは 1 つも virtual でないため、継承では差し替えられない。
// 面は MMApiMoomooTradeClient が実際に使うものだけに限る（使っていない SDK メソッドを足さない）。
public interface IMoomooTradeConnection : IDisposable
{
    void SetClientInfo(string clientId, int clientVersion);

    void SetConnCallback(MMSPI_Conn callback);

    void SetTrdCallback(MMSPI_Trd callback);

    // 鍵の内容（PKCS#1 PEM 文字列）を渡す（パスではない）。**ログへ出さないこと。**
    void SetRsaPrivateKey(string privateKeyPem);

    bool InitConnect(string host, ushort port, bool encrypt);

    void Close();

    Moomoo.OpenApi.Pb.Common.PacketID NextPacketId();

    uint GetAccList(TrdGetAccList.Request request);

    uint PlaceOrder(TrdPlaceOrder.Request request);

    uint ModifyOrder(TrdModifyOrder.Request request);

    uint GetOrderList(TrdGetOrderList.Request request);

    uint GetHistoryOrderList(TrdGetHistoryOrderList.Request request);

    uint GetPositionList(TrdGetPositionList.Request request);
}

// #732, IADR-0327: 接続オブジェクトの生成点。接続試行が失敗するたびに Create() し直す。
public interface IMoomooTradeConnectionFactory
{
    IMoomooTradeConnection Create();
}

// 本番実装。SDK の MMAPI_Trd を 1 つ包むだけで、状態も判断も持たない。
public sealed class MMApiTradeConnectionFactory : IMoomooTradeConnectionFactory
{
    public IMoomooTradeConnection Create() => new MMApiTradeConnection();

    private sealed class MMApiTradeConnection : IMoomooTradeConnection
    {
        private readonly MMAPI_Trd _trd = new();

        public void SetClientInfo(string clientId, int clientVersion) => _trd.SetClientInfo(clientId, clientVersion);

        public void SetConnCallback(MMSPI_Conn callback) => _trd.SetConnCallback(callback);

        public void SetTrdCallback(MMSPI_Trd callback) => _trd.SetTrdCallback(callback);

        public void SetRsaPrivateKey(string privateKeyPem) => _trd.SetRSAPrivateKey(privateKeyPem);

        public bool InitConnect(string host, ushort port, bool encrypt) => _trd.InitConnect(host, port, encrypt);

        public void Close() => _trd.Close();

        public Moomoo.OpenApi.Pb.Common.PacketID NextPacketId() => _trd.NextPacketID();

        public uint GetAccList(TrdGetAccList.Request request) => _trd.GetAccList(request);

        public uint PlaceOrder(TrdPlaceOrder.Request request) => _trd.PlaceOrder(request);

        public uint ModifyOrder(TrdModifyOrder.Request request) => _trd.ModifyOrder(request);

        public uint GetOrderList(TrdGetOrderList.Request request) => _trd.GetOrderList(request);

        public uint GetHistoryOrderList(TrdGetHistoryOrderList.Request request) => _trd.GetHistoryOrderList(request);

        public uint GetPositionList(TrdGetPositionList.Request request) => _trd.GetPositionList(request);

        public void Dispose() => _trd.Dispose();
    }
}
