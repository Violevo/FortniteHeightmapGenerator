using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.FileProvider;
using CUE4Parse_Conversion.Landscape;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FortniteHeightmapGenerator;

public class FortniteCentralAesResponse
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("mainKey")]
    public string MainKey { get; set; } = string.Empty;

    [JsonPropertyName("dynamicKeys")]
    public List<FortniteCentralDynamicKey> DynamicKeys { get; set; } = new();
}

public class FortniteCentralDynamicKey
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("guid")]
    public string Guid { get; set; } = string.Empty;
}

public class FortniteCentralMappingResponse
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;
}

public static class Program
{
    private static readonly string DefaultFortnitePaksPath = @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Content\Paks";

    private static readonly HttpClient _httpClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "FortniteHeightmapGenerator/1.0" } }
    };

    private static readonly string[] PossibleTerrains = { "Hera_Terrain", "Helios_Terrain", "Asteria_Terrain", "Artemis_Terrain", "Apollo_Terrain", "Athena_Terrain" };

    public static async Task Main(string[] args)
    {
        Console.WriteLine("Starting Fortnite Heightmap Generator...");

        string fortnitePaksPath = args.Length > 0 ? args[0] : DefaultFortnitePaksPath;

        if (!Directory.Exists(fortnitePaksPath))
        {
            Console.WriteLine($"Error: The directory '{fortnitePaksPath}' does not exist or is empty.");
            Console.WriteLine("Please ensure Fortnite is installed at this location or pass the path as a command-line argument.");
            return;
        }

        // Fetch AES keys
        var aesResponse = await FetchAesKeysAsync();
        if (aesResponse is null || string.IsNullOrEmpty(aesResponse.MainKey))
        {
            Console.WriteLine("Error: Failed to fetch or parse AES keys, or main key is empty.");
            return;
        }

        // Initialize compression libraries
        await InitializeCompressionAsync();

        // Initialize provider with keys and mappings
        var provider = await InitializeProviderAsync(fortnitePaksPath, aesResponse);

        // Find landscape components
        var landscapeComponents = await FindLandscapeComponentsAsync(provider);
        if (landscapeComponents.Count == 0)
        {
            Console.WriteLine("Error: Found 0 landscape components across all grid chunks.");
            return;
        }

        // Generate and save the heightmap
        await GenerateHeightmapAsync(landscapeComponents);

        Console.WriteLine("Done!");
    }

    private static async Task<FortniteCentralAesResponse?> FetchAesKeysAsync()
    {
        Console.WriteLine("Fetching AES keys from FortniteCentral API...");
        try
        {
            var responseJson = await _httpClient.GetStringAsync("https://fortnitecentral.genxgames.gg/api/v1/aes");
            return JsonSerializer.Deserialize<FortniteCentralAesResponse>(responseJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching AES keys: {ex.Message}");
            return null;
        }
    }

    private static async Task InitializeCompressionAsync()
    {
        string? oodlePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "oo2core_9_win64.dll");
        if (!File.Exists(oodlePath))
        {
            Console.WriteLine("Downloading Oodle library...");
            await CUE4Parse.Compression.OodleHelper.DownloadOodleDllAsync(ref oodlePath);
        }
        await CUE4Parse.Compression.OodleHelper.InitializeAsync(oodlePath);

        string zlibPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zlib.dll");
        if (!File.Exists(zlibPath))
        {
            Console.WriteLine("Downloading Zlib library...");
            await CUE4Parse.Compression.ZlibHelper.DownloadDllAsync(zlibPath);
        }
        await CUE4Parse.Compression.ZlibHelper.InitializeAsync(zlibPath);
    }

    private static async Task<DefaultFileProvider> InitializeProviderAsync(string paksPath, FortniteCentralAesResponse aesResponse)
    {
        Console.WriteLine("Initializing CUE4Parse provider...");
        var provider = new DefaultFileProvider(paksPath, SearchOption.AllDirectories, true, new VersionContainer(EGame.GAME_UE5_8));
        provider.Initialize();

        Console.WriteLine($"Submitting Main Key: {aesResponse.MainKey}");
        await provider.SubmitKeyAsync(new FGuid(), new FAesKey(aesResponse.MainKey));

        if (aesResponse.DynamicKeys is not null)
        {
            foreach (var key in aesResponse.DynamicKeys)
            {
                await provider.SubmitKeyAsync(new FGuid(key.Guid), new FAesKey(key.Key));
            }
        }

        provider.LoadVirtualPaths();

        await LoadMappingsAsync(provider);

        return provider;
    }

    private static async Task LoadMappingsAsync(DefaultFileProvider provider)
    {
        Console.WriteLine("Fetching Mapping URL from FortniteCentral API...");
        try
        {
            var mappingsJson = await _httpClient.GetStringAsync("https://fortnitecentral.genxgames.gg/api/v1/mappings");
            using var doc = JsonDocument.Parse(mappingsJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("mappings", out var mappingsNode))
            {
                string mappingsUrl = string.Empty;

                if (mappingsNode.TryGetProperty("Brotli", out var brotliNode) && brotliNode.ValueKind == JsonValueKind.String)
                    mappingsUrl = brotliNode.GetString() ?? string.Empty;
                else if (mappingsNode.TryGetProperty("ZStandard", out var zsNode) && zsNode.ValueKind == JsonValueKind.String)
                    mappingsUrl = zsNode.GetString() ?? string.Empty;

                if (!string.IsNullOrEmpty(mappingsUrl))
                {
                    string usmapName = Path.GetFileName(new Uri(mappingsUrl).LocalPath);
                    string usmapPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, usmapName);

                    if (!File.Exists(usmapPath))
                    {
                        Console.WriteLine($"Downloading Mappings ({usmapName})...");
                        var usmapBytes = await _httpClient.GetByteArrayAsync(mappingsUrl);
                        await File.WriteAllBytesAsync(usmapPath, usmapBytes);
                    }

                    Console.WriteLine("Loading Mappings into Provider...");
                    provider.MappingsContainer = new CUE4Parse.MappingsProvider.FileUsmapTypeMappingsProvider(usmapPath);
                }
                else
                {
                    Console.WriteLine("Warning: Mappings URL could not be extracted.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to fetch/load mappings. Error: {ex.Message}");
        }
    }

    private static async Task<List<ULandscapeComponent>> FindLandscapeComponentsAsync(DefaultFileProvider provider)
    {
        Console.WriteLine("Searching for latest terrain maps...");

        string targetTerrain = string.Empty;
        var mapFiles = new List<GameFile>();

        foreach (var terrain in PossibleTerrains)
        {
            mapFiles = provider.Files.Values.Where(x =>
                x.Path.Contains($"{terrain}/_Generated_/", StringComparison.OrdinalIgnoreCase) &&
                x.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (mapFiles.Count > 0)
            {
                targetTerrain = terrain;
                break;
            }
        }

        if (string.IsNullOrEmpty(targetTerrain))
        {
            Console.WriteLine("Error: Could not find any known terrain map.");
            return new List<ULandscapeComponent>();
        }

        Console.WriteLine($"Loading main map file for {targetTerrain}...");

        var mainMapFile = provider.Files.Values.FirstOrDefault(x =>
            x.Path.Contains(targetTerrain, StringComparison.OrdinalIgnoreCase) &&
            x.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) &&
            !x.Path.Contains("_Generated_", StringComparison.OrdinalIgnoreCase));

        if (mainMapFile is null)
        {
            Console.WriteLine($"Error: Could not find main umap file for {targetTerrain}.");
            return new List<ULandscapeComponent>();
        }

        Console.WriteLine($"Loading World: {mainMapFile.Name}...");
        var mapPackage = await provider.LoadPackageAsync(mainMapFile.Path);
        var world = mapPackage.GetExports().OfType<UWorld>().FirstOrDefault();

        if (world is null)
        {
            Console.WriteLine("Error: Could not find UWorld export in the map package.");
            return new List<ULandscapeComponent>();
        }

        var persistentLevel = await world.PersistentLevel.LoadAsync<ULevel>();

        if (persistentLevel is null)
        {
            Console.WriteLine("Error: Failed to load PersistentLevel.");
            return new List<ULandscapeComponent>();
        }

        var landscapeComponents = new List<ULandscapeComponent>();

        Console.WriteLine("Searching for ALandscapeProxy actors...");
        foreach (var actorLazy in persistentLevel.Actors)
        {
            if (actorLazy.IsNull) continue;

            var actor = await actorLazy.LoadAsync();
            if (actor is null) continue;

            if (actor.ExportType == "Landscape" || actor.ExportType == "LandscapeProxy")
            {
                var proxyComponents = actor.GetOrDefault("LandscapeComponents", Array.Empty<UObject>());

                var loadedComponents = proxyComponents
                    .Select(c => c as ULandscapeComponent)
                    .Where(c => c is not null)
                    .ToList();
                landscapeComponents.AddRange(loadedComponents!);
            }
        }

        if (landscapeComponents.Count == 0)
        {
            Console.WriteLine("No landscape proxy found in base PersistentLevel. Brute-force scanning all Terrain paths...");
            var allTerrainFiles = provider.Files.Where(x => x.Key.Contains(targetTerrain, StringComparison.OrdinalIgnoreCase)).ToList();
            Console.WriteLine($"Found {allTerrainFiles.Count} total files in {targetTerrain}. Scanning for Landscape components...");

            int scanned = 0;
            foreach (var kvp in allTerrainFiles)
            {
                scanned++;
                if (scanned % 1000 == 0) Console.WriteLine($"Scanned {scanned}/{allTerrainFiles.Count} files...");

                try
                {
                    if (kvp.Key.EndsWith(".upnl") || kvp.Key.EndsWith(".uexp") || kvp.Key.EndsWith(".ubulk")) continue;

                    var pkg = await provider.LoadPackageAsync(kvp.Value);
                    foreach (var export in pkg.GetExports())
                    {
                        if (export.ExportType == "Landscape" || export.ExportType == "LandscapeProxy" || export.ExportType == "LandscapeStreamingProxy")
                        {
                            var proxyComponents = export.GetOrDefault("LandscapeComponents", Array.Empty<UObject>());
                            var loadedComponents = proxyComponents
                                .Select(c => c as ULandscapeComponent)
                                .Where(c => c is not null)
                                .ToList();
                            landscapeComponents.AddRange(loadedComponents!);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to process '{kvp.Key}': {ex.Message}");
                }
            }
        }

        Console.WriteLine($"Found {landscapeComponents.Count} landscape components. Extracting heightmap...");
        return landscapeComponents;
    }

    private static async Task GenerateHeightmapAsync(List<ULandscapeComponent> landscapeComponents)
    {
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;

        foreach (var component in landscapeComponents)
        {
            int tMinX = minX;
            int tMinY = minY;
            int tMaxX = maxX;
            int tMaxY = maxY;
            component.GetComponentExtent(ref tMinX, ref tMinY, ref tMaxX, ref tMaxY);
            if (tMinX < minX) minX = tMinX;
            if (tMinY < minY) minY = tMinY;
            if (tMaxX > maxX) maxX = tMaxX;
            if (tMaxY > maxY) maxY = tMaxY;
        }

        int width = maxX - minX + 1;
        int height = maxY - minY + 1;

        Console.WriteLine($"Global heightmap resolution: {width}x{height}");

        using var image = new Image<L16>(width, height);

        // Fill with default midpoint height value
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                row.Fill(new L16(32768));
            }
        });

        // Resolve reflection targets once
        var cue4parseConversionAssembly = typeof(CUE4Parse_Conversion.Meshes.MeshConverter).Assembly;
        var interfaceType = cue4parseConversionAssembly.GetType("CUE4Parse_Conversion.Landscape.FLandscapeComponentDataInterface");

        if (interfaceType is null)
        {
            Console.WriteLine("Error: Could not find FLandscapeComponentDataInterface via reflection.");
            return;
        }

        var getHeightMethod = interfaceType.GetMethod("GetHeight", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(int) }, null);
        var ctor = interfaceType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, new[] { typeof(ULandscapeComponent), typeof(int) }, null);

        if (getHeightMethod is null || ctor is null)
        {
            Console.WriteLine("Error: Could not resolve FLandscapeComponentDataInterface constructor or GetHeight method via reflection.");
            return;
        }

        foreach (var component in landscapeComponents)
        {
            try
            {
                var accessor = ctor.Invoke(new object[] { component, 0 });
                if (accessor is null) continue;

                int componentSizeVerts = (component.ComponentSizeQuads + 1);

                for (int y = 0; y < componentSizeVerts; y++)
                {
                    for (int x = 0; x < componentSizeVerts; x++)
                    {
                        int globalX = x + component.SectionBaseX - minX;
                        int globalY = y + component.SectionBaseY - minY;

                        if (globalX >= 0 && globalX < width && globalY >= 0 && globalY < height)
                        {
                            ushort h = (ushort)getHeightMethod.Invoke(accessor, new object[] { x, y })!;
                            image[globalX, globalY] = new L16(h);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to process landscape component: {ex.Message}");
            }
        }

        string outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "heightmap.png");
        Console.WriteLine($"Saving heightmap image to: {outputPath}");

        await image.SaveAsPngAsync(outputPath);
    }
}