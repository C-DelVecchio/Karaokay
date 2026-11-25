using Karaokay.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Karaokay.Infrastructure.Persistence;

/// <summary>
/// EF Core model for the party-room domain (docs/ARCHITECTURE.md §4.2). The entity graph is
/// provider-agnostic; the only provider-specific fragment is the filtered unique index on
/// <c>Song.VideoId</c>, whose raw filter SQL is selected at model-configuration time through
/// <see cref="ProviderIndexFilters"/>.
/// </summary>
public sealed class KaraokayDbContext : DbContext
{
    /// <summary>Design-time fallback used only when no <c>KARAOKAY_DESIGN_CONNECTION</c> is set.</summary>
    internal const string DesignTimeConnectionString =
        "Host=localhost;Port=5432;Database=karaokay;Username=postgres;Password=postgres";

    public KaraokayDbContext(DbContextOptions<KaraokayDbContext> options)
        : base(options)
    {
    }

    /// <summary>Library of playable songs.</summary>
    public DbSet<Song> Songs => Set<Song>();

    /// <summary>Party rooms and their authoritative playback state.</summary>
    public DbSet<PartyRoom> Rooms => Set<PartyRoom>();

    /// <summary>Queue entries, densely ordered per room.</summary>
    public DbSet<QueueItem> QueueItems => Set<QueueItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, KaraokayModelCacheKeyFactory>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        ConfigureSong(modelBuilder);
        ConfigurePartyRoom(modelBuilder);
        ConfigureQueueItem(modelBuilder);
    }

    private void ConfigureSong(ModelBuilder modelBuilder)
    {
        var song = modelBuilder.Entity<Song>();
        song.ToTable("Songs");
        song.HasKey(entity => entity.Id);

        song.Property(entity => entity.Title).IsRequired().HasMaxLength(512);
        song.Property(entity => entity.Artist).HasMaxLength(512);
        song.Property(entity => entity.ThumbnailUrl).HasMaxLength(2048);
        song.Property(entity => entity.VideoId).HasMaxLength(64);
        song.Property(entity => entity.Channel).HasMaxLength(512);
        song.Property(entity => entity.InstrumentalPath).HasMaxLength(1024);
        song.Property(entity => entity.VocalsPath).HasMaxLength(1024);
        song.Property(entity => entity.LyricsPath).HasMaxLength(1024);
        song.Property(entity => entity.CachePath).HasMaxLength(1024);

        // Provider-specific raw SQL: PostgreSQL quotes identifiers with double quotes, SQL Server
        // uses brackets. Resolved here so the same entity graph migrates cleanly on both providers.
        song.HasIndex(entity => entity.VideoId)
            .IsUnique()
            .HasFilter(ProviderIndexFilters.VideoIdFilterFor(Database.ProviderName));

        song.HasIndex(entity => entity.Source);
        song.HasIndex(entity => entity.IsSyntheticDemo);
    }

    private static void ConfigurePartyRoom(ModelBuilder modelBuilder)
    {
        var room = modelBuilder.Entity<PartyRoom>();
        room.ToTable("Rooms");
        room.HasKey(entity => entity.Id);

        room.Property(entity => entity.Code).IsRequired().HasMaxLength(4);
        room.HasIndex(entity => entity.Code).IsUnique();
        room.HasIndex(entity => entity.CreatedAtUtc);

        room.HasMany(entity => entity.Queue)
            .WithOne(item => item.Room)
            .HasForeignKey(item => item.RoomId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureQueueItem(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<QueueItem>();
        item.ToTable("QueueItems");
        item.HasKey(entity => entity.Id);

        item.Property(entity => entity.RequestedByName).IsRequired().HasMaxLength(128);
        item.HasIndex(entity => new { entity.RoomId, entity.Position });
        item.HasIndex(entity => entity.SongId);

        item.HasOne(entity => entity.Song)
            .WithMany()
            .HasForeignKey(entity => entity.SongId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// Creates a <see cref="KaraokayDbContext"/> for <c>dotnet ef</c> without booting the API host.
/// The connection string is only used to build the model/migration SQL; no database is contacted
/// while scaffolding a migration.
/// </summary>
public sealed class KaraokayDbContextFactory : IDesignTimeDbContextFactory<KaraokayDbContext>
{
    public KaraokayDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("KARAOKAY_DESIGN_CONNECTION")
            ?? KaraokayDbContext.DesignTimeConnectionString;

        var options = new DbContextOptionsBuilder<KaraokayDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(KaraokayDbContext).Assembly.GetName().Name))
            .Options;

        return new KaraokayDbContext(options);
    }
}
