using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    /// <summary>
    /// NFR-06, FR-14, IADR-0626 決定 1: 危険な操作の第 2 要素の状態（単一行 <c>owner_second_factor_state</c>）を足す。
    /// **追加のみ**（行は最初の確かめで作られる）。起動時の自動移行で適用される。コード・シークレットそのものは持たない。
    /// </summary>
    public partial class AddOwnerSecondFactorState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "owner_second_factor_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    SecretFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastAcceptedStep = table.Column<long>(type: "bigint", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LockoutCount = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ManualUnlockRequired = table.Column<bool>(type: "boolean", nullable: false),
                    LastAcceptedOperation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastAcceptedTarget = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owner_second_factor_state", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "owner_second_factor_state");
        }
    }
}
