using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karaokay.Infrastructure.Persistence.PostgresMigrations
{
    /// <summary>
    /// Persists the per-room <c>QueueDelta.Sequence</c> so cold-start rehydration resumes above
    /// every sequence a client already observed (docs/ARCHITECTURE.md §5.2, Slice 4).
    /// </summary>
    public partial class AddRoomQueueSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "QueueSequence",
                table: "Rooms",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QueueSequence",
                table: "Rooms");
        }
    }
}
