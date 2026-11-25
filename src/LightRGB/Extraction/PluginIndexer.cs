using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LightRGB.Hardware;
using LightRGB.Runtime;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace LightRGB.Extraction;

public sealed class PluginIndexEntry
{
    public string RelativePath { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsUserPlugin { get; set; }
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Type { get; set; } = "hid";
    public string DeviceType { get; set; } = "";
    public int[] VendorIds { get; set; } = [];
    public int[] ProductIds { get; set; } = [];
    public int[] Size { get; set; } = [1, 1];
    public bool SubdeviceController { get; set; }
}

public sealed class PluginIndexDocument
{
    public string Fingerprint { get; set; } = "";
    public int TotalFilesScanned { get; set; }
    public int V8IndexedCount { get; set; }
    public int RegexFallbackCount { get; set; }
    public List<PluginIndexEntry> Entries { get; set; } = [];
}

public sealed class PluginIndexer
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public PluginIndexDocument LoadOrBuild(string dataDirectory, SystemInfoProvider systemInfo, bool forceRebuild = false, Action<string>? log = null)
    {
        string pluginsDir = Path.Combine(dataDirectory, "plugins");
        string userPluginsDir = Path.Combine(dataDirectory, "user_plugins");
        string indexPath = Path.Combine(dataDirectory, "plugin-index.json");

        Directory.CreateDirectory(pluginsDir);
        Directory.CreateDirectory(userPluginsDir);

        var files = CollectPluginFiles(pluginsDir, userPluginsDir);
        string fingerprint = ComputeFingerprint(files);

        if (!forceRebuild && File.Exists(indexPath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<PluginIndexDocument>(File.ReadAllText(indexPath));
                if (cached is not null && cached.Fingerprint == fingerprint)
                {
                    return cached;
                }
            }
            catch
            {
                // Rebuild if cache corrupt
            }
        }

        var doc = BuildIndex(files, pluginsDir, userPluginsDir, systemInfo, fingerprint, log);
        try
        {
            File.WriteAllText(indexPath, JsonSerializer.Serialize(doc, JsonOpts));
        }
        catch (Exception ex)
        {
            log?.Invoke($"Warning: failed to save plugin index: {ex.Message}");
        }
        return doc;
    }

    public static List<PluginIndexEntry> FindMatches(PluginIndexDocument index, ushort vendorId, ushort productId)
    {
        var matches = new List<PluginIndexEntry>();
        foreach (var e in index.Entries)
        {
            if (e.Type.Equals("smbus", StringComparison.OrdinalIgnoreCase) ||
                e.Type.Equals("serial", StringComparison.OrdinalIgnoreCase))
                continue;

            if (e.VendorIds.Contains(vendorId) && e.ProductIds.Contains(productId))
                matches.Add(e);
        }
        return matches;
    }

