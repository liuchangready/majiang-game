using System;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// Minimal shim for the linked B1 helper sources. The B2 worker deliberately does not
    /// link the B1 Program/SuiteRunner (it must not run or modify any B1 gate); only the
    /// stable redaction helper the shared ClientHandle/B1Actions sources reference is kept.
    /// </summary>
    public static class Program
    {
        public static string Redact(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            string compact = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return compact.Length > 200 ? compact.Substring(0, 200) : compact;
        }
    }
}
