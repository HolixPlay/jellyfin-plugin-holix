using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.Holix.Households;

/// <summary>
/// Persistent store for the household ↔ profile mapping (<c>households.json</c>). Thread-safe;
/// every change is written atomically (temporary file + rename). Reads return copies, so mutating a
/// returned object does not change the store.
/// </summary>
public sealed class HouseholdStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _lock = new();
    private List<Household>? _households;

    /// <summary>Initializes a new instance of the <see cref="HouseholdStore"/> class.</summary>
    /// <param name="path">Path of the JSON file.</param>
    public HouseholdStore(string path)
    {
        _path = path;
    }

    /// <summary>Finds the user's role in a household.</summary>
    /// <param name="userId">Jellyfin user id.</param>
    /// <returns>The membership.</returns>
    public HouseholdMembership Resolve(Guid userId)
    {
        lock (_lock)
        {
            foreach (var h in Load())
            {
                if (h.OwnerUserId == userId)
                {
                    return new HouseholdMembership(HouseholdRole.Owner, Clone(h), null);
                }

                var p = h.Profiles.FirstOrDefault(x => x.UserId == userId);
                if (p is not null)
                {
                    var copy = Clone(h);
                    return new HouseholdMembership(HouseholdRole.Profile, copy, copy.Profiles.First(x => x.UserId == userId));
                }
            }

            return HouseholdMembership.None;
        }
    }

    /// <summary>Whether the user is a guest profile in any household.</summary>
    /// <param name="userId">Jellyfin user id.</param>
    /// <returns><c>true</c> if guest.</returns>
    public bool IsGuest(Guid userId)
    {
        lock (_lock)
        {
            return Load().Any(h => h.Profiles.Any(p => p.UserId == userId && p.Kind == ProfileKind.Guest));
        }
    }

    /// <summary>All households (copies).</summary>
    /// <returns>The list.</returns>
    public IReadOnlyList<Household> All()
    {
        lock (_lock)
        {
            return Load().Select(Clone).ToList();
        }
    }

    /// <summary>
    /// Atomically modifies a household. <paramref name="mutate"/> runs under the lock;
    /// if it throws, nothing is written.
    /// </summary>
    /// <param name="ownerUserId">Owner.</param>
    /// <param name="mutate">The change; receives <c>null</c> if there is no household, returning <c>null</c> deletes it.</param>
    /// <returns>The new household (copy) or <c>null</c>.</returns>
    public Household? Update(Guid ownerUserId, Func<Household?, Household?> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_lock)
        {
            var list = Load().Select(Clone).ToList();
            var index = list.FindIndex(h => h.OwnerUserId == ownerUserId);
            var current = index >= 0 ? list[index] : null;
            var next = mutate(current);

            if (next is not null)
            {
                if (next.OwnerUserId != ownerUserId)
                {
                    throw new InvalidOperationException("The household owner cannot be changed.");
                }

                Validate(list, next, index);
            }

            if (index >= 0)
            {
                list.RemoveAt(index);
            }

            if (next is not null)
            {
                list.Add(next);
            }

            Save(list);
            _households = list;
            return next is null ? null : Clone(next);
        }
    }

    /// <summary>Removes Jellyfin users that no longer exist from the mapping.</summary>
    /// <param name="exists">Whether a user exists.</param>
    /// <returns>Number of removed entries (the whole household if the owner was deleted).</returns>
    public int Prune(Func<Guid, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        lock (_lock)
        {
            var list = Load().Select(Clone).ToList();
            var removed = list.RemoveAll(h => !exists(h.OwnerUserId));
            foreach (var h in list)
            {
                removed += h.Profiles.RemoveAll(p => !exists(p.UserId));
            }

            if (removed > 0)
            {
                Save(list);
                _households = list;
            }

            return removed;
        }
    }

    private static void Validate(List<Household> list, Household next, int ownIndex)
    {
        var ids = new HashSet<Guid> { next.OwnerUserId };
        foreach (var p in next.Profiles)
        {
            if (!ids.Add(p.UserId))
            {
                throw new InvalidOperationException($"User {p.UserId} appears twice in the household.");
            }
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (i == ownIndex)
            {
                continue;
            }

            var other = list[i];
            if (ids.Contains(other.OwnerUserId) || other.Profiles.Any(p => ids.Contains(p.UserId)))
            {
                throw new InvalidOperationException("A Jellyfin user can belong to only one household.");
            }
        }
    }

    private static Household Clone(Household h) =>
        JsonSerializer.Deserialize<Household>(JsonSerializer.Serialize(h, JsonOptions), JsonOptions)!;

    private List<Household> Load()
    {
        if (_households is not null)
        {
            return _households;
        }

        if (!File.Exists(_path))
        {
            _households = [];
            return _households;
        }

        using var stream = File.OpenRead(_path);
        _households = JsonSerializer.Deserialize<List<Household>>(stream, JsonOptions) ?? [];
        return _households;
    }

    private void Save(List<Household> list)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
