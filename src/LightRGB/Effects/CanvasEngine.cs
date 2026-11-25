using System.Globalization;

namespace LightRGB.Effects;

public sealed class EffectSettings
{
    public string EffectName { get; set; } = "Rainbow Wave";
    public float Speed { get; set; } = 1.0f;
    public int Brightness { get; set; } = 100;
    public string Direction { get; set; } = "right";
    public string PrimaryColor { get; set; } = "#009BDE";
    public string SecondaryColor { get; set; } = "#FF2A85";
}

public sealed class CanvasEngine
{
    public const int Width = 64;
    public const int Height = 40;

    public static readonly string[] AvailableEffects =
    [
        "Rainbow Wave",
        "Neon Shift",
        "Breathing",
        "Gradient Wave",
        "Side To Side",
        "Color Cycle",
        "Starlight",
        "Solid Color",
        "Off"
    ];

    private readonly object _lock = new();
    private byte[] _frontBuffer = new byte[Width * Height * 3];
    private byte[] _backBuffer = new byte[Width * Height * 3];
    private byte[] _frontRawBuffer = new byte[Width * Height * 3];
    private byte[] _backRawBuffer = new byte[Width * Height * 3];
    private EffectSettings _settings = new();

    public EffectSettings GetSettingsSnapshot()
    {
        lock (_lock)
        {
            return new EffectSettings
            {
                EffectName = _settings.EffectName,
                Speed = _settings.Speed,
                Brightness = _settings.Brightness,
                Direction = _settings.Direction,
                PrimaryColor = _settings.PrimaryColor,
                SecondaryColor = _settings.SecondaryColor
            };
        }
    }

    public void UpdateSettings(EffectSettings settings)
    {
        lock (_lock)
        {
            _settings = new EffectSettings
            {
                EffectName = string.IsNullOrWhiteSpace(settings.EffectName) ? "Rainbow Wave" : settings.EffectName,
                Speed = Math.Clamp(settings.Speed, 0.1f, 5.0f),
                Brightness = Math.Clamp(settings.Brightness, 0, 100),
                Direction = string.IsNullOrWhiteSpace(settings.Direction) ? "right" : settings.Direction.ToLowerInvariant(),
                PrimaryColor = NormalizeHex(settings.PrimaryColor, "#009BDE"),
                SecondaryColor = NormalizeHex(settings.SecondaryColor, "#FF2A85")
            };
        }
    }

    public void Tick(double elapsedSeconds)
    {
        EffectSettings cfg;
        lock (_lock)
        {
            cfg = _settings;
        }

        float bright = Math.Clamp(cfg.Brightness / 100.0f, 0f, 1f);
        float t = (float)(elapsedSeconds * cfg.Speed);
        var (r1, g1, b1) = ParseHexColor(cfg.PrimaryColor);
        var (r2, g2, b2) = ParseHexColor(cfg.SecondaryColor);

        byte[] buf = _backBuffer;
        byte[] rawBuf = _backRawBuffer;
        string effect = cfg.EffectName;
        string dir = cfg.Direction;

        for (int y = 0; y < Height; y++)
        {
            float ny = Height > 1 ? y / (float)(Height - 1) : 0.5f;
            for (int x = 0; x < Width; x++)
            {
                float nx = Width > 1 ? x / (float)(Width - 1) : 0.5f;
                float coord = dir switch
                {
                    "left" => 1.0f - nx,
                    "up" => 1.0f - ny,
                    "down" => ny,
                    "radial" => MathF.Sqrt((nx - 0.5f) * (nx - 0.5f) + (ny - 0.5f) * (ny - 0.5f)) * 1.414f,
                    _ => nx
                };

                byte r, g, b;
                switch (effect)
                {
                    case "Off":
                        r = g = b = 0;
                        break;

                    case "Solid Color":
                        r = r1; g = g1; b = b1;
                        break;

                    case "Color Cycle":
                    {
                        float hue = (t * 0.12f) % 1.0f;
                        if (hue < 0) hue += 1.0f;
                        (r, g, b) = HsvToRgb(hue, 1.0f, 1.0f);
                        break;
                    }

                    case "Breathing":
                    {
                        float phase = 0.5f - 0.5f * MathF.Cos(t * 2.2f);
                        float mix = (MathF.Floor(t * 2.2f / (2 * MathF.PI)) % 2 == 0) ? 0f : 1f;
                        byte br = (byte)(r1 + (r2 - r1) * mix);
                        byte bg = (byte)(g1 + (g2 - g1) * mix);
                        byte bb = (byte)(b1 + (b2 - b1) * mix);
                        r = (byte)(br * phase);
                        g = (byte)(bg * phase);
                        b = (byte)(bb * phase);
                        break;
                    }

                    case "Neon Shift":
                    {
                        float u = coord * 0.85f - t * 0.25f;
                        float wave = 0.5f + 0.5f * MathF.Sin(u * MathF.PI * 2.0f);
                        r = (byte)(r1 + (r2 - r1) * wave);
                        g = (byte)(g1 + (g2 - g1) * wave);
                        b = (byte)(b1 + (b2 - b1) * wave);
                        break;
                    }

                    case "Gradient Wave":
                    {
                        float u = (coord - t * 0.35f) % 1.0f;
                        if (u < 0) u += 1.0f;
                        float wave = u < 0.5f ? u * 2.0f : (1.0f - u) * 2.0f;
                        r = (byte)(r1 + (r2 - r1) * wave);
                        g = (byte)(g1 + (g2 - g1) * wave);
                        b = (byte)(b1 + (b2 - b1) * wave);
                        break;
                    }

                    case "Side To Side":
                    {
                        float center = 0.5f + 0.45f * MathF.Sin(t * 2.0f);
                        float dist = MathF.Abs(nx - center);
                        float glow = Math.Clamp(1.0f - dist * 4.0f, 0f, 1f);
                        glow *= glow;
                        r = (byte)(r2 * 0.15f + r1 * glow);
                        g = (byte)(g2 * 0.15f + g1 * glow);
                        b = (byte)(b2 * 0.15f + b1 * glow);
                        break;
                    }

                    case "Starlight":
                    {
                        uint hash = (uint)(x * 73856093 ^ y * 19349663);
                        float phase = (hash % 1000) / 1000.0f * MathF.PI * 2.0f;
                        float pulse = MathF.Pow(0.5f + 0.5f * MathF.Sin(t * 2.5f + phase), 4.0f);
                        float pick = ((hash >> 8) & 1) == 0 ? 0f : 1f;
                        byte sr = (byte)(r1 + (r2 - r1) * pick);
                        byte sg = (byte)(g1 + (g2 - g1) * pick);
                        byte sb = (byte)(b1 + (b2 - b1) * pick);
                        r = (byte)(sr * pulse);
                        g = (byte)(sg * pulse);
                        b = (byte)(sb * pulse);
                        break;
                    }

                    default: // Rainbow Wave
                    {
                        float hue = (coord - t * 0.25f) % 1.0f;
                        if (hue < 0) hue += 1.0f;
                        (r, g, b) = HsvToRgb(hue, 1.0f, 1.0f);
                        break;
                    }
                }

                int idx = (y * Width + x) * 3;
                rawBuf[idx] = r;
                rawBuf[idx + 1] = g;
                rawBuf[idx + 2] = b;
                buf[idx] = (byte)(r * bright);
                buf[idx + 1] = (byte)(g * bright);
                buf[idx + 2] = (byte)(b * bright);
            }
        }

        lock (_lock)
        {
            (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);
            (_frontRawBuffer, _backRawBuffer) = (_backRawBuffer, _frontRawBuffer);
        }
    }

