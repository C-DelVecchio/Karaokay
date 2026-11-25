using System.Collections.Concurrent;
using Karaokay.Application.Downloads;

namespace Karaokay.UnitTests;

/// <summary>
/// Single-flight coalescing for one YouTube video (docs/ARCHITECTURE.md §5.1). Two enqueue requests
/// for the same id must share one ticket and therefore one download; terminal tickets must be
/// dropped so a later request starts a fresh attempt.
/// </summary>
public sealed class DownloadJobCoordinatorTests
{
    private const string VideoId = "dQw4w9WgXcQ";
    private static readonly Guid SongId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task ConcurrentEnqueuesForTheSameVideoShareOneTicketAndOneDownload()
    {
        var coordinator = new DownloadJobCoordinator();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winners = 0;
        var tickets = new ConcurrentBag<DownloadJobTicket>();

        async Task<DownloadJobTicket> EnqueueAsync(Guid queueItemId)
        {
            await gate.Task;
            var ticket = coordinator.GetOrCreate(VideoId, SongId, out var created);
            tickets.Add(ticket);

            if (created)
            {
                Interlocked.Increment(ref winners);
            }

            // Every request attaches its queue row before the shared download finishes.
            Assert.True(ticket.TryAddTarget(new DownloadTarget(queueItemId, "ABCD")));
            return ticket;
        }

        var first = EnqueueAsync(Guid.NewGuid());
        var second = EnqueueAsync(Guid.NewGuid());
        gate.SetResult();

        var results = await Task.WhenAll(first, second);
        var shared = Assert.Single(results.Distinct());

        // The winner owns the download; the loser only attached to the same ticket.
        Assert.Equal(1, winners);
        Assert.Equal(1, coordinator.ActiveCount);
        Assert.Equal(2, shared.Targets.Count);
        Assert.Same(shared, coordinator.Find(VideoId));

        // One completion resolves every attached request.
        var targets = shared.Complete(new DownloadJobResult(true, null));
        Assert.Equal(2, targets.Count);
        var outcome = await shared.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(outcome.Success);
        Assert.Null(outcome.FailureReason);

        // A second completion is a no-op, so attached clients never receive duplicate terminals.
        Assert.Empty(shared.Complete(new DownloadJobResult(false, "duplicate")));
        Assert.True((await shared.Completion.Task).Success);
    }

    [Fact]
    public async Task ManyConcurrentEnqueuesStillCreateExactlyOneTicket()
    {
        var coordinator = new DownloadJobCoordinator();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createdCount = 0;
        var ticketIds = new ConcurrentDictionary<DownloadJobTicket, byte>();

        var tasks = Enumerable.Range(0, 32).Select(async index =>
        {
            await gate.Task;
            var ticket = coordinator.GetOrCreate(VideoId, SongId, out var created);
            if (created)
            {
                Interlocked.Increment(ref createdCount);
            }

            ticketIds.TryAdd(ticket, 0);
            ticket.TryAddTarget(new DownloadTarget(Guid.NewGuid(), "ABCD"));
        });

        var pending = Task.WhenAll(tasks);
        gate.SetResult();
        await pending;

        Assert.Equal(1, createdCount);
        Assert.Single(ticketIds);
        Assert.Equal(32, coordinator.Find(VideoId)!.Targets.Count);
    }

    [Fact]
    public void DifferentVideoIdsGetSeparateTickets()
    {
        var coordinator = new DownloadJobCoordinator();

        var first = coordinator.GetOrCreate("dQw4w9WgXcQ", SongId, out var firstCreated);
        var second = coordinator.GetOrCreate("e2eTestVid1", SongId, out var secondCreated);

        Assert.True(firstCreated);
        Assert.True(secondCreated);
        Assert.NotSame(first, second);
        Assert.Equal(2, coordinator.ActiveCount);
        Assert.Same(first, coordinator.Find("dQw4w9WgXcQ"));
        Assert.Same(second, coordinator.Find("e2eTestVid1"));
    }

    [Fact]
    public void TerminalTicketIsRemovedSoALaterEnqueueStartsFresh()
    {
        var coordinator = new DownloadJobCoordinator();
        var first = coordinator.GetOrCreate(VideoId, SongId, out var created);
        Assert.True(created);
        first.Complete(new DownloadJobResult(true, null));

        var second = coordinator.GetOrCreate(VideoId, SongId, out var recreated);

        Assert.True(recreated);
        Assert.NotSame(first, second);
        Assert.Same(second, coordinator.Find(VideoId));
        Assert.Equal(1, coordinator.ActiveCount);
    }

    [Fact]
    public void TryRemoveOnlyRemovesTheSameTicketInstance()
    {
        var coordinator = new DownloadJobCoordinator();
        var first = coordinator.GetOrCreate(VideoId, SongId, out _);
        first.Complete(new DownloadJobResult(true, null));
        var second = coordinator.GetOrCreate(VideoId, SongId, out _);

        Assert.False(coordinator.TryRemove(first));
        Assert.Same(second, coordinator.Find(VideoId));
        Assert.True(coordinator.TryRemove(second));
        Assert.Null(coordinator.Find(VideoId));
    }

    [Fact]
    public void FindRejectsNullOrUnknownIdsWithoutThrowing()
    {
        var coordinator = new DownloadJobCoordinator();

        Assert.Null(coordinator.Find(null!));
        Assert.Null(coordinator.Find(""));
        Assert.Null(coordinator.Find("unknown"));

        var ticket = coordinator.GetOrCreate(VideoId, SongId, out _);
        ticket.Complete(new DownloadJobResult(false, "boom"));
        // Find still returns a terminal ticket until a request rotates it.
        Assert.Same(ticket, coordinator.Find(VideoId));
    }
}
