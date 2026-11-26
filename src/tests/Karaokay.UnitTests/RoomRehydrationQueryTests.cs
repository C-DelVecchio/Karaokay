using Karaokay.Api.Features;
using Karaokay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Karaokay.UnitTests;

/// <summary>
/// Cold-start rehydration query shape (docs/ARCHITECTURE.md §5.1 rule 6). Npgsql translation is
/// asserted without a live database through <see cref="EntityFrameworkQueryableExtensions.ToQueryString{TEntity}"/>,
/// so a filtered, ordered include can never silently start throwing on the production provider.
/// </summary>
public sealed class RoomRehydrationQueryTests
{
    private const string DesignConnectionString =
        "Host=localhost;Port=5432;Database=karaokay;Username=postgres;Password=postgres";

    [Fact]
    public void OpenRoomQueryTranslatesWithOrderedQueueAndSongs()
    {
        var options = new DbContextOptionsBuilder<KaraokayDbContext>()
            .UseNpgsql(DesignConnectionString)
            .Options;

        using var database = new KaraokayDbContext(options);

        var sql = RoomRehydrator.OpenRoomsWithQueue(database.Rooms).ToQueryString();

        Assert.Contains("\"Rooms\"", sql);
        Assert.Contains("\"QueueItems\"", sql);
        Assert.Contains("\"Songs\"", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("\"ClosedAtUtc\" IS NULL", sql);
    }

    [Fact]
    public async Task RehydrationIsANoOpWithoutADatabase()
    {
        var registry = new RoomRegistry();
        var rehydrator = new RoomRehydrator(
            registry,
            new NoDatabaseScopeFactory(),
            NullLogger<RoomRehydrator>.Instance);

        var restored = await rehydrator.RehydrateAsync(CancellationToken.None);

        Assert.Equal(0, restored);
        Assert.Empty(registry.ActiveCodes);
    }
}
