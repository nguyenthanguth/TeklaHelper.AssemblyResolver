using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TeklaHelper.AssemblyResolver
{
    /// <summary>
    /// Describes a single Tekla Structures installation as read from the Windows Registry.
    /// </summary>
    public class TeklaInstallation
    {
        private static readonly bool IsNetFramework =
            RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase);

        private string _mainDirectory;
        private string _versionDirectoryName;
        private string _version;

        // Cached because the resolver reads them on every AssemblyResolve event; reset when the source values change
        private Version _parsedVersion;
        private bool _parsedVersionComputed;
        private List<string> _binDirectories;
        private List<string> _binSubDirectories; // set together with _binDirectories

        /// <summary>
        /// Root installation directory (registry value <c>MainDir</c>), e.g. <c>C:\TeklaStructures\</c>.
        /// </summary>
        public string MainDirectory
        {
            get => _mainDirectory;
            internal set
            {
                _mainDirectory = value;
                _binDirectories = null;
            }
        }

        /// <summary>
        /// Version sub-directory name (registry value <c>TSVersionDir</c>), e.g. <c>2026.0</c>.
        /// </summary>
        public string VersionDirectoryName
        {
            get => _versionDirectoryName;
            internal set
            {
                _versionDirectoryName = value;
                _parsedVersionComputed = false;
                _binDirectories = null;
            }
        }

        /// <summary>
        /// Version string (registry value <c>Version</c>, or the version sub-key name if missing), e.g. <c>2026.0</c>.
        /// </summary>
        public string Version
        {
            get => _version;
            internal set
            {
                _version = value;
                _parsedVersionComputed = false;
            }
        }

        /// <summary>
        /// Full product build number (registry value <c>ProductVersion</c>), e.g. <c>226.3.61483</c>.
        /// </summary>
        public string ProductVersion { get; internal set; }

        /// <summary>
        /// Environments directory (registry value <c>EnvDir</c>).
        /// </summary>
        public string EnvironmentDirectory { get; internal set; }

        /// <summary>
        /// Default models directory (registry value <c>ModelDir</c>).
        /// </summary>
        public string ModelDirectory { get; internal set; }

        /// <summary>
        /// Registry key the information was read from, including the registry view (64-bit or 32-bit).
        /// </summary>
        public string RegistryPath { get; internal set; }

        /// <summary>
        /// Version parsed from <see cref="Version"/> (or from <see cref="VersionDirectoryName"/> if <see cref="Version"/> is invalid).
        /// For example "2026.0" → 2026.0, "21.1" → 21.1. Returns null if neither value can be parsed.
        /// </summary>
        public Version ParsedVersion
        {
            get
            {
                if (!_parsedVersionComputed)
                {
                    _parsedVersion = ParseVersion(Version) ?? ParseVersion(VersionDirectoryName);
                    _parsedVersionComputed = true;
                }

                return _parsedVersion;
            }
        }

        /// <summary>
        /// Major version number (e.g. 2026, 2020, 21). Returns 0 if the version cannot be determined.
        /// </summary>
        public int MajorVersion => ParsedVersion?.Major ?? 0;

        /// <summary>
        /// Installation directory of this Tekla Structures version (<see cref="MainDirectory"/> combined with <see cref="VersionDirectoryName"/>).
        /// For example: C:\TeklaStructures\2026.0
        /// </summary>
        public string InstallDirectory
        {
            get
            {
                string mainDir = CleanPath(MainDirectory);
                string versionDir = CleanPath(VersionDirectoryName);
                if (mainDir.Length == 0 || versionDir.Length == 0)
                {
                    return string.Empty;
                }

                try
                {
                    return Path.Combine(mainDir, versionDir);
                }
                catch (ArgumentException)
                {
                    // The registry value contains characters that are invalid in a path
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// Gets the path of the shared Tekla extensions directory (Environments\common\extensions).
        /// Only computes the path; it does not create the directory. Use <see cref="EnsureExtensionsDirectory"/> to create it.
        /// </summary>
        /// <returns>Full path to the extensions directory, or an empty string if <see cref="InstallDirectory"/> cannot be determined.</returns>
        public string GetExtensionsDirectory()
        {
            string installDir = InstallDirectory;
            if (installDir.Length == 0)
            {
                return string.Empty;
            }

            return Path.Combine(installDir, "Environments", "common", "extensions");
        }

        /// <summary>
        /// Creates the extensions directory if it does not exist yet.
        /// </summary>
        /// <returns>true if the directory already exists or was created; false if write access is denied or an IO error occurs.</returns>
        public bool EnsureExtensionsDirectory()
        {
            string path = GetExtensionsDirectory();
            if (path.Length == 0)
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(path);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the existing bin directories that contain the DLLs of this Tekla Structures version, in priority order:
        /// - bin\Net48Runtime: Tekla 2026 and later, DLLs built for .NET Framework 4.8
        ///   (searched before bin when running on .NET Framework, after bin otherwise)
        /// - bin: Tekla 2021 and later
        /// - nt\bin\plugins: Tekla 2020 and earlier, contains the Open API DLLs (must come before nt\bin)
        /// - nt\bin: Tekla 2020 and earlier
        /// - the direct sub-folders of bin and nt\bin not listed above, in alphabetical order
        ///   (e.g. nt\bin\dialogs contains Tekla.Structures.Dialog on Tekla 2020). They come last because some of them
        ///   keep other versions of DLLs that are also in the folders above (e.g. nt\bin\symed\dxkit.dll).
        /// </summary>
        /// <returns>List of bin directory paths that exist on disk (checked once, then cached).</returns>
        public List<string> GetBinDirectories()
        {
            EnsureBinDirectories();
            return _binDirectories.Concat(_binSubDirectories).ToList();
        }

        private void EnsureBinDirectories()
        {
            if (_binDirectories == null)
            {
                _binDirectories = FindBinDirectories(out _binSubDirectories);
            }
        }

        private List<string> FindBinDirectories(out List<string> subDirectories)
        {
            subDirectories = new List<string>();
            string installDir = InstallDirectory;
            if (installDir.Length == 0)
            {
                return new List<string>();
            }

            string net48RuntimeDir = Path.Combine(installDir, "bin", "Net48Runtime");
            string binDir = Path.Combine(installDir, "bin");
            string ntBinDir = Path.Combine(installDir, "nt", "bin");

            string[] candidates =
            {
                IsNetFramework ? net48RuntimeDir : binDir,
                IsNetFramework ? binDir : net48RuntimeDir,
                Path.Combine(ntBinDir, "plugins"),
                ntBinDir
            };

            var directories = candidates.Where(Directory.Exists).ToList();

            // Only one level deep: deeper folders can contain copies from other Tekla versions
            // (e.g. bin\applications\Tekla\Model\StatusSharing\Tekla.Structures.dll is version 2024 in Tekla 2026)
            subDirectories = GetSubDirectories(binDir)
                .Concat(GetSubDirectories(ntBinDir))
                .Where(x => !directories.Contains(x, StringComparer.OrdinalIgnoreCase))
                .ToList();

            return directories;
        }

        private static IEnumerable<string> GetSubDirectories(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return Enumerable.Empty<string>();
            }

            try
            {
                return Directory.GetDirectories(directory).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return Enumerable.Empty<string>();
            }
            catch (IOException)
            {
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// Searches the directories returned by <see cref="GetBinDirectories"/> of this installation for a DLL by its simple name.
        /// The file version is not checked; use <see cref="TeklaAssemblyResolver"/> to find the matching version.
        /// </summary>
        /// <param name="assemblySimpleName">Assembly name (e.g. "Tekla.Structures.Model" or "Tekla.Structures.Model.dll").</param>
        /// <returns>Full path to the .dll file if it exists; otherwise null.</returns>
        public string FindAssemblyPath(string assemblySimpleName)
        {
            return FindAssemblyPath(assemblySimpleName, null);
        }

        /// <summary>
        /// Searches the bin directories, then the additional directories, then the sub-folders of bin and nt\bin,
        /// for a DLL by its simple name. Directories added on purpose come before the sub-folders found automatically.
        /// </summary>
        /// <param name="assemblySimpleName">Assembly name (e.g. "Tekla.Structures.Model" or "Tekla.Structures.Model.dll").</param>
        /// <param name="additionalDirectories">
        /// Extra directories relative to <see cref="InstallDirectory"/> (e.g. <c>bin\plugins\Tekla</c>); absolute paths are used as-is.
        /// </param>
        /// <returns>Full path to the .dll file if it exists; otherwise null.</returns>
        internal string FindAssemblyPath(string assemblySimpleName, IEnumerable<string> additionalDirectories)
        {
            if (string.IsNullOrWhiteSpace(assemblySimpleName))
            {
                return null;
            }

            string fileName = assemblySimpleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? assemblySimpleName
                : $"{assemblySimpleName}.dll";

            EnsureBinDirectories();

            foreach (string directory in _binDirectories.Concat(ResolveDirectories(additionalDirectories)).Concat(_binSubDirectories))
            {
                string candidatePath;
                try
                {
                    candidatePath = Path.Combine(directory, fileName);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            return null;
        }

        /// <summary>
        /// Turns directories relative to <see cref="InstallDirectory"/> into full paths. Absolute paths (with a drive or a
        /// UNC share) are returned as-is; invalid paths are skipped.
        /// </summary>
        private IEnumerable<string> ResolveDirectories(IEnumerable<string> directories)
        {
            if (directories == null)
            {
                yield break;
            }

            string installDir = InstallDirectory;
            foreach (string directory in directories)
            {
                string path = CleanPath(directory);
                if (path.Length == 0)
                {
                    continue;
                }

                string resolved;
                try
                {
                    bool isAbsolute = Path.IsPathRooted(path) && Path.GetPathRoot(path).Length > 1;
                    if (isAbsolute)
                    {
                        resolved = path;
                    }
                    else if (installDir.Length > 0)
                    {
                        // "\bin\plugins" and "bin\plugins" both mean a folder inside the installation
                        resolved = Path.Combine(installDir, path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    }
                    else
                    {
                        continue;
                    }
                }
                catch (ArgumentException)
                {
                    continue;
                }

                yield return resolved;
            }
        }

        /// <summary>
        /// Searches the directories returned by <see cref="GetBinDirectories"/> of this installation for a DLL by its AssemblyName.
        /// The file version is not checked; use <see cref="TeklaAssemblyResolver"/> to find the matching version.
        /// </summary>
        /// <param name="assemblyName">The AssemblyName to look for.</param>
        /// <returns>Full path to the .dll file if it exists; otherwise null.</returns>
        public string FindAssemblyPath(AssemblyName assemblyName)
        {
            return FindAssemblyPath(assemblyName?.Name);
        }

        /// <summary>
        /// Returns a multi-line, human-readable description of this Tekla Structures installation.
        /// </summary>
        /// <returns>Formatted string describing the installation.</returns>
        public override string ToString()
        {
            string binPaths = string.Join(Environment.NewLine + new string(' ', 20), GetBinDirectories());

            return new StringBuilder()
                .AppendLine($"[Tekla Structures {Version}]")
                .AppendLine($"  - Main Dir        : {MainDirectory}")
                .AppendLine($"  - TS Version Dir  : {VersionDirectoryName}")
                .AppendLine($"  - Version         : {Version}")
                .AppendLine($"  - Product Version : {ProductVersion}")
                .AppendLine($"  - Env Directory   : {EnvironmentDirectory}")
                .AppendLine($"  - Model Directory : {ModelDirectory}")
                .AppendLine($"  - Registry Key    : {RegistryPath}")
                .AppendLine($"  - Install Dir     : {InstallDirectory}")
                .AppendLine($"  - Extension Dir   : {GetExtensionsDirectory()}")
                .Append($"  - Bin Directories : {binPaths}")
                .ToString();
        }

        /// <summary>
        /// Parses version strings such as "2026.0", "2026", "21.1" or "2026.0 SP1" into a <see cref="System.Version"/>.
        /// </summary>
        internal static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // Take the leading digits and dots, e.g. "2026.0 SP1" -> "2026.0"
            string numeric = new string(text.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
            if (numeric.Length == 0)
            {
                return null;
            }

            if (numeric.IndexOf('.') < 0)
            {
                numeric += ".0";
            }

            return System.Version.TryParse(numeric, out var version) ? version : null;
        }

        /// <summary>
        /// Sorts installations from newest to oldest: numerically by <see cref="ParsedVersion"/> (string comparison would
        /// wrongly put "21.1" above "2020.0"), then by <see cref="Version"/> text, e.g. "2026.0 SP1" before "2026.0".
        /// </summary>
        internal static List<TeklaInstallation> SortNewestFirst(IEnumerable<TeklaInstallation> installations)
        {
            return installations
                .OrderByDescending(x => x.ParsedVersion)
                .ThenByDescending(x => x.Version, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string CleanPath(string path)
        {
            return path?.Trim().Trim('"').Trim() ?? string.Empty;
        }
    }
}
