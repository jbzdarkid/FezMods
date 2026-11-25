using FezEngine.Tools;
using Microsoft.Xna.Framework;
using System;
using System.Reflection;

namespace FlickerFreeFez;

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
        ServiceHelper.AddComponent(new TreasureFlashHost(Game));
    }

    internal static bool InProgress(float elapsed, float duration)
    {
        return 0f <= elapsed && elapsed < duration;
    }

    internal static FieldInfo GetPrivateField<T>(string name)
    {
        return GetPrivateField(typeof(T), name);
    }

    internal static FieldInfo GetPrivateField(Type type, string name)
    {
        return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Could not find field '{name}' on '{type.FullName}'.");
    }
}
