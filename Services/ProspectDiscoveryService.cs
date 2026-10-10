using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using GeoAPI.Geometries;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;
using OsmSharp;
using OsmSharp.Streams;
using OsmSharp.Tags;

namespace ArsanGazERP.Services;

public sealed class ProspectDiscoveryService
{
    private const string DatasetUrl = "https://download.geofabrik.de/europe/turkey-latest.osm.pbf";
    private const string ProviderName = "OpenStreetMap";
    private const int SaveBatchSize = 500;
    private static readonly GeometryFactory GeometryFactory = new();
    private static readonly TimeSpan DatasetRefreshInterval = TimeSpan.FromDays(7);
    private static readonly HttpClient DownloadClient = CreateDownloadClient();
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private static string StateDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "ProspectHunter");
    private static string DatasetDirectory => System.IO.Path.Combine(StateDirectory, "data");
    private static string DatasetPath => System.IO.Path.Combine(DatasetDirectory, "turkey-latest.osm.pbf");
    private static readonly string TemporaryDatasetPath = DatasetPath + ".part";
    private static string LegacyFailureStatePath => System.IO.Path.Combine(StateDirectory, "overpass-backoff.json");

    public static readonly string[] DefaultCities =
    {
        "Adana", "Mersin", "Hatay", "Osmaniye", "Gaziantep", "Şanlıurfa"
    };

    public async Task<ProspectScanResult> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await ScanGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(StateDirectory);
            DatasetState dataset = await EnsureDatasetAsync(progress, cancellationToken);
            IReadOnlyList<DiscoveredProspect> discovered = await Task.Run(
                () => ReadLocalDataset(dataset.Path, progress, cancellationToken),
                cancellationToken);

            ProspectScanResult result = await SaveProspectsAsync(discovered, dataset, progress, cancellationToken);
            if (System.IO.File.Exists(LegacyFailureStatePath))
            {
                System.IO.File.Delete(LegacyFailureStatePath);
            }

            return result;
        }
        finally
        {
            ScanGate.Release();
        }
    }

    private static async Task<DatasetState> EnsureDatasetAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DatasetDirectory);
        FileInfo? existing = System.IO.File.Exists(DatasetPath) ? new FileInfo(DatasetPath) : null;
        if (existing is { Length: > 0 }
            && DateTime.UtcNow - existing.LastWriteTimeUtc < DatasetRefreshInterval)
        {
            return new DatasetState(DatasetPath, existing.LastWriteTimeUtc, null);
        }

        try
        {
            await DownloadDatasetAsync(progress, cancellationToken);
            FileInfo downloaded = new(DatasetPath);
            return new DatasetState(DatasetPath, downloaded.LastWriteTimeUtc, null);
        }
        catch (Exception exception) when (
            existing is { Length: > 0 }
            && !cancellationToken.IsCancellationRequested
            && (exception is HttpRequestException or IOException or TaskCanceledException))
        {
            string warning = $"Geofabrik veri yenilemesi başarısız oldu; mevcut {existing.LastWriteTimeUtc.ToLocalTime():dd.MM.yyyy} tarihli yerel veri kullanılacak. Ayrıntı: {exception.Message}";
            progress?.Report(warning);
            return new DatasetState(DatasetPath, existing.LastWriteTimeUtc, warning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Türkiye OpenStreetMap veri kümesi indirilemedi. Ayrıntı: {exception.GetBaseException().Message}",
                exception);
        }
    }

    private static async Task DownloadDatasetAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, DatasetUrl);
        using HttpResponseMessage response = await DownloadClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        long? expectedLength = response.Content.Headers.ContentLength;
        if (expectedLength is null or <= 0)
        {
            throw new InvalidDataException("Geofabrik yanıtında doğrulanabilir dosya boyutu bulunmuyor.");
        }

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream output = new(
            TemporaryDatasetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            useAsync: true);

        byte[] buffer = new byte[1024 * 1024];
        long received = 0;
        long lastReportedMegabyte = -1;
        int bytesRead;
        while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            received += bytesRead;
            long downloadedMegabyte = received / (1024 * 1024);
            if (downloadedMegabyte > lastReportedMegabyte)
            {
                lastReportedMegabyte = downloadedMegabyte;
                progress?.Report($"Türkiye OSM verisi indiriliyor · {received / (1024 * 1024)} / {expectedLength.Value / (1024 * 1024)} MB");
            }
        }

        await output.FlushAsync(cancellationToken);
        if (received != expectedLength.Value)
        {
            throw new InvalidDataException(
                $"Türkiye OSM veri indirmesi eksik: beklenen {expectedLength.Value} bayt, alınan {received} bayt.");
        }

        await output.DisposeAsync();
        ValidatePbfHeader(TemporaryDatasetPath);
        System.IO.File.Move(TemporaryDatasetPath, DatasetPath, overwrite: true);
        progress?.Report($"Türkiye OSM veri kümesi hazır · {received / (1024 * 1024)} MB");
    }

    private static HttpClient CreateDownloadClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ArsanGazERP-ProspectHunter/9.0");
        return client;
    }

    private static void ValidatePbfHeader(string path)
    {
        using FileStream file = System.IO.File.OpenRead(path);
        PBFOsmStreamSource source = new(file);
        using IEnumerator<OsmGeo> enumerator = source.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            throw new InvalidDataException("İndirilen dosya geçerli veya dolu bir OpenStreetMap PBF değil.");
        }
    }

    private static IReadOnlyList<DiscoveredProspect> ReadLocalDataset(
        string path,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ScanTargets targets = FindTargetElements(path, progress, cancellationToken);
        CollectReferencedNodes(path, targets, progress, cancellationToken);

        Dictionary<string, List<IGeometry>> provinceBoundaries = DefaultCities.ToDictionary(
            NormalizeCityName,
            _ => new List<IGeometry>(),
            StringComparer.Ordinal);
        Dictionary<string, List<List<Node>>> provinceBoundaryPaths = DefaultCities.ToDictionary(
            NormalizeCityName,
            _ => new List<List<Node>>(),
            StringComparer.Ordinal);
        HashSet<string> citiesWithRelationBoundary = new(StringComparer.Ordinal);
        List<string> unmappedBoundaryNames = new();
        int mappedBoundaryElements = 0;
        int incompleteBoundaryWays = 0;
        List<PendingCandidate> candidates = new();
        Dictionary<long, Node> referencedNodes = new();
        Dictionary<long, Way> selectedWays = new();
        progress?.Report("Yerel PBF · il sınırları ve aday işletmeler okunuyor");

        long featureCount = 0;
        int candidateFeatureCount = 0;
        int namedCandidateCount = 0;
        using FileStream file = System.IO.File.OpenRead(path);
        foreach (OsmGeo element in new PBFOsmStreamSource(file))
        {
            bool isSelected = element switch
            {
                Node node => node.Id is long nodeId && targets.NodeIds.Contains(nodeId),
                Way way => way.Id is long wayId && targets.WayIds.Contains(wayId),
                Relation relation => relation.Id is long relationId && targets.RelationIds.Contains(relationId),
                _ => false
            };
            if (!isSelected)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            featureCount++;
            Dictionary<string, string> tags = ReadTags(element.Tags);
            if (element is Node selectedNode && selectedNode.Id is long selectedNodeId)
            {
                referencedNodes[selectedNodeId] = selectedNode;
            }
            else if (element is Way selectedWay && selectedWay.Id is long selectedWayId)
            {
                selectedWays[selectedWayId] = selectedWay;
            }

            if (IsAdministrativeProvinceBoundary(element.Tags))
            {
                string[] cities = ResolveCities(
                    First(tags, "name:tr", "name"),
                    First(tags, "official_name"),
                    First(tags, "alt_name"),
                    First(tags, "name:en"));
                if (cities.Length > 0)
                {
                    mappedBoundaryElements++;
                    List<Node> boundaryWayNodes = element is Way standaloneBoundaryWay
                        ? GetWayNodes(standaloneBoundaryWay, referencedNodes)
                        : new List<Node>();
                    if (element is Way && boundaryWayNodes.Count < 2)
                    {
                        incompleteBoundaryWays++;
                    }
                    List<List<Node>> relationPaths = element is Relation boundaryRelation
                        ? GetOuterPaths(boundaryRelation, selectedWays, referencedNodes)
                        : new List<List<Node>>();
                    foreach (string city in cities)
                    {
                        List<List<Node>> paths = provinceBoundaryPaths[NormalizeCityName(city)];
                        if (boundaryWayNodes.Count >= 2)
                        {
                            if (!citiesWithRelationBoundary.Contains(NormalizeCityName(city)))
                            {
                                paths.Add(boundaryWayNodes);
                            }
                        }
                        else if (relationPaths.Count > 0)
                        {
                            paths.Clear();
                            paths.AddRange(relationPaths);
                            citiesWithRelationBoundary.Add(NormalizeCityName(city));
                        }
                    }
                }
                else
                {
                    string name = First(tags, "name:tr", "name", "official_name", "alt_name", "name:en") ?? "(adı yok)";
                    unmappedBoundaryNames.Add($"{element.GetType().Name}: {name}");
                }

                continue;
            }

            if (!IsCandidate(null, tags))
            {
                continue;
            }

            candidateFeatureCount++;
            if (string.IsNullOrWhiteSpace(First(tags, "name")))
            {
                continue;
            }

            namedCandidateCount++;
            Coordinate? location;
            string? osmType;
            long? osmId = element.Id;
            switch (element)
            {
                case Node node:
                    location = GetNodeCoordinate(node);
                    osmType = "node";
                    break;
                case Way way:
                    location = GetWayRepresentativeLocation(way, referencedNodes);
                    osmType = "way";
                    break;
                case Relation relation when IsCandidateMultipolygon(element.Tags):
                    List<List<Node>> paths = GetOuterPaths(relation, selectedWays, referencedNodes);
                    location = BuildPolygons(paths).FirstOrDefault()?.InteriorPoint.Coordinate;
                    osmType = "relation";
                    break;
                default:
                    continue;
            }

            if (location is null || osmId is null)
            {
                continue;
            }

            if (osmType is null)
            {
                continue;
            }

            candidates.Add(new PendingCandidate(osmType, osmId.Value, location, tags));
            if (featureCount % 10_000 == 0)
            {
                progress?.Report($"Yerel PBF · {featureCount:N0} özellik incelendi, {candidates.Count:N0} aday bulundu");
            }
        }

        foreach (string city in DefaultCities)
        {
            List<List<Node>> paths = provinceBoundaryPaths[NormalizeCityName(city)];
            provinceBoundaries[NormalizeCityName(city)].AddRange(
                BuildPolygons(paths.Select(path => path.ToList()).ToList()));
        }

        string[] missingCities = DefaultCities
            .Where(city => provinceBoundaries[NormalizeCityName(city)].Count == 0)
            .ToArray();
        if (missingCities.Length > 0)
        {
            string boundaryDiagnostics = string.Join("; ", DefaultCities.Select(city =>
            {
                List<List<Node>> paths = provinceBoundaryPaths[NormalizeCityName(city)];
                int polygonCount = provinceBoundaries[NormalizeCityName(city)].Count;
                return $"{city}: {paths.Count} yol parçası, " +
                    $"{polygonCount} poligon, " +
                    $"{paths.Count(path => path.Count >= 4 && path[0].Id == path[^1].Id)} kapalı";
            }));
            string unmappedSummary = unmappedBoundaryNames.Count == 0
                ? string.Empty
                : " Eşleştirilemeyen admin_level=4 sınırları: " +
                    string.Join(", ", unmappedBoundaryNames.Distinct(StringComparer.Ordinal).Take(30));
            throw new InvalidDataException(
                "Türkiye PBF veri kümesinde admin_level=4 il sınırı poligonu üretilemedi: " +
                string.Join(", ", missingCities) + ". " + boundaryDiagnostics +
                $" Eşleşen sınır öğesi={mappedBoundaryElements}, koordinatı eksik sınır yolu={incompleteBoundaryWays}." +
                unmappedSummary);
        }

        List<DiscoveredProspect> discovered = new();
        int outsideProvinceCount = 0;
        foreach (PendingCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Point point = new(candidate.Location);
            string? city = DefaultCities.FirstOrDefault(candidateCity =>
                provinceBoundaries[NormalizeCityName(candidateCity)].Any(boundary => boundary.Covers(point)));
            if (city is not null)
            {
                discovered.Add(new DiscoveredProspect(city, candidate.OsmType, candidate.OsmId, candidate.Tags));
            }
            else
            {
                outsideProvinceCount++;
            }
        }

        if (candidates.Count > 0 && discovered.Count == 0)
        {
            string boundarySummary = string.Join("; ", DefaultCities.Select(city =>
            {
                List<IGeometry> boundaries = provinceBoundaries[NormalizeCityName(city)];
                return $"{city}: {boundaries.Count} geometri" +
                    (boundaries.Count == 0 ? string.Empty : $" {GetGeometryBounds(boundaries.SelectMany(boundary => boundary.Coordinates))}");
            }));
            Coordinate sample = candidates[0].Location;
            throw new InvalidDataException(
                $"PBF'de {candidates.Count:N0} aday var ancak hiçbiri il sınırlarına eşleşmedi. " +
                $"İlk aday koordinatı=({sample.X:F4}, {sample.Y:F4}); sınırlar: {boundarySummary}");
        }

        progress?.Report(
            $"Yerel PBF taraması tamamlandı · {featureCount:N0} özellik, {candidateFeatureCount:N0} aday etiketi, " +
            $"{namedCandidateCount:N0} isimli aday, {discovered.Count:N0} il içinde, {outsideProvinceCount:N0} il dışında");
        return discovered;
    }

    private static ScanTargets FindTargetElements(
        string path,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        HashSet<long> wayIds = new();
        HashSet<long> relationIds = new();
        HashSet<long> nodeIds = new();
        progress?.Report("Yerel PBF · aday işletme ve il sınırı kayıtları aranıyor (1/3)");

        using FileStream file = System.IO.File.OpenRead(path);
        long scanned = 0;
        foreach (OsmGeo osmGeo in new PBFOsmStreamSource(file))
        {
            if (++scanned % 250_000 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Yerel PBF · ilk geçiş {scanned:N0} kayıt");
            }

            if (osmGeo is Node node && node.Id is long nodeId && IsCandidate(node.Tags))
            {
                nodeIds.Add(nodeId);
            }
            else if (osmGeo is Way way
                && way.Id is long wayId
                && (IsCandidate(way.Tags) || IsAdministrativeProvinceBoundary(way.Tags)))
            {
                wayIds.Add(wayId);
            }
            else if (osmGeo is Relation relation
                && relation.Id is long relationId
                && (IsAdministrativeProvinceBoundary(relation.Tags) || IsCandidateMultipolygon(relation.Tags)))
            {
                relationIds.Add(relationId);
                if (relation.Members is null)
                {
                    continue;
                }

                foreach (RelationMember member in relation.Members)
                {
                    if (member.Type == OsmGeoType.Way && member.Id is long memberWayId)
                    {
                        wayIds.Add(memberWayId);
                    }
                }
            }
        }

        progress?.Report($"Yerel PBF · {nodeIds.Count:N0} nokta, {wayIds.Count:N0} yol ve {relationIds.Count:N0} ilişki seçildi");
        return new ScanTargets(nodeIds, wayIds, relationIds);
    }

    private static void CollectReferencedNodes(
        string path,
        ScanTargets targets,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Yerel PBF · seçilen yolların koordinatları hazırlanıyor (2/3)");
        using FileStream file = System.IO.File.OpenRead(path);
        long scanned = 0;
        foreach (OsmGeo osmGeo in new PBFOsmStreamSource(file))
        {
            if (++scanned % 250_000 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (osmGeo is not Way way || way.Id is not long wayId || !targets.WayIds.Contains(wayId) || way.Nodes is null)
            {
                continue;
            }

            foreach (long nodeId in way.Nodes)
            {
                targets.NodeIds.Add(nodeId);
            }
        }
    }

    private static async Task<ProspectScanResult> SaveProspectsAsync(
        IReadOnlyList<DiscoveredProspect> discovered,
        DatasetState dataset,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        int created = 0;
        int updated = 0;
        await using ArsanGazDbContext db = new();
        Dictionary<string, Prospect> existingByPlaceId = await db.Prospects
            .Where(item => item.Provider == ProviderName)
            .ToDictionaryAsync(item => item.ProviderPlaceId, StringComparer.Ordinal, cancellationToken);

        int processed = 0;
        foreach (DiscoveredProspect item in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, string> tags = item.Tags;
            string? companyName = First(tags, "name");
            if (string.IsNullOrWhiteSpace(companyName))
            {
                continue;
            }

            string providerId = $"{item.OsmType}/{item.OsmId}";
            Prospect prospect;
            if (existingByPlaceId.TryGetValue(providerId, out Prospect? existing))
            {
                prospect = existing ?? throw new InvalidOperationException($"OSM kaydı beklenmedik biçimde boş: {providerId}");
                updated++;
            }
            else
            {
                prospect = new Prospect
                {
                    Provider = ProviderName,
                    ProviderPlaceId = providerId,
                    FoundAtUtc = DateTime.UtcNow
                };
                db.Prospects.Add(prospect);
                existingByPlaceId.Add(providerId, prospect);
                created++;
            }

            string sector = DetectSector(tags);
            prospect.CompanyName = companyName.Trim();
            prospect.City = item.City;
            prospect.SectorQuery = sector;
            prospect.Address = BuildAddress(tags);
            prospect.Phone = First(tags, "contact:phone", "phone");
            prospect.Website = First(tags, "contact:website", "website");
            prospect.MapsUrl = $"https://www.openstreetmap.org/{item.OsmType}/{item.OsmId}";
            prospect.Categories = string.Join(", ", tags
                .Where(pair => pair.Key is "industrial" or "craft" or "healthcare" or "amenity" or "office" or "shop" or "man_made")
                .Select(pair => $"{pair.Key}={pair.Value}"));
            prospect.OpportunityScore = CalculateScore(sector, prospect.Phone, prospect.Website, tags);
            prospect.RecommendedProducts = RecommendProducts(sector);
            prospect.LastSeenAtUtc = DateTime.UtcNow;

            if (++processed % SaveBatchSize == 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                progress?.Report($"Yerel OSM adayları kaydediliyor · {processed:N0}/{discovered.Count:N0}");
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        string provider = $"OpenStreetMap Türkiye PBF / Geofabrik · veri {dataset.LastUpdatedUtc.ToLocalTime():dd.MM.yyyy}";
        if (dataset.Warning is not null)
        {
            provider += $" · uyarı: {dataset.Warning}";
        }

        return new ProspectScanResult(DefaultCities.Length, created, updated, DateTime.UtcNow, provider);
    }

    private static Dictionary<string, string> ReadTags(TagsCollectionBase? osmTags)
    {
        Dictionary<string, string> tags = new(StringComparer.OrdinalIgnoreCase);
        if (osmTags is null)
        {
            return tags;
        }

        foreach (Tag tag in osmTags)
        {
            tags[tag.Key] = tag.Value;
        }
        return tags;
    }

    private static Coordinate? GetNodeCoordinate(Node node)
    {
        if (node.Latitude is not double latitude || node.Longitude is not double longitude)
        {
            return null;
        }

        return new Coordinate(longitude, latitude);
    }

    private static Coordinate? GetWayRepresentativeLocation(Way way, IReadOnlyDictionary<long, Node> referencedNodes)
    {
        Coordinate[] coordinates = GetWayNodes(way, referencedNodes)
            .Select(GetNodeCoordinate)
            .Where(coordinate => coordinate is not null)
            .Select(coordinate => coordinate!)
            .ToArray();
        if (coordinates.Length == 0)
        {
            return null;
        }

        return coordinates.Length == 1
            ? coordinates[0]
            : GeometryFactory.CreateLineString(coordinates).Centroid.Coordinate;
    }

    private static List<Node> GetWayNodes(Way way, IReadOnlyDictionary<long, Node> referencedNodes)
    {
        if (way.Nodes is null || way.Nodes.Length == 0)
        {
            return new List<Node>();
        }

        List<Node> nodes = new(way.Nodes.Length);
        foreach (long nodeId in way.Nodes)
        {
            if (!referencedNodes.TryGetValue(nodeId, out Node? node))
            {
                return new List<Node>();
            }

            nodes.Add(node);
        }

        return nodes;
    }

    private static List<List<Node>> GetOuterPaths(
        Relation relation,
        IReadOnlyDictionary<long, Way> selectedWays,
        IReadOnlyDictionary<long, Node> referencedNodes) =>
        relation.Members
            .Where(member => !string.Equals(member.Role, "inner", StringComparison.OrdinalIgnoreCase)
                && member.Id is long wayId
                && selectedWays.ContainsKey(wayId))
            .Select(member => member.Id is long wayId && selectedWays.TryGetValue(wayId, out Way? way)
                ? GetWayNodes(way, referencedNodes)
                : new List<Node>())
            .Where(nodes => nodes.Count >= 2)
            .ToList();

    private static IReadOnlyList<IGeometry> BuildPolygons(List<List<Node>> paths)
    {
        ILineString[] lines = paths
            .Select(path => path
                .Select(GetNodeCoordinate)
                .Where(coordinate => coordinate is not null)
                .Select(coordinate => coordinate!)
                .ToArray())
            .Where(coordinates => coordinates.Length >= 2)
            .Select(GeometryFactory.CreateLineString)
            .ToArray();
        if (lines.Length == 0)
        {
            return Array.Empty<IGeometry>();
        }

        Polygonizer polygonizer = new();
        polygonizer.Add(GeometryFactory.CreateMultiLineString(lines));
        return polygonizer.GetPolygons()
            .Cast<IGeometry>()
            .Where(polygon => !polygon.IsEmpty && polygon.IsValid && polygon.Area > 0)
            .ToArray();
    }

    private static string GetGeometryBounds(IEnumerable<Coordinate> coordinates)
    {
        Coordinate[] points = coordinates.ToArray();
        if (points.Length == 0)
        {
            return "(boş)";
        }

        return $"X={points.Min(point => point.X):F2}..{points.Max(point => point.X):F2}, " +
            $"Y={points.Min(point => point.Y):F2}..{points.Max(point => point.Y):F2}";
    }

    private static bool IsAdministrativeProvinceBoundary(IReadOnlyDictionary<string, string> tags) =>
        First(tags, "boundary")?.Equals("administrative", StringComparison.OrdinalIgnoreCase) == true
        && First(tags, "admin_level") == "4";

    private static bool IsAdministrativeProvinceBoundary(TagsCollectionBase? tags) =>
        HasTag(tags, "boundary", "administrative")
        && HasTag(tags, "admin_level", "4");

    private static bool IsCandidateMultipolygon(TagsCollectionBase? tags) =>
        IsCandidate(tags)
        && First(tags, "type") is string type
        && (type.Equals("multipolygon", StringComparison.OrdinalIgnoreCase)
            || type.Equals("boundary", StringComparison.OrdinalIgnoreCase));

    private static bool IsCandidate(IReadOnlyDictionary<string, string> tags) =>
        IsCandidate((TagsCollectionBase?)null, tags);

    private static bool IsCandidate(TagsCollectionBase? tags) =>
        IsCandidate(tags, null);

    private static bool IsCandidate(TagsCollectionBase? osmTags, IReadOnlyDictionary<string, string>? dictionaryTags = null)
    {
        bool Has(string key, string value)
        {
            if (dictionaryTags is not null
                && dictionaryTags.TryGetValue(key, out string? dictionaryValue)
                && dictionaryValue.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HasTag(osmTags, key, value);
        }

        bool HasAny(string key, params string[] values) => values.Any(value => Has(key, value));
        return Has("man_made", "works")
            || HasAny("industrial", "metalworking", "chemical", "glass", "food", "machinery", "manufacturing", "metal", "pharmaceutical", "oil", "bakery", "brewery", "electronics", "textile", "warehouse")
            || HasAny("craft", "welder", "metal_construction", "metal_works", "industrial_equipment", "laboratory", "hvac", "car_repair")
            || HasAny("healthcare", "hospital", "clinic", "laboratory")
            || HasAny("amenity", "hospital", "clinic", "laboratory", "veterinary")
            || HasAny("office", "research", "logistics")
            || HasAny("shop", "medical_supply", "car_repair", "trade");
    }

    private static bool HasTag(TagsCollectionBase? tags, string key, string value) =>
        tags is not null
        && tags.TryGetValue(key, out string? existing)
        && existing.Equals(value, StringComparison.OrdinalIgnoreCase);

    private static string? First(IReadOnlyDictionary<string, string> tags, params string[] keys)
    {
        foreach (string key in keys)
            if (tags.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    private static string? First(TagsCollectionBase? tags, params string[] keys)
    {
        if (tags is null)
        {
            return null;
        }

        foreach (string key in keys)
        {
            if (tags.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string[] ResolveCities(params string?[] names)
    {
        HashSet<string> cities = new(StringComparer.Ordinal);
        foreach (string? name in names.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            foreach (string alias in name!.Split([';', '|', '/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string normalized = NormalizeCityName(alias);
                foreach (string city in DefaultCities.Where(candidate =>
                {
                    string normalizedCity = NormalizeCityName(candidate);
                    return normalized == normalizedCity
                        || normalized.StartsWith(normalizedCity, StringComparison.Ordinal)
                        || normalized.Contains(normalizedCity, StringComparison.Ordinal);
                }))
                {
                    cities.Add(city);
                }
            }
        }

        return cities.ToArray();
    }

    private static string NormalizeCityName(string name)
    {
        string decomposed = name.Normalize(NormalizationForm.FormD);
        StringBuilder normalized = new(decomposed.Length);
        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            char lower = char.ToLowerInvariant(character);
            lower = lower == 'ı' ? 'i' : lower;
            if (char.IsLetterOrDigit(lower))
            {
                normalized.Append(lower);
            }
        }

        return normalized.ToString();
    }

    private static string DetectSector(IReadOnlyDictionary<string, string> tags)
    {
        string value = string.Join(" ", new[]
        {
            First(tags, "industrial", "craft", "healthcare", "amenity", "office", "shop", "man_made") ?? string.Empty,
            First(tags, "name") ?? string.Empty,
            First(tags, "description") ?? string.Empty
        }).ToLowerInvariant();

        if (value.Contains("hospital") || value.Contains("clinic") || value.Contains("health")) return "Sağlık kuruluşu";
        if (value.Contains("laboratory") || value.Contains("research")) return "Laboratuvar / araştırma";
        if (value.Contains("metal") || value.Contains("welder")) return "Metal işleme / kaynak";
        if (value.Contains("food") || value.Contains("beverage")) return "Gıda üretimi";
        if (value.Contains("glass")) return "Cam üretimi";
        if (value.Contains("chemical")) return "Kimya sanayi";
        if (value.Contains("party")) return "Organizasyon / balon";
        if (value.Contains("logistics") || value.Contains("warehouse")) return "Lojistik / depo";
        if (value.Contains("car") || value.Contains("automotive")) return "Otomotiv";
        return "Sanayi / ticari işletme";
    }

    private static string? BuildAddress(IReadOnlyDictionary<string, string> tags)
    {
        string? full = First(tags, "addr:full");
        if (!string.IsNullOrWhiteSpace(full)) return full;
        string[] parts =
        {
            First(tags, "addr:street") ?? string.Empty,
            First(tags, "addr:housenumber") ?? string.Empty,
            First(tags, "addr:district", "addr:suburb") ?? string.Empty,
            First(tags, "addr:city") ?? string.Empty
        };
        string result = string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static int CalculateScore(string sector, string? phone, string? website, IReadOnlyDictionary<string, string> tags)
    {
        int score = 35;
        if (!string.IsNullOrWhiteSpace(phone)) score += 20;
        if (!string.IsNullOrWhiteSpace(website)) score += 15;
        if (sector.Contains("Metal") || sector.Contains("Kimya") || sector.Contains("Cam")) score += 20;
        if (sector.Contains("Sağlık") || sector.Contains("Laboratuvar")) score += 15;
        if (tags.ContainsKey("opening_hours")) score += 5;
        return Math.Min(score, 100);
    }

    private static string RecommendProducts(string sector) => sector switch
    {
        var value when value.Contains("Metal") => "Oksijen, Argon, Asetilen, Azot",
        var value when value.Contains("Gıda") || value.Contains("depo") => "Azot, Karbondioksit, Kuru Hava",
        var value when value.Contains("Sağlık") || value.Contains("Laboratuvar") => "Oksijen, Azot, Helyum, Kuru Hava",
        var value when value.Contains("balon") => "Balon Gazı, Helyum",
        var value when value.Contains("Kimya") => "Azot, Hidrojen, Argon, Kuru Hava",
        _ => "Azot, Oksijen, Argon"
    };

    private sealed record DatasetState(string Path, DateTime LastUpdatedUtc, string? Warning);
    private sealed record ScanTargets(HashSet<long> NodeIds, HashSet<long> WayIds, HashSet<long> RelationIds);
    private sealed record PendingCandidate(string OsmType, long OsmId, Coordinate Location, Dictionary<string, string> Tags);
    private sealed record DiscoveredProspect(string City, string OsmType, long OsmId, Dictionary<string, string> Tags);
}

public sealed record ProspectScanResult(int CityCount, int CreatedCount, int UpdatedCount, DateTime CompletedAtUtc, string Provider)
{
    public override string ToString() =>
        $"Ücretsiz tarama tamamlandı · kaynak {Provider} · il {CityCount} · yeni aday {CreatedCount} · güncellenen {UpdatedCount}";
}