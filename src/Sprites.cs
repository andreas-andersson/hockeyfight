// Sprites.cs
// Hockey Fight
//
// Loads the sprite sheets that are embedded in the executable. On macOS these
// live in the .saver bundle's Resources directory and are read with
// [NSBundle pathForResource:ofType:]; here they are embedded resources so the
// .scr stays a single self-contained file.

using System.Drawing;
using System.Diagnostics;
using System.Reflection;

namespace HockeyFight;

internal static class Sprites
{
    /// <summary>
    /// Loads an embedded PNG, or returns null if it is missing or unreadable.
    /// Mirrors the defensive nil-checks in Hockey_FightView's initializer: a
    /// missing sprite sheet skips its drawing rather than taking the whole
    /// screensaver down.
    /// </summary>
    public static Bitmap? Load(string resourceName)
    {
        Assembly assembly = typeof(Sprites).Assembly;

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            Debug.WriteLine($"Could not find {resourceName} in assembly resources");
            return null;
        }

        try
        {
            // Decode into a memory stream, then copy into an independent bitmap so
            // the image does not keep a reference to the resource stream.
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;

            using var decoded = new Bitmap(buffer);
            return new Bitmap(decoded);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
        {
            Debug.WriteLine($"Failed to load or validate {resourceName}: {ex.Message}");
            return null;
        }
    }
}
