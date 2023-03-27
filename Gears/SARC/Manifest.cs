using NewGear.GearSystem.GearLoading;
using NewGear.Gears.Containers;
using NewGear.GearSystem.Enums;

public class Manifest : GearManifest
{
    public override string Name => "SARC";

    public override string Description => "A simple gear for SARC files.";

    public override string[] Authors => new string[] { "Lord-Giganticus" };

    public override string[] OriginalSources => new string[] { "https://github.com/Lord-Giganticus/LordG.IO" };

    public override GearEntry[] Entries => new GearEntry[]
    {
        new(typeof(SARC))
        {
            ContextMenu = DefaultContextMenus.ContainerMenu,
            DefaultEditor = EditorEntries.FileTree,
            Identify = DefaultIdentifyMethods.IdentifyByMagic("SARC")
        }
    };
}