    private static List<(string FullPath, string RelativePath, bool IsUser)> CollectPluginFiles(string pluginsDir, string userPluginsDir)
    {
        var seen = new Dictionary<string, (string FullPath, string RelativePath, bool IsUser)>(StringComparer.OrdinalIgnoreCase);

        void Scan(string root, bool isUser)
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".test.bak.js", StringComparison.OrdinalIgnoreCase))
                    continue;

                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                seen[rel] = (file, rel, isUser);
            }
        }

        Scan(pluginsDir, isUser: false);
        Scan(userPluginsDir, isUser: true);
        return seen.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ComputeFingerprint(List<(string FullPath, string RelativePath, bool IsUser)> files)
    {
        long maxTicks = 0;
        long totalLen = 0;
        foreach (var (full, _, _) in files)
        {
            var fi = new FileInfo(full);
            totalLen += fi.Length;
            if (fi.LastWriteTimeUtc.Ticks > maxTicks)
                maxTicks = fi.LastWriteTimeUtc.Ticks;
        }
        return $"{files.Count}:{totalLen}:{maxTicks}";
    }

    private static PluginIndexDocument BuildIndex(
        List<(string FullPath, string RelativePath, bool IsUser)> files,
        string pluginsDir,
        string userPluginsDir,
        SystemInfoProvider systemInfo,
        string fingerprint,
        Action<string>? log)
    {
        var doc = new PluginIndexDocument
        {
            Fingerprint = fingerprint,
            TotalFilesScanned = files.Count
        };

        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableDynamicModuleImports);
        engine.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableFileLoading;
        engine.DocumentSettings.Loader = new SignalRgbModuleLoader(pluginsDir, userPluginsDir, systemInfo, log);

        // Minimal dummy globals in case a plugin references device/console at top level during load
        engine.Execute("""
            globalThis.console = { log() {}, warn() {}, error() {}, info() {} };
            globalThis.device = new Proxy({}, {
                get(_, prop) {
                    if (prop === "getHidEndpoints") return () => [];
                    if (prop === "getChannelNames") return () => [];
                    if (prop === "getCurrentSubdevices") return () => [];
                    return () => {};
                }
            });
            globalThis.bus = new Proxy({}, { get() { return () => 0; } });
            globalThis.service = new Proxy({}, { get() { return () => {}; } });
            globalThis.__extractMeta = function(mod) {
                const call = (fn, def) => {
                    if (typeof mod[fn] !== "function") return def;
                    try { const r = mod[fn](); return r === undefined ? def : r; } catch { return def; }
                };
                const toIntArr = (v) => {
                    if (Array.isArray(v)) return v.map(x => Number(x) | 0).filter(x => x > 0);
                    if (typeof v === "number" && Number.isFinite(v) && v > 0) return [v | 0];
                    return [];
                };
                const hasRender = typeof mod.Render === "function";
                const hasInit = typeof mod.Initialize === "function";
                const hasScan = typeof mod.Scan === "function";
                if (!hasRender && !hasInit && !hasScan) return null;
                return JSON.stringify({
                    name: String(call("Name", "")),
                    publisher: String(call("Publisher", "")),
                    type: String(call("Type", "hid")).toLowerCase(),
                    deviceType: String(call("DeviceType", "")),
                    vendorIds: toIntArr(call("VendorId", [])),
                    productIds: toIntArr(call("ProductId", [])),
                    size: Array.isArray(call("Size", [1, 1])) ? call("Size", [1, 1]).slice(0, 2).map(x => Number(x) || 1) : [1, 1],
                    subdeviceController: Boolean(call("SubdeviceController", false))
                });
            };
            """);

        foreach (var (fullPath, relPath, isUser) in files)
        {
            string source = File.ReadAllText(fullPath);
            if (!LooksLikeRunnablePlugin(source))
                continue;

            PluginIndexEntry? entry = null;
            try
            {
                string escaped = relPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string script = $$"""
                    import * as __mod from "plugin://{{escaped}}";
                    globalThis.__lastMeta = globalThis.__extractMeta(__mod);
                    """;
                engine.Script.__lastMeta = null;
                engine.Execute(new DocumentInfo($"indexer://{relPath}") { Category = ModuleCategory.Standard }, script);

                if (engine.Script.__lastMeta is string json && !string.IsNullOrEmpty(json))
                {
                    using var jdoc = JsonDocument.Parse(json);
                    var root = jdoc.RootElement;
                    entry = new PluginIndexEntry
                    {
                        RelativePath = relPath,
                        FullPath = fullPath,
                        IsUserPlugin = isUser,
                        Name = root.GetProperty("name").GetString() ?? Path.GetFileNameWithoutExtension(relPath),
                        Publisher = root.GetProperty("publisher").GetString() ?? "",
                        Type = root.GetProperty("type").GetString() ?? "hid",
                        DeviceType = root.GetProperty("deviceType").GetString() ?? "",
                        VendorIds = root.GetProperty("vendorIds").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                        ProductIds = root.GetProperty("productIds").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                        Size = root.GetProperty("size").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                        SubdeviceController = root.GetProperty("subdeviceController").GetBoolean()
                    };
                    doc.V8IndexedCount++;
                }
            }
            catch
            {
                entry = TryRegexFallback(fullPath, relPath, isUser, source);
                if (entry is not null)
                    doc.RegexFallbackCount++;
            }

            if (entry is not null)
                doc.Entries.Add(entry);
        }

        log?.Invoke($"Indexed {doc.Entries.Count} runnable plugins (V8={doc.V8IndexedCount}, fallback={doc.RegexFallbackCount}).");
        return doc;
    }

    private static bool LooksLikeRunnablePlugin(string source)
    {
        bool hasRenderOrScan = source.Contains("export function Render", StringComparison.Ordinal) ||
                               source.Contains("export function Scan", StringComparison.Ordinal);
        return hasRenderOrScan;
    }

    private static PluginIndexEntry? TryRegexFallback(string fullPath, string relPath, bool isUser, string source)
    {
        string name = MatchStringFn(source, "Name") ?? Path.GetFileNameWithoutExtension(relPath);
        string type = (MatchStringFn(source, "Type") ?? "hid").ToLowerInvariant();
        int[] vids = MatchIntListFn(source, "VendorId");
        int[] pids = MatchIntListFn(source, "ProductId");

        return new PluginIndexEntry
        {
            RelativePath = relPath,
            FullPath = fullPath,
            IsUserPlugin = isUser,
            Name = name,
            Publisher = MatchStringFn(source, "Publisher") ?? "",
            Type = type,
            DeviceType = MatchStringFn(source, "DeviceType") ?? "",
            VendorIds = vids,
            ProductIds = pids,
            Size = [1, 1]
        };
    }

    private static string? MatchStringFn(string src, string fn)
    {
        var m = Regex.Match(src, $@"export\s+function\s+{fn}\s*\([^)]*\)\s*\{{[^}}]*?return\s+[""']([^""']+)[""']", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int[] MatchIntListFn(string src, string fn)
    {
        var m = Regex.Match(src, $@"export\s+function\s+{fn}\s*\([^)]*\)\s*\{{([^}}]*)\}}", RegexOptions.Singleline);
        if (!m.Success) return [];
        var nums = new List<int>();
        foreach (Match nm in Regex.Matches(m.Groups[1].Value, @"0x[0-9a-fA-F]+|\b\d{3,5}\b"))
        {
            string tok = nm.Value;
            if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(tok[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) && v > 0)
                    nums.Add(v);
            }
            else if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0)
            {
                nums.Add(v);
            }
        }
        return nums.Distinct().ToArray();
    }
}
