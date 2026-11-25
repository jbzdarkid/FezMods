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

// Vanilla's INDUS_CITY sky flickers on and off instead of using RainHost's lightning.
internal sealed class SkyFlickerHost : DrawableGameComponent
{
    private const float PeakOpacity = 1f;
    private const float FadeInSeconds = 0.2f;
    private const float FadeOutSeconds = 0.2f;
    // Vanilla's DoFlicker counts in TimeFactor / 360 seconds, and the default TimeFactor is 260.
    private const float VanillaTimeScale = 360f / 260f;

    private static readonly FieldInfo OriginalOpacityField = typeof(SkyHost).GetNestedType("BgLayerState", BindingFlags.NonPublic).GetField("OriginalOpacity");

    // Overwritten every frame so vanilla's DoFlicker never fires.
    private static readonly FieldInfo FlickerInField = typeof(SkyHost).GetField("flickerIn", BindingFlags.NonPublic | BindingFlags.Instance);

    [ServiceDependency]
    public IGameLevelManager LevelManager { private get; set; }

    [ServiceDependency]
    public IGameStateManager GameState { private get; set; }

    [ServiceDependency]
    public ISoundManager SoundManager { private get; set; }

    [ServiceDependency]
    public ITargetRenderingManager TargetRenderer { private get; set; }

    private Hook _skyHostUpdateHook;
    // Starts negative and counts down to 0 at the moment the sky flickers.
    // Then counts up to track the different pieces of the flicker, until it resets back to a negative (for the next one).
    private float _secondsIntoFlicker;
    private float _holdSeconds;
    // Includes the flicker in progress.
    private int _flickersLeft;
    private float _brightness;

    public SkyFlickerHost(Game game) : base(game)
    {
        // After SkyHost (0), before the level (5) and the rain (20), so only the sky is darkened.
        DrawOrder = 2;
    }

    public override void Initialize()
    {
        base.Initialize();
        LevelManager.LevelChanged += ResetBurst;
        ResetBurst();
        _skyHostUpdateHook = new Hook(typeof(SkyHost).GetMethod("Update"), SkyHostUpdate);
    }

    protected override void Dispose(bool disposing)
    {
        _skyHostUpdateHook?.Dispose();
        base.Dispose(disposing);
    }

    private void ResetBurst()
    {
        // At most 2 flickers per burst, with bursts 2.8s+ apart, keeps this under 3 flashes per second.
        _flickersLeft = RandomHelper.Random.Next(1, 3);
        _secondsIntoFlicker = -RandomHelper.Between(2.0, 4.0) * VanillaTimeScale;
        _holdSeconds = RandomHelper.Between(0.05, 0.25) * VanillaTimeScale;
    }

    private void NextFlicker()
    {
        _flickersLeft--;
        if (_flickersLeft == 0)
        {
            ResetBurst();
            return;
        }
        _secondsIntoFlicker = -RandomHelper.Between(0.05, 0.25) * VanillaTimeScale;
        _holdSeconds = RandomHelper.Between(0.05, 0.25) * VanillaTimeScale;
    }

    private void SkyHostUpdate(Action<SkyHost, GameTime> orig, SkyHost self, GameTime gameTime)
    {
        if (LevelManager.Sky?.Name != "INDUS_CITY")
        {
            orig(self, gameTime);
            return;
        }

        var canStartFlicker = !GameState.Paused && !GameState.InMap && !GameState.Loading;
        var delta = 0f;
        if (_secondsIntoFlicker >= 0f || canStartFlicker)
        {
            delta = (float)gameTime.ElapsedGameTime.TotalSeconds;
            _secondsIntoFlicker += delta;
        }
        var brightness = 0f;

        // This is the first frame where the flicker is active, mute the rain like vanilla does
        if (InProgress(_secondsIntoFlicker, delta))
        {
            SoundManager.MuteAmbience("Ambience ^ rain", 0f);
        }

        // The flicker has started, the brightness should fade in quickly
        if (InProgress(_secondsIntoFlicker, FadeInSeconds))
        {
            brightness = Easing.EaseInOut(_secondsIntoFlicker / FadeInSeconds, EasingType.Sine);
        }

        // Hold the brightness at maximum for _holdSeconds
        if (InProgress(_secondsIntoFlicker - FadeInSeconds, _holdSeconds))
        {
            brightness = 1f;
        }

        // This is the first frame where the fade-out has started, bring the rain back
        if (InProgress(_secondsIntoFlicker - FadeInSeconds - _holdSeconds, delta))
        {
            SoundManager.UnmuteAmbience("Ambience ^ rain", 0f);
        }

        // Fade the brightness back out for FadeOutSeconds
        if (InProgress(_secondsIntoFlicker - FadeInSeconds - _holdSeconds, FadeOutSeconds))
        {
            brightness = 1f - Easing.EaseInOut((_secondsIntoFlicker - FadeInSeconds - _holdSeconds) / FadeOutSeconds, EasingType.Sine);
        }

        // The flicker has finished; PositiveInfinity makes this window open-ended.
        if (InProgress(_secondsIntoFlicker - FadeInSeconds - _holdSeconds - FadeOutSeconds, float.PositiveInfinity))
        {
            NextFlicker();
        }

        // Vanilla's flickering state is held for the whole flicker, and Draw darkens it back down.
        _brightness = brightness;
        FlickerInField.SetValue(self, float.MaxValue);
        self.flickering = _secondsIntoFlicker >= 0f;
        for (var i = 0; i < 16; i++)
        {
            var group = self.BgLayers.Groups[i];
            group.Material.Opacity = (float)OriginalOpacityField.GetValue(group.CustomData) * (1f - brightness * PeakOpacity);
        }

        orig(self, gameTime);
    }

    public override void Draw(GameTime gameTime)
    {
        if (GameState.Loading || GameState.InMap || _secondsIntoFlicker < 0f)
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
