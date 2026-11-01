using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;

namespace VideoDispatch.Editing;

/// <summary>
/// Microsoft's library for reaching Windows' media tools is 23 MB, and most of it is never used. So the published
/// program holds it zipped, at about 6 MB, and unpacks it into memory the first time something is encoded. Posting never
/// touches it. When it isn't packed, as in a debug build, it's beside the program and this never runs.
/// </summary>
internal static class WinRtLibrary
{
    private const string LibraryName = "Microsoft.Windows.SDK.NET";
    private const string ResourceName = "winrt.zip";

    private static readonly object Lock = new();
    private static bool _installed;

    public static void Install()
    {
        lock (Lock)
        {
            if (!_installed)
            {
                _installed = true;
                AssemblyLoadContext.Default.Resolving += Resolve;
            }
        }
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name != LibraryName || Assembly.GetEntryAssembly()?.GetManifestResourceStream(ResourceName) is not { } packed)
        {
            return null;
        }

        using (packed)
        using (var zip = new ZipArchive(packed))
        using (var entry = zip.Entries.Single(found => found.Name == LibraryName + ".dll").Open())
        using (var library = new MemoryStream())
        {
            entry.CopyTo(library);
            library.Position = 0;
            return context.LoadFromStream(library);
        }
    }
}
