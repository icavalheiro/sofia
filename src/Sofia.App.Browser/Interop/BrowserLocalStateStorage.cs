using Sofia.App.Services;

namespace Sofia.App.Browser;

/// <summary>
/// Browser <see cref="ILocalStateStorage"/>: the same one-blob document the desktop keeps in
/// <c>%APPDATA%/Sofia/state.json</c>, stored under a single <c>localStorage</c> key so drafts, panel flags, language and
/// the pairing token survive a reload (spec 044).
/// </summary>
internal sealed class BrowserLocalStateStorage : ILocalStateStorage
{
    /// <summary>The web kept one key per feature; after the cutover the native document is one key.</summary>
    public const string Key = "sofia.localState.v1";

    public string? Read()
    {
        try
        {
            return BrowserInterop.StorageGet(Key);
        }
        catch
        {
            return null;
        }
    }

    public void Write(string json)
    {
        try
        {
            BrowserInterop.StorageSet(Key, json);
        }
        catch
        {
            // Local state is a nicety: a browser with storage disabled must never break the UI.
        }
    }
}
