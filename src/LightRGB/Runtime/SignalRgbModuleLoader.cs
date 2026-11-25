using System.Text.Json;
using LightRGB.Hardware;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;

namespace LightRGB.Runtime;

public sealed class SignalRgbModuleLoader : DocumentLoader
{
    private readonly string _pluginsDir;
    private readonly string _userPluginsDir;
    private readonly SystemInfoProvider _systemInfo;
    private readonly Action<string>? _log;

    public SignalRgbModuleLoader(string pluginsDir, string userPluginsDir, SystemInfoProvider systemInfo, Action<string>? log = null)
    {
        _pluginsDir = Path.GetFullPath(pluginsDir);
        _userPluginsDir = Path.GetFullPath(userPluginsDir);
        _systemInfo = systemInfo;
        _log = log;
    }

    public override Task<Document> LoadDocumentAsync(
        DocumentSettings settings,
        DocumentInfo? sourceInfo,
        string specifier,
        DocumentCategory category,
        DocumentContextCallback contextCallback)
    {
        if (specifier.StartsWith("@SignalRGB/", StringComparison.OrdinalIgnoreCase))
        {
            string virtualCode = BuildVirtualModule(specifier);
            var info = new DocumentInfo(specifier) { Category = ModuleCategory.Standard };
            return Task.FromResult<Document>(new StringDocument(info, virtualCode));
        }

        string? resolvedPath = ResolvePluginPath(sourceInfo, specifier);
        if (resolvedPath is null || !File.Exists(resolvedPath))
            throw new FileNotFoundException($"Plugin module '{specifier}' not found (resolved: '{resolvedPath ?? "<null>"}').");

        string source = File.ReadAllText(resolvedPath);
        var docInfo = new DocumentInfo(new Uri(resolvedPath))
        {
            Category = ModuleCategory.Standard
        };
        return Task.FromResult<Document>(new StringDocument(docInfo, source));
    }

    private string? ResolvePluginPath(DocumentInfo? sourceInfo, string specifier)
    {
        if (specifier.StartsWith("plugin://", StringComparison.OrdinalIgnoreCase))
        {
            string rel = specifier["plugin://".Length..].Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string userCandidate = Path.GetFullPath(Path.Combine(_userPluginsDir, rel));
            if (File.Exists(userCandidate))
                return userCandidate;
            return Path.GetFullPath(Path.Combine(_pluginsDir, rel));
        }

        if (Path.IsPathRooted(specifier) && File.Exists(specifier))
            return Path.GetFullPath(specifier);

        if (sourceInfo?.Uri is { IsFile: true } baseUri)
        {
            string? baseDir = Path.GetDirectoryName(baseUri.LocalPath);
            if (!string.IsNullOrEmpty(baseDir))
            {
                string candidate = Path.GetFullPath(Path.Combine(baseDir, specifier.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(candidate))
                    return candidate;
                if (!candidate.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate + ".js"))
                    return candidate + ".js";
            }
        }

        string fallback = Path.GetFullPath(Path.Combine(_pluginsDir, specifier.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(fallback))
            return fallback;
        if (!fallback.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && File.Exists(fallback + ".js"))
            return fallback + ".js";

        return null;
    }

    private string BuildVirtualModule(string specifier)
    {
        string name = specifier["@SignalRGB/".Length..];
        if (name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            name = name[..^3];

        switch (name.ToLowerInvariant())
        {
            case "errors":
                return """
                    export class Assert {
                        static isOk(cond, msg, ctx) {
                            if (!cond) throw new Error(msg || "Assertion failed");
                        }
                        static isEqual(a, b, msg) {
                            if (a !== b) throw new Error(msg || `Expected ${a} === ${b}`);
                        }
                        static softIsDefined(val, msg) {
                            return val !== undefined && val !== null;
                        }
                        static unreachable(msg) {
                            throw new Error(msg || "Unreachable code reached");
                        }
                        static fail(msg) {
                            throw new Error(msg || "Assertion failed");
                        }
                    }
                    export default { Assert };
                    """;

            case "systeminfo":
            {
                var mobo = _systemInfo.GetMotherboardInfo();
                var bios = _systemInfo.GetBiosInfo();
                string moboJson = JsonSerializer.Serialize(new { manufacturer = mobo.Manufacturer, model = mobo.Model });
                string biosJson = JsonSerializer.Serialize(new { vendor = bios.Vendor, version = bios.Version });
                return $$"""
                    const _mobo = {{moboJson}};
                    const _bios = {{biosJson}};
                    export default {
                        GetMotherboardInfo() { return _mobo; },
                        GetBiosInfo() { return _bios; },
                        GetRamInfo() { return []; }
                    };
                    """;
            }

            case "devicediscovery":
                return """
                    export default {
                        foundVirtualDevice(info) {
                            try {
                                if (typeof globalThis.__onVirtualDeviceDiscovered === "function") {
                                    globalThis.__onVirtualDeviceDiscovered(JSON.stringify(info || {}));
                                }
                            } catch {}
                        }
                    };
                    """;

            case "permissions":
                return """
                    let _cb = null;
                    export default {
                        permissions() { return {}; },
                        setCallback(fn) { _cb = fn; }
                    };
                    """;

            case "serial":
                return """
                    export default {
                        availablePorts() { return []; },
                        isConnected() { return false; },
                        connect() { return false; },
                        disconnect() {},
                        write() { return 0; },
                        read() { return []; }
                    };
                    """;

            case "lcd":
                return """
                    export default class LCD {
                        constructor() {}
                        static isSupported() { return false; }
                        sendImage() {}
                        clear() {}
                        setBrightness() {}
                    }
                    """;

            default:
                _log?.Invoke($"Unknown virtual import requested: {specifier}");
                return "export default {};";
        }
    }
}
