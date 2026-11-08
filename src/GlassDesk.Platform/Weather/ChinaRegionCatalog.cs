using System.Text.Json;

namespace GlassDesk.Platform;

public sealed record ChinaRegion(int Code, string Name, int ParentCode, int Level, double Latitude, double Longitude);

public static class ChinaRegionCatalog
{
    private const string ResourceName = "GlassDesk.Platform.Weather.china-regions.json";
    private const double EarthRadiusKm = 6371;
    private static readonly IReadOnlyList<ChinaRegion> Empty = Array.AsReadOnly(Array.Empty<ChinaRegion>());
    private static readonly string[] Suffixes =
    [
        "特别行政区", "自治州", "自治县", "自治区", "自治旗", "地区", "林区", "省", "市", "区", "县", "盟", "旗"
    ];
    private static readonly Lazy<CatalogData> Catalog = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<ChinaRegion> Provinces => Catalog.Value.Provinces;

    public static IReadOnlyList<ChinaRegion> Cities(int provinceCode)
    {
        CatalogData data = Catalog.Value;
        if (!data.ByCode.TryGetValue(provinceCode, out ChinaRegion? province) || province.Level != 1)
            return Empty;

        IReadOnlyList<ChinaRegion> cities = ChildrenOf(data, provinceCode, 2);
        if (cities.Count > 0) return cities;
        return ChildrenOf(data, provinceCode, 3).Count > 0 ? Array.AsReadOnly(new[] { province! }) : Empty;
    }

    public static IReadOnlyList<ChinaRegion> Districts(int cityCode)
    {
        CatalogData data = Catalog.Value;
        if (!data.ByCode.TryGetValue(cityCode, out ChinaRegion? city)) return Empty;
        return ChildrenOf(data, cityCode, 3);
    }

    public static ChinaRegion? Find(int code) =>
        Catalog.Value.ByCode.TryGetValue(code, out ChinaRegion? region) ? region : null;

    public static string PathOf(int code)
    {
        CatalogData data = Catalog.Value;
        if (!data.ByCode.TryGetValue(code, out ChinaRegion? region)) return "";
        if (region.Level == 1) return region.Name;
        if (!data.ByCode.TryGetValue(region.ParentCode, out ChinaRegion? parent)) return "";
        if (region.Level == 2) return $"{parent.Name} · {region.Name}";
        if (region.Level != 3) return "";
        if (parent.Level == 1) return $"{parent.Name} · {region.Name}";
        if (parent.Level != 2 || !data.ByCode.TryGetValue(parent.ParentCode, out ChinaRegion? province)) return "";
        return $"{province.Name} · {parent.Name} · {region.Name}";
    }

