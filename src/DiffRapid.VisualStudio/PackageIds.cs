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
}
