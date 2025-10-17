using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Idb.Libraries.Abstractions.Services.Indexes;

namespace Idb.Libraries.Implementation.Services.Indexes;

internal sealed class RepositoryIndexManager : IRepositoryIndexManager
{
    private readonly ConcurrentDictionary<Type, ContractIndexCatalog> _catalogs = new();

    public void ConfigureIndex(IRepositoryIndexConfiguration configuration)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        var contractType = configuration.ContractType ?? throw new ArgumentException("Index configuration must specify a contract type.", nameof(configuration));

        var catalog = _catalogs.GetOrAdd(contractType, static _ => new ContractIndexCatalog());
        catalog.Configurations[configuration.Name] = configuration;

        IRepositoryIndex? index = configuration.Kind switch
        {
            IndexTypeEnum.SortedList => CreateSortedListIndex(configuration),
            _ => null
        };

        if (index is not null)
        {
            catalog.RegisterIndex(index);
        }
    }

    public void ConfigureIndexes(IEnumerable<IRepositoryIndexConfiguration> configurations)
    {
        if (configurations is null)
        {
            throw new ArgumentNullException(nameof(configurations));
        }

        foreach (var configuration in configurations)
        {
            ConfigureIndex(configuration);
        }
    }

    public IReadOnlyCollection<IRepositoryIndexConfiguration> GetIndexConfigurations(Type contractType)
    {
        if (contractType is null)
        {
            throw new ArgumentNullException(nameof(contractType));
        }

        return _catalogs.TryGetValue(contractType, out var catalog)
            ? catalog.Configurations.Values.ToArray()
            : Array.Empty<IRepositoryIndexConfiguration>();
    }

    public Dictionary<string, object?> UpdateIndexes<TContract>(TContract entity)
        where TContract : class
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        if (!_catalogs.TryGetValue(typeof(TContract), out var catalog))
        {
            return new Dictionary<string, object?>(0, StringComparer.OrdinalIgnoreCase);
        }

        var keys = new Dictionary<string, object?>(catalog.IndexCount, StringComparer.OrdinalIgnoreCase);
        foreach (var index in catalog.EnumerateIndexes())
        {
            keys[index.Name] = index.Add(entity);
        }

        return keys;
    }

    public bool TryGetSortedListIndex<TContract, TKey>(
        string name,
        out SortedList<TKey, List<TContract>> index)
        where TContract : class
        where TKey : notnull
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Index name cannot be null or whitespace.", nameof(name));
        }

        if (_catalogs.TryGetValue(typeof(TContract), out var catalog) &&
            catalog.SortedListIndexes.TryGetValue(name, out var sorted) &&
            sorted.TryAs<TContract, TKey>(out var typed))
        {
            index = typed;
            return true;
        }

        index = null!;
        return false;
    }

    public RepositoryIndexSelection? SelectBestIndex(IndexSelectionRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.ContractType is null)
        {
            throw new ArgumentException("Selection request must include a contract type.", nameof(request));
        }

        if (!_catalogs.TryGetValue(request.ContractType, out var catalog))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.PreferredIndex) &&
            catalog.Indexes.TryGetValue(request.PreferredIndex, out var preferred))
        {
            preferred.TryExtractKey(request.Probe!, out var preferredKey, out var preferredMatches);
            var preferredScore = ComputeScore(preferred, preferredMatches, isPreferred: true, catalog);
            var preferredFullMatch = preferredMatches >= preferred.KeyMembers.Count && preferred.KeyMembers.Count > 0;
            return new RepositoryIndexSelection(preferred, preferredKey, preferredScore, preferredMatches, preferredFullMatch, true);
        }

        if (request.Operation == IndexOperationKind.Write || request.Probe is null)
        {
            return null;
        }

        var memberSnapshot = RepositoryIndexProbeAccessor.GetMemberSnapshot(request.Probe);
        var candidateIndexes = catalog.ResolveCandidateIndexes(memberSnapshot.Keys);

        RepositoryIndexSelection? best = null;
        foreach (var index in candidateIndexes)
        {
            if (!index.TryExtractKey(request.Probe, out var key, out var matchedMembers))
            {
                continue;
            }

            var isFullMatch = matchedMembers >= index.KeyMembers.Count && index.KeyMembers.Count > 0;
            var score = ComputeScore(index, matchedMembers, isPreferred: false, catalog);

            if (best is null || score > best.Score)
            {
                best = new RepositoryIndexSelection(index, key, score, matchedMembers, isFullMatch, false);
            }
        }

        return best;
    }

    public bool TryGetIndex(Type contractType, string name, out IRepositoryIndex index)
    {
        if (contractType is null)
        {
            throw new ArgumentNullException(nameof(contractType));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Index name cannot be null or whitespace.", nameof(name));
        }

        if (_catalogs.TryGetValue(contractType, out var catalog) &&
            catalog.Indexes.TryGetValue(name, out var located) &&
            located is not null)
        {
            index = located;
            return true;
        }

        index = null!;
        return false;
    }

    private static IRepositoryIndex CreateSortedListIndex(IRepositoryIndexConfiguration configuration)
    {
        if (configuration is not ISortedListIndexFactory factory)
        {
            throw new InvalidOperationException($"Index configuration '{configuration.GetType().FullName}' must implement {nameof(ISortedListIndexFactory)}.");
        }

        return factory.CreateIndex();
    }

    private static double ComputeScore(IRepositoryIndex index, int matchedMembers, bool isPreferred, ContractIndexCatalog catalog)
    {
        var totalMembers = index.KeyMembers.Count == 0 ? 1 : index.KeyMembers.Count;
        var matchRatio = Math.Min(1d, matchedMembers / (double)totalMembers);
        var score = matchRatio * 10d;

        if (catalog.TryGetSelectivity(index.Name, out var selectivityWeight))
        {
            score += selectivityWeight;
        }

        if (isPreferred)
        {
            score += 5d;
        }

        return score;
    }

    private sealed class ContractIndexCatalog
    {
        private readonly ConcurrentDictionary<string, IRepositoryIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ISortedListIndex> _sortedListIndexes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IRepositoryIndexConfiguration> _configurations = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IRepositoryIndex>> _indexesByRootMember = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _indexMemberPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, double> _selectivityScores = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentDictionary<string, IRepositoryIndexConfiguration> Configurations => _configurations;
        public ConcurrentDictionary<string, ISortedListIndex> SortedListIndexes => _sortedListIndexes;
        public ConcurrentDictionary<string, IRepositoryIndex> Indexes => _indexes;
        public int IndexCount => _indexes.Count;

        public void RegisterIndex(IRepositoryIndex index)
        {
            if (index is null)
            {
                return;
            }

            _indexes[index.Name] = index;
            if (index is ISortedListIndex sorted)
            {
                _sortedListIndexes[index.Name] = sorted;
            }

            UpdateSelectivity(index.Name);
            UpdateMemberMappings(index.Name, index.KeyMembers);
        }

        public bool TryGetSelectivity(string indexName, out double score)
            => _selectivityScores.TryGetValue(indexName, out score);

        public IEnumerable<IRepositoryIndex> EnumerateIndexes()
            => _indexes.Values;

        public IEnumerable<IRepositoryIndex> ResolveCandidateIndexes(IEnumerable<string> memberNames)
        {
            var candidates = new HashSet<IRepositoryIndex>();

            foreach (var memberName in memberNames)
            {
                if (string.IsNullOrWhiteSpace(memberName))
                {
                    continue;
                }

                if (_indexesByRootMember.TryGetValue(memberName, out var map))
                {
                    foreach (var index in map.Values)
                    {
                        candidates.Add(index);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                foreach (var index in _indexes.Values)
                {
                    candidates.Add(index);
                }
            }

            return candidates;
        }

        public void UpdateConfiguration(IRepositoryIndexConfiguration configuration)
        {
            _configurations[configuration.Name] = configuration;
            UpdateSelectivity(configuration.Name);
        }

        private void UpdateSelectivity(string indexName)
        {
            if (!_configurations.TryGetValue(indexName, out var configuration))
            {
                return;
            }

            if (configuration.Settings is null || configuration.Settings.Count == 0)
            {
                _selectivityScores.TryRemove(indexName, out _);
                return;
            }

            if (TryGetNumericSetting(configuration.Settings, "SelectivityScore", out var score) ||
                TryGetNumericSetting(configuration.Settings, "Selectivity", out score))
            {
                _selectivityScores[indexName] = score;
            }
            else
            {
                _selectivityScores.TryRemove(indexName, out _);
            }
        }

        private static bool TryGetNumericSetting(IReadOnlyDictionary<string, object?> settings, string key, out double value)
        {
            value = 0d;

            if (!settings.TryGetValue(key, out var raw) || raw is null)
            {
                return false;
            }

            try
            {
                value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (InvalidCastException)
            {
                return false;
            }
        }

        private void UpdateMemberMappings(string indexName, IReadOnlyList<string> members)
        {
            if (_indexMemberPaths.TryRemove(indexName, out var existingMembers))
            {
                RemoveMemberMappings(indexName, existingMembers);
            }

            if (members.Count == 0)
            {
                return;
            }

            _indexMemberPaths[indexName] = members;

            var rootMember = members[0];
            if (string.IsNullOrWhiteSpace(rootMember))
            {
                return;
            }

            var map = _indexesByRootMember.GetOrAdd(rootMember, static _ => new ConcurrentDictionary<string, IRepositoryIndex>(StringComparer.OrdinalIgnoreCase));
            if (_indexes.TryGetValue(indexName, out var index) && index is not null)
            {
                map[indexName] = index;
            }
        }

        private void RemoveMemberMappings(string indexName, IReadOnlyList<string> members)
        {
            if (members.Count == 0)
            {
                return;
            }

            var rootMember = members[0];
            if (!_indexesByRootMember.TryGetValue(rootMember, out var map))
            {
                return;
            }

            map.TryRemove(indexName, out _);
            if (map.IsEmpty)
            {
                _indexesByRootMember.TryRemove(rootMember, out _);
            }
        }
    }
}

