using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Karaokay.Infrastructure.Persistence;

/// <summary>
/// Includes the provider name in the EF model cache key. The model differs per provider because
/// the <c>Song.VideoId</c> filtered index uses provider-specific SQL; without this, a test or host
/// that instantiates both providers in one process could reuse the wrong cached model.
/// </summary>
public sealed class KaraokayModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime) => (
        context.GetType(),
        context.Database.ProviderName,
        designTime);
}
