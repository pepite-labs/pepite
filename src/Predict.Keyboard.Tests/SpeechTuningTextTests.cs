using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Réglages (owner, 2026-11-14): the chosen speed and volume shown as text next to the sliders and given to
/// UI Automation.</summary>
public class SpeechTuningTextTests
{
    [Theory]
    [InlineData(0, "Normale (0)")]
    [InlineData(-1, "Un peu lente (−1)")]
    [InlineData(-3, "Un peu lente (−3)")]
    [InlineData(-4, "Lente (−4)")]
    [InlineData(-7, "Lente (−7)")]
    [InlineData(-8, "Très lente (−8)")]
    [InlineData(-10, "Très lente (−10)")]
    [InlineData(2, "Un peu rapide (+2)")]
    [InlineData(5, "Rapide (+5)")]
    [InlineData(10, "Très rapide (+10)")]
    [InlineData(25, "Très rapide (+10)")]
    public void Rate_Bands(int rate, string expected) => Assert.Equal(expected, Strings.RateValue(rate));

    [Theory]
    [InlineData(80, "80 %")]
    [InlineData(0, "0 %")]
    [InlineData(130, "100 %")]
    public void Volume_Is_A_Percentage(int volume, string expected) => Assert.Equal(expected, Strings.VolumeValue(volume));

    [Theory]
    [InlineData("piper:pierre", (int)PiperState.Loading, "Chargement de la voix Pierre…")]
    [InlineData("piper:pierre", (int)PiperState.Ready, "✓ Voix Pierre prête")]
    [InlineData("piper:siwis", (int)PiperState.Failed, "Voix Siwis indisponible — la voix Windows est utilisée")]
    [InlineData("piper:siwis", (int)PiperState.NotLoaded, null)]
    [InlineData("sapi:Microsoft Hortense Desktop", (int)PiperState.Loading, null)]
    [InlineData(null, (int)PiperState.Ready, null)]
    public void Voice_Status_Line(string? voice, int state, string? expected) =>
        Assert.Equal(expected, Strings.VoiceStatus(voice, (PiperState)state));

    [Fact]
    public void Tray_Tooltip_Says_A_Load_Briefly()
    {
        Assert.Equal("Pépite — chargement de la voix Pierre…", Strings.TrayText(false, "piper:pierre"));
        Assert.Equal(Strings.TrayActive, Strings.TrayText(false, null));
        Assert.Equal(Strings.TrayPaused, Strings.TrayText(true, "piper:pierre"));   // paused wins
        Assert.True(Strings.TrayText(false, "piper:" + new string('x', 200)).Length <= 127);   // NotifyIcon limit
    }

    [Fact]
    public void Slider_Name_Carries_The_Value() =>
        Assert.Equal("Vitesse : Normale (0)", Strings.SliderName(Strings.RateLabel, Strings.RateValue(0)));
}
