using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karaokay.Infrastructure.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Rooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentQueueItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlaybackStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PausedAtPositionMs = table.Column<long>(type: "bigint", nullable: false),
                    GuideVocalVolume = table.Column<double>(type: "double precision", nullable: false),
                    CountdownRemainingBeats = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rooms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Songs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Artist = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    ThumbnailUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    VideoId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Channel = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    InstrumentalPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    VocalsPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    LyricsPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CachePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    IsSyntheticDemo = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastPlayedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Songs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueueItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SessionTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueueItems_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QueueItems_Songs_SongId",
                        column: x => x.SongId,
                        principalTable: "Songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_RoomId_Position",
                table: "QueueItems",
                columns: new[] { "RoomId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_SongId",
                table: "QueueItems",
                column: "SongId");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_Code",
                table: "Rooms",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_CreatedAtUtc",
                table: "Rooms",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_IsSyntheticDemo",
                table: "Songs",
                column: "IsSyntheticDemo");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_Source",
                table: "Songs",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_VideoId",
                table: "Songs",
                column: "VideoId",
                unique: true,
                filter: "\"VideoId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QueueItems");

            migrationBuilder.DropTable(
                name: "Rooms");

            migrationBuilder.DropTable(
                name: "Songs");
        }
    }
}
