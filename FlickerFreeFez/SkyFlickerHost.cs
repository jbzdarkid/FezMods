using System.Reflection;
using FezEngine.Components;
using FezEngine.Services;
using FezEngine.Tools;
using FezGame.Services;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using static FlickerFreeFez.ModComponent;

namespace FlickerFreeFez;

// Replaces the NU ZU flickering sky with a slower, in-and-out effect.
internal sealed class SkyFlickerHost : GameComponent
{
    private const float PeakOpacity = 1f;
    private const float FadeInSeconds = 0.25f;
    private const float FadeOutSeconds = 0.25f;

    private static readonly FieldInfo OriginalOpacityField = GetPrivateField(
        typeof(SkyHost).GetNestedType("BgLayerState", BindingFlags.NonPublic),
        "OriginalOpacity");

    private static readonly FieldInfo FlickerInField = GetPrivateField<SkyHost>("flickerIn");

    [ServiceDependency]
    public IGameLevelManager LevelManager { private get; set; }

    [ServiceDependency]
    public IGameStateManager GameState { private get; set; }

    [ServiceDependency]
    public ISoundManager SoundManager { private get; set; }

    private Hook _skyHostUpdateHook;
    private ILHook _lightingPreDrawHook;
    // Starts negative and counts up to 0 at the start of each flash in a sky flicker (1 or 2 flashes).
    // Then keeps counting to track the different parts of the flash, until it resets back to a negative (for the next one).
    private float _secondsIntoFlash;
    private float _holdSeconds;
    private int _flashesLeft;
    private float _brightness;

    public SkyFlickerHost(Game game) : base(game)
    {
    }

    public override void Initialize()
    {
        base.Initialize();
        LevelManager.SkyChanged += OnSkyChanged;
        OnSkyChanged();
        _skyHostUpdateHook = new Hook(typeof(SkyHost).GetMethod("Update"), SkyHostUpdate);
        _lightingPreDrawHook = new ILHook(
            typeof(LightingPostProcess).GetMethod("PreDraw", BindingFlags.NonPublic | BindingFlags.Instance),
            LightingPostProcessPreDraw);
    }

    protected override void Dispose(bool disposing)
    {
        _skyHostUpdateHook?.Dispose();
        _lightingPreDrawHook?.Dispose();
        base.Dispose(disposing);
    }

    private void OnSkyChanged()
    {
        // The initial delay before a flicker is 2-10s (game time)
        ResetFlicker(RandomHelper.Between(2.77, 13.85));
    }

    private void ResetFlicker(float delaySeconds)
    {
        // Replicate the vanilla flickering effect -- 1 or 2 flashes, delayed depending on context.
        _flashesLeft = RandomHelper.Probability(0.5) ? 1 : 2;
        _secondsIntoFlash = -delaySeconds;
        _holdSeconds = RandomHelper.Between(0.07, 0.35);
    }

    private void NextFlash()
    {
        // Same as vanilla -- 0.05-0.25s (game time) delay between flashes, and 2-4s (game time) delay between flickers.
        if (--_flashesLeft <= 0)
        {
            ResetFlicker(RandomHelper.Between(2.77, 5.54));
            return;
        }
        _secondsIntoFlash = -RandomHelper.Between(0.07, 0.35);
        _holdSeconds = RandomHelper.Between(0.07, 0.35);
    }

    private void SkyHostUpdate(Action<SkyHost, GameTime> orig, SkyHost self, GameTime gameTime)
    {
        if (LevelManager.Sky?.Name != "INDUS_CITY")
        {
            orig(self, gameTime);
            return;
        }

        // Vanilla's SkyHost.Update returns early in these states, which pauses its flicker partway through.
        var delta = 0f;
        if (!GameState.Paused && !GameState.InMap && !(GameState.Loading && !GameState.FarawaySettings.InTransition))
        {
            delta = (float)gameTime.ElapsedGameTime.TotalSeconds;
            _secondsIntoFlash += delta;
        }
        var brightness = 0f;

        // This is the first frame where the flash is active, mute the rain like vanilla does
        if (InProgress(_secondsIntoFlash, delta))
        {
            SoundManager.MuteAmbience("Ambience ^ rain", 0f);
        }

        // The flash has started, the brightness should fade in quickly
        if (InProgress(_secondsIntoFlash, FadeInSeconds))
        {
            brightness = Easing.EaseInOut(_secondsIntoFlash / FadeInSeconds, EasingType.Sine);
        }

        // Hold the brightness at maximum for _holdSeconds
        if (InProgress(_secondsIntoFlash - FadeInSeconds, _holdSeconds))
        {
            brightness = 1f;
        }

        // This is the first frame where the fade-out has started, bring the rain back (vanilla does this when the sky goes dark)
        if (InProgress(_secondsIntoFlash - FadeInSeconds - _holdSeconds, delta))
        {
            SoundManager.UnmuteAmbience("Ambience ^ rain", 0f);
        }

        // Fade the brightness back out for FadeOutSeconds
        if (InProgress(_secondsIntoFlash - FadeInSeconds - _holdSeconds, FadeOutSeconds))
        {
            brightness = 1f - Easing.EaseInOut((_secondsIntoFlash - FadeInSeconds - _holdSeconds) / FadeOutSeconds, EasingType.Sine);
        }

        // The flash has finished; PositiveInfinity makes this window open-ended.
        if (InProgress(_secondsIntoFlash - FadeInSeconds - _holdSeconds - FadeOutSeconds, float.PositiveInfinity))
        {
            NextFlash();
        }

        // Like vanilla, the sky is drawn lit while flickering, and the light map darkens it (see LightMapClearColor).
        _brightness = brightness;
        FlickerInField.SetValue(self, float.MaxValue);
        self.flickering = brightness > 0f;
        // Vanilla's DoFlicker hides the first 16 layers while the sky is lit; this fades them instead.
        for (var i = 0; i < 16; i++)
        {
            var group = self.BgLayers.Groups[i];
            group.Material.Opacity = (float)OriginalOpacityField.GetValue(group.CustomData) * (1f - brightness * PeakOpacity);
        }

        orig(self, gameTime);
    }

    // Vanilla clears the light map to black while this sky isn't flickering, which darkens the whole screen,
    // and to neutral grey while it is. This always takes the black branch, then swaps in LightMapClearColor.
    private void LightingPostProcessPreDraw(ILContext il)
    {
        var cursor = new ILCursor(il);
        cursor.GotoNext(MoveType.After, i => i.MatchLdfld<SkyHost>("flickering"));
        cursor.Emit(OpCodes.Pop);
        cursor.Emit(OpCodes.Ldc_I4_0);
        cursor.GotoNext(MoveType.After, i => i.MatchNewobj<Color>());
        cursor.Emit(OpCodes.Pop);
        cursor.EmitDelegate<Func<Color>>(LightMapClearColor);
    }

    // Fades between vanilla's black (dark) and neutral grey (lit) clear colors.
    private Color LightMapClearColor()
    {
        var grey = 0.5f * _brightness * PeakOpacity;
        return new Color(grey, grey, grey, 0f);
    }
}
