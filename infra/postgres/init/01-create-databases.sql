-- ai-stock-trading: Database-per-Service（ADR-0001）用の専有 DB を作成する。
-- issue #107 / IADR-0048。postgres コンテナ初回起動時に /docker-entrypoint-initdb.d/ から実行される。
-- 所有者は POSTGRES_USER（.env）。各 Worker は起動時に EF Migration で自身のスキーマを適用する（#82 で疎通）。
CREATE DATABASE audit_svc;
CREATE DATABASE configuration_svc;
CREATE DATABASE cost_control_svc;
CREATE DATABASE market_monitor_svc;
CREATE DATABASE order_execution_svc;
CREATE DATABASE report_svc;
CREATE DATABASE risk_management_svc;
