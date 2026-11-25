using System.Reflection;
using FezEngine.Components;
using FezEngine.Services;
using FezEngine.Structure;
using FezEngine.Tools;
using FezGame.Components;
using FezGame.Services;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using MonoMod.RuntimeDetour;
using static FlickerFreeFez.ModComponent;

namespace FlickerFreeFez;

// Replaces the vanilla graveyard's sharp lightning flashes with a smoother flash.
internal sealed class RainFlashHost : GameComponent
{
    private const float PeakOpacity = 0.75f;
    private const float FadeInSeconds = 0.2f;
    private const float HoldSeconds = 0.1f;
    private const float FadeOutSeconds = 0.2f;

    private static readonly FieldInfo DoThunderField = GetPrivateField<RainHost>("doThunder");
    private static readonly FieldInfo LightningField = GetPrivateField<RainHost>("lightning");
    private static readonly FieldInfo PreThunderSoundField = GetPrivateField<RainHost>("sPreThunder");
    private static readonly FieldInfo LightningSoundsField = GetPrivateField<RainHost>("lightningSounds");

    // These three are used to customize the base game's Draw behavior.
    private static readonly FieldInfo FlashInField = GetPrivateField<RainHost>("flashIn");
    private static readonly FieldInfo FlashOpacityField = GetPrivateField<RainHost>("flashOpacity");
    private static readonly FieldInfo SinceFlashField = GetPrivateField<RainHost>("sinceFlash");

    [ServiceDependency]
    public IGameLevelManager LevelManager { private get; set; }

    [ServiceDependency]
    public IGameStateManager GameState { private get; set; }

    [ServiceDependency]
    public IDefaultCameraManager CameraManager { private get; set; }

    private Hook _rainHostDrawHook;
    // Starts negative and counts down to 0 at the moment the lightning strikes.
    // Then counts up to track the different pieces of the strike, until it resets back to a negative (for the next one).
    private float _secondsIntoStrike;
    private SoundEmitter _preThunder;

    public RainFlashHost(Game game) : base(game)
    {
    }

    public override void Initialize()
    {
        base.Initialize();
        LevelManager.LevelChanged += ResetLightningStrike;
        ResetLightningStrike();
        _rainHostDrawHook = new Hook(typeof(RainHost).GetMethod("Draw"), RainHostDraw);
    }

    protected override void Dispose(bool disposing)
    {
        _rainHostDrawHook?.Dispose();
        base.Dispose(disposing);
    }

    private void ResetLightningStrike()
    {
        _secondsIntoStrike = -RandomHelper.Between(4.0, 6.0);
    }

    private void RainHostDraw(Action<RainHost, GameTime> orig, RainHost self, GameTime gameTime)
    {
        if (GameState.Loading || !(bool)DoThunderField.GetValue(self))
        {
            orig(self, gameTime);
            return;
        }

        var lightning = (Mesh)LightningField.GetValue(self);

        var canStartStrike = !GameState.Paused && CameraManager.Viewpoint.IsOrthographic() && CameraManager.ActionRunning && !GameState.InMap;
        var delta = 0f;
        if (_secondsIntoStrike >= 0f || canStartStrike)
        {
            delta = (float)gameTime.ElapsedGameTime.TotalSeconds;
            _secondsIntoStrike += delta;
        }
        var brightness = 0f;

        // This is the first frame where the lightning strike is active, play the visual
        if (InProgress(_secondsIntoStrike, delta))
        {
            lightning.Rotation = CameraManager.Rotation;
            lightning.Position = CameraManager.Center
                + LevelManager.Size / 2f * CameraManager.Viewpoint.ForwardVector()
                + RandomHelper.Centered(1.0) * CameraManager.Radius * 0.5f * CameraManager.Viewpoint.RightVector()
                + RandomHelper.Centered(10.0) * Vector3.UnitY;
            var soundPosition = lightning.Position * FezMath.XZMask + CameraManager.Center * Vector3.UnitY;
            var preThunderSound = (SoundEffect)PreThunderSoundField.GetValue(self);
            _preThunder = preThunderSound.EmitAt(soundPosition, RandomHelper.Centered(0.025));
            _preThunder.NoAttenuation = true;
        }

        // The strike has started, the brightness should fade in gradually
        if (InProgress(_secondsIntoStrike, FadeInSeconds))
        {
            brightness = Easing.EaseInOut(_secondsIntoStrike / FadeInSeconds, EasingType.Sine);
        }

        // This is the first frame where the fade-in has finished, play the audio
        if (InProgress(_secondsIntoStrike - FadeInSeconds, delta))
        {
            if (_preThunder != null && !_preThunder.Dead)
            {
                _preThunder.Cue.Stop();
            }
            var soundPosition = lightning.Position * FezMath.XZMask + CameraManager.Center * Vector3.UnitY;
            var lightningSounds = (SoundEffect[])LightningSoundsField.GetValue(self);
            var emitter = RandomHelper.InList(lightningSounds).EmitAt(soundPosition, RandomHelper.Centered(0.025));
            emitter.NoAttenuation = true;
            emitter.Persistent = true;
            Waiters.DoUntil(() => emitter.Dead, _ => emitter.Position = Vector3.Lerp(emitter.Position, CameraManager.Center, 0.025f))
                .AutoPause = true;
        }

        // Hold the brightness at maximum for HoldSeconds
        if (InProgress(_secondsIntoStrike - FadeInSeconds, HoldSeconds))
        {
            brightness = 1f;
        }

        // Fade the brightness back out for FadeOutSeconds
        if (InProgress(_secondsIntoStrike - FadeInSeconds - HoldSeconds, FadeOutSeconds))
        {
            brightness = 1f - Easing.EaseInOut((_secondsIntoStrike - FadeInSeconds - HoldSeconds) / FadeOutSeconds, EasingType.Sine);
        }

        // The strike has finished; PositiveInfinity makes this window open-ended.
        if (InProgress(_secondsIntoStrike - FadeInSeconds - HoldSeconds - FadeOutSeconds, float.PositiveInfinity))
        {
            ResetLightningStrike();
        }

        // Apply our customizations (and disable the vanilla game's lightning effect)
        FlashInField.SetValue(self, TimeSpan.MaxValue);
        FlashOpacityField.SetValue(self, brightness * PeakOpacity);
        SinceFlashField.SetValue(self, TimeSpan.FromSeconds(-0.2 * brightness));

        orig(self, gameTime);
    }
}