    public (byte R, byte G, byte B) Sample(float normX, float normY)
    {
        int x = Math.Clamp((int)MathF.Round(normX * (Width - 1)), 0, Width - 1);
        int y = Math.Clamp((int)MathF.Round(normY * (Height - 1)), 0, Height - 1);
        int idx = (y * Width + x) * 3;
        lock (_lock)
        {
            return (_frontBuffer[idx], _frontBuffer[idx + 1], _frontBuffer[idx + 2]);
        }
    }

    /// <summary>
    /// Samples the full-brightness unscaled canvas RGB color so downstream gamma-corrected
    /// devices can preserve hue ratios across brightness levels.
    /// </summary>
    public (byte R, byte G, byte B) SampleUnscaled(float normX, float normY)
    {
        int x = Math.Clamp((int)MathF.Round(normX * (Width - 1)), 0, Width - 1);
        int y = Math.Clamp((int)MathF.Round(normY * (Height - 1)), 0, Height - 1);
        int idx = (y * Width + x) * 3;
        lock (_lock)
        {
            return (_frontRawBuffer[idx], _frontRawBuffer[idx + 1], _frontRawBuffer[idx + 2]);
        }
    }

    public byte[] GetDownsampledFrame(int outW = 16, int outH = 10)
    {
        var result = new byte[outW * outH * 3];
        lock (_lock)
        {
            for (int y = 0; y < outH; y++)
            {
                int sy = y * (Height - 1) / Math.Max(1, outH - 1);
                for (int x = 0; x < outW; x++)
                {
                    int sx = x * (Width - 1) / Math.Max(1, outW - 1);
                    int srcIdx = (sy * Width + sx) * 3;
                    int dstIdx = (y * outW + x) * 3;
                    result[dstIdx] = _frontBuffer[srcIdx];
                    result[dstIdx + 1] = _frontBuffer[srcIdx + 1];
                    result[dstIdx + 2] = _frontBuffer[srcIdx + 2];
                }
            }
        }
        return result;
    }

    public static (byte R, byte G, byte B) ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return (0, 155, 222);
        string clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 &&
            byte.TryParse(clean[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(clean[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(clean[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return (r, g, b);
        }
        return (0, 155, 222);
    }

    public static (byte R, byte G, byte B) ApplyBrightness((byte R, byte G, byte B) c, int brightnessPercent)
    {
        float f = Math.Clamp(brightnessPercent / 100.0f, 0f, 1f);
        return ((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
    }

    private static string NormalizeHex(string? hex, string fallback)
    {
        var (r, g, b) = ParseHexColor(string.IsNullOrWhiteSpace(hex) ? fallback : hex);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    public static (byte R, byte G, byte B) HsvToRgb(float h, float s, float v)
    {
        float hh = (h % 1.0f) * 6.0f;
        if (hh < 0) hh += 6.0f;
        int sector = (int)MathF.Floor(hh);
        float f = hh - sector;
        float p = v * (1.0f - s);
        float q = v * (1.0f - s * f);
        float t = v * (1.0f - s * (1.0f - f));

        (float r, float g, float b) = sector switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
