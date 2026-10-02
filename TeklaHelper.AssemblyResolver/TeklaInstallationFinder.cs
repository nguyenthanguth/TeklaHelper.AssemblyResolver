using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace TeklaHelper.AssemblyResolver
{
    /// <summary>
    /// Discovers Tekla Structures installations in the Windows Registry (HKEY_LOCAL_MACHINE).
    /// </summary>
    public static class TeklaInstallationFinder
    {
        private static readonly string[] TeklaRegistryRoots = new[]
        {
            @"SOFTWARE\Trimble\Tekla Structures",
            @"SOFTWARE\Tekla\Structures"
        };

        /// <summary>
        /// Scans HKLM in both the 64-bit and 32-bit registry views
        /// and returns every Tekla Structures version installed on this machine.
        /// Registry keys that cannot be read (e.g. access denied) are skipped.
        /// </summary>
        /// <returns>
        /// List of <see cref="TeklaInstallation"/> objects, sorted from newest to oldest version.
        /// Returns an empty list on non-Windows platforms.
        /// </returns>
        public static List<TeklaInstallation> FindAll()
        {
            var results = new List<TeklaInstallation>();

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return results;
            }

            // Read the 64-bit registry view first, then the 32-bit view
            RegistryView[] views = { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var view in views)
            {
                string viewLabel = view == RegistryView.Registry64 ? "64-bit" : "32-bit";

                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    {
                        foreach (var rootPath in TeklaRegistryRoots)
                        {
                            ReadRoot(baseKey, rootPath, viewLabel, results);
                        }
                    }
                }
                catch (Exception ex) when (IsRegistryAccessError(ex))
                {
                    // This registry view cannot be opened; continue with the next one
                }
            }

            return TeklaInstallation.SortNewestFirst(results);
        }

        private static void ReadRoot(RegistryKey baseKey, string rootPath, string viewLabel, List<TeklaInstallation> results)
        {
            string[] subKeyNames;
            try
            {
                using (var teklaKey = baseKey.OpenSubKey(rootPath))
                {
                    if (teklaKey == null) return;
                    subKeyNames = teklaKey.GetSubKeyNames();
                }
            }
            catch (Exception ex) when (IsRegistryAccessError(ex))
            {
                return;
            }

            foreach (var subKeyName in subKeyNames)
            {
                // Version sub-keys are usually named like "2026.0", "2020.0", ...
                string setupSubKeyPath = $@"{rootPath}\{subKeyName}\setup";
                TeklaInstallation installation;
                try
                {
                    installation = ReadInstallation(baseKey, setupSubKeyPath, subKeyName, viewLabel);
                }
                catch (Exception ex) when (IsRegistryAccessError(ex))
                {
                    // Skip only this version; the other installations can still be used
                    continue;
                }

                // Skip versions that have already been added (e.g. the same version in both registry views)
                if (installation != null &&
                    !results.Any(x => string.Equals(x.Version, installation.Version, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(installation);
                }
            }
        }

        private static TeklaInstallation ReadInstallation(RegistryKey baseKey, string setupSubKeyPath, string subKeyName, string viewLabel)
        {
            using (var setupKey = baseKey.OpenSubKey(setupSubKeyPath))
            {
                if (setupKey == null) return null;

                return new TeklaInstallation
                {
                    MainDirectory = ReadString(setupKey, "MainDir"),
                    VersionDirectoryName = ReadString(setupKey, "TSVersionDir"),
                    // Fall back to the sub-key name when Version is missing or blank
                    Version = ReadString(setupKey, "Version") ?? subKeyName,
                    ProductVersion = ReadString(setupKey, "ProductVersion"),
                    EnvironmentDirectory = ReadString(setupKey, "EnvDir"),
                    ModelDirectory = ReadString(setupKey, "ModelDir"),
                    RegistryPath = $@"HKLM\{setupSubKeyPath} ({viewLabel})"
                };
            }
        }

        /// <summary>
        /// Reads a registry value as a string. Returns null if the value is missing, empty or whitespace.
        /// </summary>
        private static string ReadString(RegistryKey key, string name)
        {
            string value = key.GetValue(name)?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static bool IsRegistryAccessError(Exception ex)
        {
            return ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException;
        }
    }
}
