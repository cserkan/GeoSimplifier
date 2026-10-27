using System;
using System.IO;

namespace GeoSimplifier.SolidWorks.UI
{
    /// <summary>Appends PropertyManager events to %TEMP%\GeoSimplifier.log, to see what SOLIDWORKS sends while typing.</summary>
    internal static class PageLog
    {
        // GEOSIMPLIFIER_LOG names another file: a test SOLIDWORKS running beside the user's keeps its own log.
        private static readonly string FilePath = Environment.GetEnvironmentVariable("GEOSIMPLIFIER_LOG") is string own && own != ""
            ? own : Path.Combine(Path.GetTempPath(), "GeoSimplifier.log");

        public static void Write(string line)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
