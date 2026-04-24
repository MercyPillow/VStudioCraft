using System;

namespace VStudioCraft
{
    internal static class PackageGuids
    {
        public const string PackageGuidString = "a8d1c7e2-3f9b-4c5d-8e2a-1b6d4f7c9e3a";
        public static readonly Guid PackageGuid = new Guid(PackageGuidString);

        public const string EditorFactoryGuidString = "7e4a9b1c-2d8f-4a6b-9c3e-5f1d8a2b7c4e";
        public static readonly Guid EditorFactoryGuid = new Guid(EditorFactoryGuidString);

        public const string CommandSetGuidString = "b3f2d6e8-1a4c-47e5-9b2a-6d8f3c5e1a7b";
        public static readonly Guid CommandSet = new Guid(CommandSetGuidString);
    }
}
