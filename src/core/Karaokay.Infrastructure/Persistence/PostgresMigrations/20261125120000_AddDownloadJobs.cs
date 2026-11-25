using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karaokay.Infrastructure.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class AddDownloadJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DownloadJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    ProgressPercent = table.Column<double>(type: "double precision", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QueueItemId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadJobs_Songs_SongId",
                        column: x => x.SongId,
                        principalTable: "Songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadJobs_QueueItemId",
                table: "DownloadJobs",
                column: "QueueItemId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadJobs_SongId",
                table: "DownloadJobs",
                column: "SongId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadJobs_VideoId",
                table: "DownloadJobs",
                column: "VideoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadJobs");
        }
    }
}
