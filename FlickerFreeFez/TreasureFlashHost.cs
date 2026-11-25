using System.Reflection;
using FezGame.Components;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour;
using static FlickerFreeFez.ModComponent;

namespace FlickerFreeFez;

// Disables the white flash when collecting some secret items (like heart pieces).
internal sealed class TreasureFlashHost : GameComponent
{
    private static readonly Type GlitchyDespawnerType = typeof(RainHost).Assembly.GetType("FezGame.Components.GlitchyDespawner", throwOnError: true);

    private static readonly FieldInfo FlashOnSpawnField = GetPrivateField(GlitchyDespawnerType, "FlashOnSpawn");

    private Hook _glitchyDespawnerInitializeHook;

    public TreasureFlashHost(Game game) : base(game)
    {
    }

    public override void Initialize()
    {
        base.Initialize();
        _glitchyDespawnerInitializeHook = new Hook(GlitchyDespawnerType.GetMethod("Initialize"), GlitchyDespawnerInitialize);
    }

    protected override void Dispose(bool disposing)
    {
        _glitchyDespawnerInitializeHook?.Dispose();
        base.Dispose(disposing);
    }

    private void GlitchyDespawnerInitialize(Action<DrawableGameComponent> orig, DrawableGameComponent self)
    {
        // Completely disable the behavior for all entities.
        FlashOnSpawnField.SetValue(self, false);
        orig(self);
    }
}
