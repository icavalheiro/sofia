using System.IO;

namespace Sofia.App.Tests;

/// <summary>Locates the repository's <c>.specs/spec_assets</c> folder so the harness writes its PNG evidence next to the
/// spec that owns it (the folder is git-ignored; nothing here is committed).</summary>
internal static class Evidence
{
    public static string Directory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("SOFIA_APP_SHOTS_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                System.IO.Directory.CreateDirectory(configured);
                return configured;
            }

            var current = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(current.FullName, "Sofia.slnx")))
                {
                    var path = System.IO.Path.Combine(current.FullName, ".specs", "spec_assets");
                    System.IO.Directory.CreateDirectory(path);
                    return path;
                }
                current = current.Parent;
            }

            var fallback = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sofia-app-evidence");
            System.IO.Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static string File(string fileName) => System.IO.Path.Combine(Directory, fileName);
}
