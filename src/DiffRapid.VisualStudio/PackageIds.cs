using System;

namespace DiffRapid.VisualStudio;

/// <summary>GUIDs and command ids; must match DiffRapidPackage.vsct.</summary>
internal static class PackageIds
{
    public const string PackageGuidString = "d62115b4-a651-44ed-87ba-2400bf8f2a7e";
    public const string CommandSetGuidString = "cd4a73c1-601e-4ff6-83c2-8fef6a7455e5";
    public const string DiffWindowGuidString = "26ef4ac2-8d5b-4904-a008-96ca9cd7af9b";

    public static readonly Guid CommandSet = new(CommandSetGuidString);

    public const int CompareSelected = 0x0100;
    public const int CompareFiles = 0x0101;
    public const int CompareFolders = 0x0102;

    // File comparison window commands, bound to Alt+arrows in its key binding scope (DiffWindowGuid).
    public const int PreviousDifference = 0x0200;
    public const int NextDifference = 0x0201;
    public const int CopyToLeft = 0x0202;
    public const int CopyToRight = 0x0203;

    /// <summary>Name of the key binding scope in Tools > Options > Keyboard (VSPackage.resx).</summary>
    public const short KeyBindingScopeNameResource = 110;
}