    public static IReadOnlyList<ChinaRegion> Search(string? query, int max = 12)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0) return Empty;
        string term = query.Trim();
        string shortTerm = WithoutSuffix(term);
        CatalogData data = Catalog.Value;
        return data.Regions
            .Select(region => (Region: region, Score: SearchScore(region, term, shortTerm)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Region.Level)
            .ThenBy(item => item.Region.Code)
            .Take(max)
            .Select(item => item.Region)
            .ToArray();
    }

    public static (ChinaRegion Region, double DistanceKm)? Nearest(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180)
            return null;

        CatalogData data = Catalog.Value;
        IReadOnlyList<ChinaRegion> candidates = data.Regions.Where(region => region.Level == 3).ToArray();
        if (candidates.Count == 0)
            candidates = data.Regions.Where(region => region.Level == 2).ToArray();
        if (candidates.Count == 0) return null;

        ChinaRegion? nearest = null;
        double nearestDistance = double.PositiveInfinity;
        foreach (ChinaRegion region in candidates)
        {
            double distance = DistanceKm(latitude, longitude, region.Latitude, region.Longitude);
            if (distance < nearestDistance)
            {
                nearest = region;
                nearestDistance = distance;
            }
        }
        return nearest is null ? null : (nearest, nearestDistance);
    }

    public static ChinaRegion? MatchByNames(string? province, string? city)
    {
        if (string.IsNullOrWhiteSpace(province)) return null;
        string provinceName = province.Trim();
        string provinceShort = WithoutSuffix(provinceName);
        ChinaRegion? matchedProvince = Provinces.FirstOrDefault(region =>
            region.Name.StartsWith(provinceName, StringComparison.Ordinal) ||
            string.Equals(WithoutSuffix(region.Name), provinceName, StringComparison.Ordinal) ||
            string.Equals(WithoutSuffix(region.Name), provinceShort, StringComparison.Ordinal));
        if (matchedProvince is null) return null;
        if (string.IsNullOrWhiteSpace(city)) return matchedProvince;

        string cityName = city.Trim();
        string cityShort = WithoutSuffix(cityName);
        return Cities(matchedProvince.Code).FirstOrDefault(region =>
                   region.Name.StartsWith(cityName, StringComparison.Ordinal) ||
                   string.Equals(WithoutSuffix(region.Name), cityName, StringComparison.Ordinal) ||
                   string.Equals(WithoutSuffix(region.Name), cityShort, StringComparison.Ordinal))
               ?? matchedProvince;
    }

    private static CatalogData Load()
    {
        try
        {
            using Stream? stream = typeof(ChinaRegionCatalog).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return CatalogData.Empty;
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return CatalogData.Empty;

            var regions = new List<ChinaRegion>();
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 6)
                    return CatalogData.Empty;
                JsonElement.ArrayEnumerator values = item.EnumerateArray();
                values.MoveNext();
                int code = values.Current.GetInt32();
                values.MoveNext();
                string name = values.Current.GetString() ?? throw new JsonException("Region name is missing.");
                values.MoveNext();
                int parentCode = values.Current.GetInt32();
                values.MoveNext();
                int level = values.Current.GetInt32();
                values.MoveNext();
                double longitude = values.Current.GetDouble();
                values.MoveNext();
                double latitude = values.Current.GetDouble();
                if (level is < 1 or > 3 || !double.IsFinite(latitude) || !double.IsFinite(longitude))
                    return CatalogData.Empty;
                regions.Add(new ChinaRegion(code, name, parentCode, level, latitude, longitude));
            }

            ChinaRegion[] ordered = regions.OrderBy(region => region.Code).ToArray();
            var byCode = ordered.ToDictionary(region => region.Code);
            var children = ordered.GroupBy(region => region.ParentCode)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<ChinaRegion>)Array.AsReadOnly(group.ToArray()));
            ChinaRegion[] provinces = ordered.Where(region => region.Level == 1 && region.ParentCode == 100000).ToArray();
            return new CatalogData(Array.AsReadOnly(ordered), byCode, children, Array.AsReadOnly(provinces));
        }
        catch
        {
            return CatalogData.Empty;
        }
    }

    private static IReadOnlyList<ChinaRegion> ChildrenOf(CatalogData data, int parentCode, int level)
    {
        if (!data.Children.TryGetValue(parentCode, out IReadOnlyList<ChinaRegion>? children)) return Empty;
        ChinaRegion[] matches = children.Where(region => region.Level == level).OrderBy(region => region.Code).ToArray();
        return matches.Length == 0 ? Empty : Array.AsReadOnly(matches);
    }

    private static int SearchScore(ChinaRegion region, string query, string shortQuery)
    {
        string shortName = WithoutSuffix(region.Name);
        if (string.Equals(region.Name, query, StringComparison.Ordinal)) return 100;
        if (string.Equals(shortName, shortQuery, StringComparison.Ordinal)) return 90;
        if (region.Name.StartsWith(query, StringComparison.Ordinal)) return 80;
        if (region.Name.Contains(query, StringComparison.Ordinal)) return 60;
        return PathOf(region.Code).Contains(query, StringComparison.Ordinal) ? 40 : 0;
    }

    private static string WithoutSuffix(string value)
    {
        foreach (string suffix in Suffixes)
            if (value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length)
                return value[..^suffix.Length];
        return value;
    }

    private static double DistanceKm(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        double latitudeDelta = DegreesToRadians(latitude2 - latitude1);
        double longitudeDelta = DegreesToRadians(longitude2 - longitude1);
        double firstLatitude = DegreesToRadians(latitude1);
        double secondLatitude = DegreesToRadians(latitude2);
        double haversine = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
            Math.Cos(firstLatitude) * Math.Cos(secondLatitude) * Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return EarthRadiusKm * 2 * Math.Asin(Math.Min(1, Math.Sqrt(haversine)));
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180;

    private sealed record CatalogData(
        IReadOnlyList<ChinaRegion> Regions,
        IReadOnlyDictionary<int, ChinaRegion> ByCode,
        IReadOnlyDictionary<int, IReadOnlyList<ChinaRegion>> Children,
        IReadOnlyList<ChinaRegion> Provinces)
    {
        public static CatalogData Empty { get; } = new(
            ChinaRegionCatalog.Empty,
            new Dictionary<int, ChinaRegion>(),
            new Dictionary<int, IReadOnlyList<ChinaRegion>>(),
            ChinaRegionCatalog.Empty);
    }
}
