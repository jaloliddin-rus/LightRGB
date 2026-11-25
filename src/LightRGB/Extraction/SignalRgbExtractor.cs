using System.IO.Compression;

namespace LightRGB.Extraction;

public sealed record ExtractionResult(int PluginsExtracted, int ComponentsExtracted, string PluginsDirectory, string ComponentsDirectory, string UserPluginsDirectory);

public sealed class SignalRgbExtractor
{
    public static string? FindInstallerPath(string? explicitPath = null) =>
        FindInstallerPath(
            explicitPath,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

    public static string? FindInstallerPath(
        string? explicitPath,
        string workingDirectory,
        string baseDirectory,
        string downloadsDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        string[] candidates =
        [
            Path.Combine(workingDirectory, "Install_SignalRgb.exe"),
            Path.Combine(workingDirectory, "..", "Install_SignalRgb.exe"),
            Path.Combine(baseDirectory, "Install_SignalRgb.exe"),
            Path.Combine(baseDirectory, "..", "Install_SignalRgb.exe"),
            Path.Combine(downloadsDirectory, "Install_SignalRgb.exe")
        ];

        foreach (var c in candidates)
        {
            try
            {
                string full = Path.GetFullPath(c);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // Ignore invalid paths
            }
        }

        return null;
    }

    public ExtractionResult Extract(string installerPath, string dataDirectory, Action<string>? log = null)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("SignalRGB installer not found.", installerPath);

        string pluginsDir = Path.Combine(dataDirectory, "plugins");
        string componentsDir = Path.Combine(dataDirectory, "components");
        string userPluginsDir = Path.Combine(dataDirectory, "user_plugins");

        Directory.CreateDirectory(pluginsDir);
        Directory.CreateDirectory(componentsDir);
        Directory.CreateDirectory(userPluginsDir);

        using var fs = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var (zipStart, zipEnd) = LocateEmbeddedZip(fs);
        log?.Invoke($"Located Squirrel ZIP in installer: offset={zipStart}, size={(zipEnd - zipStart) / 1024 / 1024.0:F1} MB");

        using var subStream = new SubReadOnlyStream(fs, zipStart, zipEnd - zipStart);
        using var outerArchive = new ZipArchive(subStream, ZipArchiveMode.Read, leaveOpen: true);

        var nupkgEntry = outerArchive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) &&
            e.Name.Contains("VortxEngine", StringComparison.OrdinalIgnoreCase))
            ?? outerArchive.Entries.FirstOrDefault(e => e.Name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("No .nupkg package found inside SignalRGB installer.");

        log?.Invoke($"Opening {nupkgEntry.Name} ({nupkgEntry.Length / 1024 / 1024.0:F1} MB)...");

        string tempNupkg = Path.Combine(Path.GetTempPath(), $"lightrgb_extract_{Guid.NewGuid():N}.nupkg");
        try
        {
            using (var entryStream = nupkgEntry.Open())
            using (var tempOut = new FileStream(tempNupkg, FileMode.Create, FileAccess.Write, FileShare.None, 131072))
            {
                entryStream.CopyTo(tempOut);
            }

            using var nupkgArchive = ZipFile.OpenRead(tempNupkg);
            const string pluginPrefix = "lib/net45/Signal-x64/Plugins/";
            const string componentPrefix = "lib/net45/Signal-x64/Components/";

            int pluginCount = 0;
            int componentCount = 0;

            foreach (var entry in nupkgArchive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                string fullName = entry.FullName.Replace('\\', '/');

                if (fullName.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = fullName[pluginPrefix.Length..];
                    string dest = GetSafeDestPath(pluginsDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                    if (rel.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                        pluginCount++;
                }
                else if (fullName.StartsWith(componentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = fullName[componentPrefix.Length..];
                    string dest = GetSafeDestPath(componentsDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                    if (rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        componentCount++;
                }
            }

            log?.Invoke($"Extracted {pluginCount} JS plugins and {componentCount} component definitions.");
            return new ExtractionResult(pluginCount, componentCount, pluginsDir, componentsDir, userPluginsDir);
        }
        finally
        {
            try { if (File.Exists(tempNupkg)) File.Delete(tempNupkg); } catch { }
        }
    }

    private static string GetSafeDestPath(string baseDir, string relativePath)
    {
        string baseFull = Path.GetFullPath(baseDir);
        string dest = Path.GetFullPath(Path.Combine(baseFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!dest.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe archive path: {relativePath}");
        return dest;
    }

    private static (long Start, long End) LocateEmbeddedZip(FileStream fs)
    {
        long len = fs.Length;
        int tailLen = (int)Math.Min(len, 2 * 1024 * 1024);
        long tailOffset = len - tailLen;
        byte[] buf = new byte[tailLen];

        fs.Position = tailOffset;
        int totalRead = 0;
        while (totalRead < tailLen)
        {
            int r = fs.Read(buf, totalRead, tailLen - totalRead);
            if (r == 0) break;
            totalRead += r;
        }

        // Scan backwards for EOCD signature PK\x05\x06 (0x06054b50)
        for (int i = totalRead - 22; i >= 0; i--)
        {
            if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x05 && buf[i + 3] == 0x06)
            {
                uint cdSize = BitConverter.ToUInt32(buf, i + 12);
                uint cdOffset = BitConverter.ToUInt32(buf, i + 16);
                ushort commentLen = BitConverter.ToUInt16(buf, i + 20);
                if (i + 22 + commentLen <= totalRead)
                {
                    long eocdPos = tailOffset + i;
                    long zipStart = eocdPos - cdSize - cdOffset;
                    long zipEnd = eocdPos + 22 + commentLen;
                    if (zipStart >= 0 && zipEnd <= len)
                        return (zipStart, zipEnd);
                }
            }
        }

        throw new InvalidDataException("Could not locate embedded Squirrel ZIP EOCD in installer.");
    }

    private sealed class SubReadOnlyStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _pos;

        public SubReadOnlyStream(Stream inner, long start, long length)
        {
            _inner = inner;
            _start = start;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _pos;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _pos;
            if (remaining <= 0) return 0;
            int toRead = (int)Math.Min(count, remaining);
            _inner.Position = _start + _pos;
            int read = _inner.Read(buffer, offset, toRead);
            _pos += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0 || target > _length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            _pos = target;
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
