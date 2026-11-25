using System.Reflection;
using FezEngine.Components;
using FezEngine.Services;
using FezEngine.Structure;
using FezEngine.Tools;
using FezGame.Services;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.RuntimeDetour;
using static FlickerFreeFez.ModComponent;

namespace FlickerFreeFez;

// Replaces the NU ZU flickering sky with a slower, in-and-out effect.
internal sealed class SkyFlickerHost : DrawableGameComponent
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

    [ServiceDependency]
    public ITargetRenderingManager TargetRenderer { private get; set; }

    private Hook _skyHostUpdateHook;
    // Starts negative and counts up to 0 at the start of each flash in a sky flicker (1 or 2 flashes).
    // Then keeps counting to track the different parts of the flash, until it resets back to a negative (for the next one).
    private float _secondsIntoFlash;
    private float _holdSeconds;
    private int _flashesLeft;
    private float _brightness;

    public SkyFlickerHost(Game game) : base(game)
    {
        // After SkyHost (0), before the level (5) and the rain (20), so only the sky is darkened.
        DrawOrder = 2;
    }

    public override void Initialize()
    {
        base.Initialize();
        LevelManager.SkyChanged += OnSkyChanged;
        OnSkyChanged();
        _skyHostUpdateHook = new Hook(typeof(SkyHost).GetMethod("Update"), SkyHostUpdate);
    }

    protected override void Dispose(bool disposing)
    {
        _skyHostUpdateHook?.Dispose();
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

        // Vanilla draws the sky lit while flickering, and Draw darkens it back down. At zero brightness, vanilla's own dark state takes over.
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

    public override void Draw(GameTime gameTime)
    {
        // Vanilla's SkyHost.Draw skips the sky in these states.
        if ((GameState.Loading && !GameState.FarawaySettings.InTransition) || GameState.InMap || _secondsIntoFlash < 0f)
        {
            return;
        }

        // Only the sky has been drawn so far, so this doesn't need a stencil mask.
        GraphicsDevice.SetBlendingMode(BlendingMode.Alphablending);
        GraphicsDevice.PrepareStencilRead(CompareFunction.Always, StencilMask.None);
        TargetRenderer.DrawFullscreen(new Color(0f, 0f, 0f, 1f - _brightness * PeakOpacity));
        GraphicsDevice.PrepareStencilWrite(StencilMask.None);
    }
}
