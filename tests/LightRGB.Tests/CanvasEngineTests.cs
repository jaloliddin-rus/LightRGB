using LightRGB.Effects;
using Xunit;

namespace LightRGB.Tests;

public sealed class CanvasEngineTests
{
    [Fact]
    public void AllEffects_RenderWithoutErrorAndRespectBrightness()
    {
        var canvas = new CanvasEngine();

        foreach (var eff in CanvasEngine.AvailableEffects)
        {
            canvas.UpdateSettings(new EffectSettings
            {
                EffectName = eff,
                Brightness = 100,
                Speed = 1.5f,
                Direction = "right",
                PrimaryColor = "#FF0040",
                SecondaryColor = "#00E5FF"
            });
            canvas.Tick(1.23);

            var frame = canvas.GetDownsampledFrame(16, 10);
            Assert.Equal(16 * 10 * 3, frame.Length);

            if (eff == "Off")
            {
                Assert.All(frame, b => Assert.Equal(0, b));
            }
            else
            {
                Assert.Contains(frame, b => b > 0);
            }
        }

        // Brightness 0% yields black even on Solid Color
        canvas.UpdateSettings(new EffectSettings
        {
            EffectName = "Solid Color",
            Brightness = 0,
            PrimaryColor = "#FFFFFF"
        });
        canvas.Tick(0);
        Assert.Equal((0, 0, 0), canvas.Sample(0.5f, 0.5f));
    }

    [Fact]
    public void RainbowWave_AnimatesOverTime()
    {
        var canvas = new CanvasEngine();
        canvas.UpdateSettings(new EffectSettings { EffectName = "Rainbow Wave", Speed = 1.0f, Brightness = 100 });
        canvas.Tick(0.0);
        var c0 = canvas.Sample(0.25f, 0.5f);

        canvas.Tick(1.0);
        var c1 = canvas.Sample(0.25f, 0.5f);

        Assert.NotEqual(c0, c1);
    }
}
