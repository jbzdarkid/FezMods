using FezEngine.Tools;
using Microsoft.Xna.Framework;

namespace TempLightningMod;

// HAT creates this component using its public Game constructor.
public sealed class ModComponent : GameComponent
{
    public ModComponent(Game game) : base(game)
    {
    }

    public override void Initialize()
    {
        base.Initialize();
        ServiceHelper.AddComponent(new RainFlashHost(Game));
        ServiceHelper.AddComponent(new SkyFlickerHost(Game));
    }

    internal static bool InProgress(float elapsed, float duration)
    {
        return 0f <= elapsed && elapsed < duration;
    }
}
