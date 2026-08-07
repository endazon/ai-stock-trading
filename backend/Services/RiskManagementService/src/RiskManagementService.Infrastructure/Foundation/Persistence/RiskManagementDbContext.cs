using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;

// ADR-0001（Database per Service）, IADR-0012: リスク管理サービス専有の DbContext。
// 設定・kill switch・ロックアウトは単一行、変更履歴は追記専用。
internal sealed class RiskManagementDbContext(DbContextOptions<RiskManagementDbContext> options)
    : DbContext(options)
{
    public DbSet<RiskSettingsRow> RiskSettings => Set<RiskSettingsRow>();

    public DbSet<KillSwitchRow> KillSwitch => Set<KillSwitchRow>();

    // FR-10, ADR-0009: 取引の一時停止（pause）の単一行状態。kill switch と別テーブル・別状態。
    public DbSet<PauseRow> Pause => Set<PauseRow>();

    public DbSet<LockoutRow> Lockout => Set<LockoutRow>();

    public DbSet<SettingsChangeRow> SettingsChangeLog => Set<SettingsChangeRow>();

    public DbSet<ApprovedOrderRow> ApprovedOrders => Set<ApprovedOrderRow>();

    public DbSet<TradeFillRow> TradeFills => Set<TradeFillRow>();

    // FR-19, #154, IADR-0067: 相場操縦検知の入力＝注文アクティビティの射影。
    public DbSet<OrderActivityRow> OrderActivities => Set<OrderActivityRow>();

    // FR-20, UC-06, IADR-0070: 段階ゲートの遷移履歴（追記専用）と段階別実績（単一行）。
    public DbSet<StageTransitionRow> StageTransitions => Set<StageTransitionRow>();

    public DbSet<StagePerformanceRow> StagePerformance => Set<StagePerformanceRow>();

    // FR-20, FR-11, #387, IADR-0148: 発注審査の観測ログ（段階ゲートの「統制違反 0 件」の供給元）。
    public DbSet<OrderScreeningObservationRow> OrderScreeningObservations =>
        Set<OrderScreeningObservationRow>();

    // FR-20, FR-12, #386, IADR-0149: 約定の観測ログ（段階ゲートの「最小取引件数 100 件」の供給元）。
    public DbSet<Stage1FillObservationRow> Stage1FillObservations => Set<Stage1FillObservationRow>();

    // FR-20, FR-12, #385, IADR-0150: 稼働の観測ログ（段階ゲートの「60 営業日」の供給元）。
    public DbSet<Stage1SessionUptimeRow> Stage1SessionUptimes => Set<Stage1SessionUptimeRow>();

    // FR-20, FR-09, IADR-0085: 撤退の非停止（ペーパー乖離）降格提案の通知重複排除（最後に通知したシグネチャ・単一行）。
    public DbSet<WithdrawalNotificationRow> WithdrawalNotifications => Set<WithdrawalNotificationRow>();

    // FR-05, FR-10, #305, IADR-0124: 建玉乖離の追跡状態（連続観測回数・報告済みシグネチャ・単一行）。
    public DbSet<PositionDriftStateRow> PositionDriftStates => Set<PositionDriftStateRow>();

    // FR-10, FR-11, FR-06, #419, IADR-0159: 強制買戻しの事後推定の追記専用台帳
    // （30 日禁止の供給元・ADR-0016 決定15 の「発生回数」の集計元）。
    public DbSet<BuyInInferenceRow> BuyInInferences => Set<BuyInInferenceRow>();

    // FR-19, FR-10, FR-11, #425, ADR-0025 決定2, IADR-0166: GFV 発生回数の**自前計数**の追記専用台帳。
    // **数えているのは「自らのガードをすり抜けた買付」であり、ブローカーの GFV カウンタの写しではない。**
    public DbSet<GoodFaithViolationRow> GoodFaithViolations => Set<GoodFaithViolationRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<RiskSettingsRow>(e =>
        {
            e.ToTable("risk_settings");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Json).HasColumnType("jsonb").IsRequired();
            // 楽観的排他制御: Version を並行トークンとして扱う（更新時に一致を要求）。
            e.Property(r => r.Version).IsConcurrencyToken();
        });

        mb.Entity<KillSwitchRow>(e =>
        {
            e.ToTable("kill_switch");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Actor).HasMaxLength(256);
            e.Property(r => r.Reason).HasMaxLength(1024);
        });

        mb.Entity<PauseRow>(e =>
        {
            e.ToTable("pause");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Actor).HasMaxLength(256);
            e.Property(r => r.Reason).HasMaxLength(1024);
        });

        mb.Entity<LockoutRow>(e =>
        {
            e.ToTable("lockout");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Reason).HasMaxLength(1024).IsRequired();
        });

        mb.Entity<SettingsChangeRow>(e =>
        {
            e.ToTable("settings_change_log");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Actor).HasMaxLength(256).IsRequired();
            e.Property(r => r.ChangeType).HasMaxLength(64).IsRequired();
            e.Property(r => r.Reason).HasMaxLength(1024).IsRequired();
            // 新しい順の照会が既定のため日時にインデックスを張る。
            e.HasIndex(r => r.ChangedAt);
        });

        // IADR-0018: 取引台帳（承認・約定）。追記専用。DecisionId/OrderId で冪等。
        mb.Entity<ApprovedOrderRow>(e =>
        {
            e.ToTable("approved_orders");
            e.HasKey(r => r.DecisionId);
            e.Property(r => r.DecisionId).ValueGeneratedNever();
            e.Property(r => r.Symbol).HasMaxLength(32).IsRequired();
        });

        mb.Entity<TradeFillRow>(e =>
        {
            e.ToTable("trade_fills");
            e.HasKey(r => r.OrderId);
            e.Property(r => r.OrderId).HasMaxLength(128).ValueGeneratedNever();
            // 承認 Intent との相関・時系列畳み込みのため DecisionId にインデックスを張る。
            e.HasIndex(r => r.DecisionId);
        });

        // FR-19, #154, IADR-0067: 注文アクティビティの射影（DecisionId で 1 注文＝1 行・更新される）。
        mb.Entity<OrderActivityRow>(e =>
        {
            e.ToTable("order_activity");
            e.HasKey(r => r.DecisionId);
            e.Property(r => r.DecisionId).ValueGeneratedNever();
            e.Property(r => r.Symbol).HasMaxLength(32).IsRequired();
            // 相場操縦検知の窓照会（銘柄・市場別に発注時刻の範囲を切り出す）用の複合インデックス。
            e.HasIndex(r => new { r.Symbol, r.Market, r.PlacedAt });
        });

        // FR-20, UC-06, IADR-0070: 段階遷移履歴（追記専用）。Sequence が主キー＝一意連番で並行二重追記を弾く。
        mb.Entity<StageTransitionRow>(e =>
        {
            e.ToTable("stage_transitions");
            e.HasKey(r => r.Sequence);
            e.Property(r => r.Sequence).ValueGeneratedNever();
            e.Property(r => r.ApprovedBy).HasMaxLength(256).IsRequired();
            e.Property(r => r.Reason).HasMaxLength(1024).IsRequired();
        });

        // FR-20, FR-15, IADR-0070: 段階別実績（単一行）。未記録は fail-safe 既定を返す（ストア側）。
        mb.Entity<StagePerformanceRow>(e =>
        {
            e.ToTable("stage_performance");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
        });

        // FR-20, FR-11, #387, IADR-0148: 発注審査の観測ログ。DecisionId が主キー＝**計上単位**
        // （1 回の発注拒否につき 1 件）を主キー制約で担保し、再送でも二重計上しない。
        // 集計は「算入対象があるか」「そのうち違反は何件か」の 2 問だけであり、両方を満たすインデックスを張る。
        mb.Entity<OrderScreeningObservationRow>(e =>
        {
            e.ToTable("order_screening_observations");
            e.HasKey(r => r.DecisionId);
            e.Property(r => r.DecisionId).ValueGeneratedNever();
            e.HasIndex(r => new { r.CountsTowardStage1, r.IsControlViolation });
        });

        // FR-20, FR-12, #386, IADR-0149: 約定の観測ログ。DecisionId が主キー＝**計上単位**（約定した注文 1 件）を
        // 主キー制約で担保し、分割約定の続報・再送でも二重計上しない。集計は「算入対象は何件か」の 1 問だけである。
        mb.Entity<Stage1FillObservationRow>(e =>
        {
            e.ToTable("stage1_fill_observations");
            e.HasKey(r => r.DecisionId);
            e.Property(r => r.DecisionId).ValueGeneratedNever();
            e.HasIndex(r => r.CountsTowardStage1);
        });

        // FR-20, FR-12, #385, IADR-0150: 稼働の観測ログ。(取引日, 発注先) が主キー＝**1 取引日 1 発注先 1 行**を
        // 主キー制約で担保し、probe の巡回が何度届いても行が増えない。集計は「算入された日は何日か」
        // 「paper で除外された日は何日か」の 2 問であり、両方を満たすインデックスを張る。
        mb.Entity<Stage1SessionUptimeRow>(e =>
        {
            e.ToTable("stage1_session_uptime");
            e.HasKey(r => new { r.SessionDateEasternTime, r.Provider });
            e.HasIndex(r => new { r.QualifiesTowardStage1, r.MeetsUptimeThreshold });
        });

        // FR-20, FR-09, IADR-0085: 撤退の非停止降格提案の通知重複排除（単一行）。未記録＝未通知（fail-safe）。
        mb.Entity<WithdrawalNotificationRow>(e =>
        {
            e.ToTable("withdrawal_notification");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Signature).HasMaxLength(256);
        });

        // FR-05, FR-10, #305, IADR-0124: 建玉乖離の追跡状態（単一行）。Version を並行トークンにして
        // レプリカ間の read-modify-write を守る（負けた側は何も書かない）。シグネチャは銘柄数に比例して
        // 伸びるため上限を置かない（text）。
        mb.Entity<PositionDriftStateRow>(e =>
        {
            e.ToTable("position_drift_state");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.ObservedSignature).IsRequired();
            e.Property(r => r.ReportedSignature).IsRequired();
            e.Property(r => r.Version).IsConcurrencyToken();
        });

        // FR-10, FR-11, FR-06, #419, IADR-0159: 強制買戻しの推定台帳（追記専用）。
        // 集計は「銘柄ごとの最新行」「銘柄ごとの禁止期限の最大値」「期間内の推定件数」の 3 問である。
        mb.Entity<BuyInInferenceRow>(e =>
        {
            e.ToTable("buy_in_inferences");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Symbol).HasMaxLength(32).IsRequired();
            e.HasIndex(r => new { r.Symbol, r.Market });
            e.HasIndex(r => r.InferredOn);
        });

        // FR-19, FR-10, FR-11, #425, ADR-0025 決定2, IADR-0166: GFV 自前計数の台帳（追記専用）。
        // **主キーは OrderId**——計上単位（1 注文 1 件）そのものを主キーにすることで、部分約定の進行・
        // メッセージ再送で二重計上しないことを DB 側で担保する（IADR-0148 決定3 の DecisionId 主キーと同じ規律）。
        mb.Entity<GoodFaithViolationRow>(e =>
        {
            e.ToTable("good_faith_violations");
            e.HasKey(r => r.OrderId);
            e.Property(r => r.OrderId).HasMaxLength(64).ValueGeneratedNever();
            e.Property(r => r.Symbol).HasMaxLength(32).IsRequired();
            e.HasIndex(r => r.OccurredOn);
        });
    }
}
