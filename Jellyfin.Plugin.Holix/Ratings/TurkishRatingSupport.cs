using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Holix.Ratings;

/// <summary>
/// At start-up: adds <see cref="TurkishRatingAliases"/> to Jellyfin's rating lookup, then recomputes the stored
/// rating score of existing items that carry such a label (and their descendants, e.g. the episodes of a
/// "Genel İzleyici" series) so library queries for child profiles see them. Only Jellyfin's derived score
/// (<c>InheritedParentalRatingValue</c>, the same value a metadata refresh writes) is updated; the item's
/// <c>OfficialRating</c> is never changed. New and refreshed items are scored by Jellyfin itself.
/// </summary>
public sealed class TurkishRatingSupport : IHostedService, IDisposable
{
    private readonly ILocalizationManager _localization;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TurkishRatingSupport> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _backfill;

    /// <summary>Initializes a new instance of the <see cref="TurkishRatingSupport"/> class.</summary>
    /// <param name="localization">The localization manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public TurkishRatingSupport(ILocalizationManager localization, ILibraryManager libraryManager, ILogger<TurkishRatingSupport> logger)
    {
        _localization = localization;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!RatingAliasInstaller.TryInstall(_localization, TurkishRatingAliases.SystemKey, TurkishRatingAliases.All, out var error))
        {
            _logger.LogWarning("Holix: Turkish rating aliases not installed ({Error}); labels such as \"Genel İzleyici\" stay unrated", error);
            return Task.CompletedTask;
        }

        _logger.LogInformation("Holix: {Count} Turkish rating aliases installed", TurkishRatingAliases.All.Count);
        _backfill = Task.Run(() => Backfill(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_backfill is not null)
        {
            await _backfill.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    private async Task Backfill(CancellationToken ct)
    {
        try
        {
            var matches = _libraryManager.GetItemList(new InternalItemsQuery
            {
                OfficialRatings = TurkishRatingAliases.All.Keys.ToArray(),
                Recursive = true,
            });

            var seen = new HashSet<Guid>();
            var updated = 0;
            foreach (var item in matches)
            {
                var candidates = item is Folder folder
                    ? folder.GetRecursiveChildren(i => i.InheritedParentalRatingValue is null).Prepend(item)
                    : [item];
                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (candidate.InheritedParentalRatingValue is not null || !seen.Add(candidate.Id))
                    {
                        continue;
                    }

                    var change = candidate.OnMetadataChanged();
                    if (change != ItemUpdateType.None)
                    {
                        await candidate.UpdateToRepositoryAsync(change, ct).ConfigureAwait(false);
                        updated++;
                    }
                }
            }

            if (updated > 0)
            {
                _logger.LogInformation("Holix: rating score updated for {Count} items with Turkish rating labels", updated);
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A failed backfill must not affect the server; a metadata refresh fixes the items too.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Holix: updating rating scores of existing items failed; refresh their metadata instead");
        }
    }
}
