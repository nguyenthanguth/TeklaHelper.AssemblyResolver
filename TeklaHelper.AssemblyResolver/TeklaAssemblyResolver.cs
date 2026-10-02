using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TeklaHelper.AssemblyResolver
{
    /// <summary>
    /// Loads DLLs that the application cannot find (Tekla Open API assemblies and their dependencies) from the folders of
    /// the Tekla Structures installation in use.
    /// <para>
    /// File selection rules:
    /// - If the AssemblyName is a Tekla assembly (name starts with "Tekla." or it is signed with the Tekla public key) that
    ///   carries a Tekla version (e.g. 2026.0.0.0, 2020.0.0.0, 21.1.0.0), only the installation with the same major version
    ///   is searched, and the actual version of the DLL file is verified. If nothing matches, null is returned instead of a
    ///   DLL from another version (unless <see cref="AllowVersionFallback"/> is enabled).
    /// - Any other missing DLL (e.g. Trimble.Remoting, System.Memory, Google.Protobuf) is a dependency: only the installation
    ///   currently in use (<see cref="ActiveInstallation"/>) is searched, so DLLs of different Tekla versions are never mixed.
    ///   While the installation in use is unknown, or with <see cref="AllowVersionFallback"/>, the other installations are
    ///   searched as well, from newest to oldest. The file version must not be lower than the requested version.
    /// - In each installation the standard bin folders are searched first, then <see cref="AdditionalSearchDirectories"/>.
    /// </para>
    /// <para>
    /// Usage: call <see cref="Register(bool, Action{string})"/> at the start of Main, before calling any method that references Tekla types
    /// (the JIT loads the referenced assemblies as soon as such a method is compiled).
    /// <code>
    /// TeklaAssemblyResolver.Register();
    /// RunTeklaCode();
    /// </code>
    /// Or create and register your own instance:
    /// <code>
    /// using (var resolver = TeklaAssemblyResolver.CreateFromRegistry())
    /// {
    ///     resolver.Register();
    ///     RunTeklaCode();
    /// }
    /// </code>
    /// </para>
    /// </summary>
    public sealed class TeklaAssemblyResolver : IDisposable
    {
        private const string TeklaPublicKeyToken = "2f04dbe497b71114";

        private static readonly object DefaultSync = new object();
        private static TeklaAssemblyResolver _default;

        [ThreadStatic]
        private static bool _isWritingLog;

        private readonly List<TeklaInstallation> _installations;
        private readonly HashSet<int> _installedMajorVersions;
        private readonly HashSet<string> _notFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SearchDirectoryCollection _additionalSearchDirectories;
        private readonly object _sync = new object();
        private bool _registered;
        private bool _allowVersionFallback;
        private int _cacheGeneration;
        private volatile TeklaInstallation _activeInstallation;

        /// <summary>
        /// Creates a resolver for the given Tekla Structures installations.
        /// </summary>
        /// <param name="installations">Tekla Structures installations (usually from <see cref="TeklaInstallationFinder.FindAll"/>).</param>
        public TeklaAssemblyResolver(IEnumerable<TeklaInstallation> installations)
        {
            if (installations == null)
            {
                throw new ArgumentNullException(nameof(installations));
            }

            _installations = TeklaInstallation.SortNewestFirst(installations.Where(x => x != null));
            _installedMajorVersions = new HashSet<int>(_installations.Select(x => x.MajorVersion).Where(x => x > 0));
            _additionalSearchDirectories = new SearchDirectoryCollection(this);
        }

        /// <summary>
        /// Creates a resolver for all Tekla Structures installations found in the registry.
        /// </summary>
        public static TeklaAssemblyResolver CreateFromRegistry()
        {
            return new TeklaAssemblyResolver(TeklaInstallationFinder.FindAll());
        }

        /// <summary>
        /// Tekla Structures installations used by this resolver, sorted from newest to oldest.
        /// </summary>
        public IReadOnlyList<TeklaInstallation> Installations => _installations;

        /// <summary>
        /// The installation the Tekla assemblies in use come from: the first Tekla assembly this resolver loaded, or a Tekla
        /// assembly that was already loaded in the AppDomain. Dependencies are searched only in this installation
        /// (unless <see cref="AllowVersionFallback"/> is enabled).
        /// </summary>
        public TeklaInstallation ActiveInstallation => _activeInstallation;

        /// <summary>
        /// Extra directories searched in each installation after the standard bin folders, for DLLs that Tekla keeps in
        /// sub-folders. Paths are relative to <see cref="TeklaInstallation.InstallDirectory"/>, e.g. <c>bin\plugins\Tekla</c>
        /// (Tekla 2021+) or <c>nt\bin\plugins\Tekla</c> (Tekla 2020 and earlier); absolute paths are used as-is.
        /// The same version rules apply, so a DLL of another Tekla version found here is still rejected.
        /// <code>
        /// TeklaAssemblyResolver resolver = TeklaAssemblyResolver.Register();
        /// resolver.AdditionalSearchDirectories.Add(@"bin\plugins\Tekla");
        /// </code>
        /// </summary>
        public IList<string> AdditionalSearchDirectories => _additionalSearchDirectories;

        /// <summary>
        /// Allows loading DLLs from another Tekla version when no installation with the same major version is found, and
        /// loading dependencies from other installations when the installation in use does not have them. Defaults to false.
        /// Enabling this can cause MissingMethodException/TypeLoadException at runtime because of API differences.
        /// </summary>
        public bool AllowVersionFallback
        {
            get => _allowVersionFallback;
            set
            {
                lock (_sync)
                {
                    if (_allowVersionFallback == value) return;
                    _allowVersionFallback = value;
                    InvalidateNotFoundCache();
                }

                WriteLog($"AllowVersionFallback = {value}");
            }
        }

        /// <summary>
        /// Receives diagnostic messages: registration, every resolve request, the file that was loaded, files that were
        /// skipped and why, and requests that could not be resolved. Null (the default) disables logging.
        /// <para>
        /// It can be called from several threads at once. Exceptions thrown by it are ignored, so a failing logger never
        /// breaks assembly resolution.
        /// </para>
        /// <code>
        /// resolver.Logger = Console.WriteLine;
        /// resolver.Logger = message => File.AppendAllText(@"C:\Temp\resolver.log", $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        /// </code>
        /// </summary>
        public Action<string> Logger { get; set; }

        /// <summary>
        /// Creates a shared resolver for all Tekla Structures installations found in the registry and subscribes it to
        /// AppDomain.CurrentDomain.AssemblyResolve. Calling it again returns the same resolver.
        /// <code>
        /// TeklaAssemblyResolver.Register();
        /// RunTeklaCode();
        /// </code>
        /// </summary>
        /// <param name="allowVersionFallback">Value for <see cref="AllowVersionFallback"/>. The value from the latest call is used.</param>
        /// <param name="logger">Value for <see cref="Logger"/>. Ignored when null, so a later call without it keeps the current logger.</param>
        /// <returns>The shared resolver. Dispose it to unsubscribe.</returns>
        public static TeklaAssemblyResolver Register(bool allowVersionFallback = false, Action<string> logger = null)
        {
            lock (DefaultSync)
            {
                if (_default == null)
                {
                    _default = CreateFromRegistry();
                }

                if (logger != null)
                {
                    _default.Logger = logger;
                }

                _default.AllowVersionFallback = allowVersionFallback;
                _default.Register();
                return _default;
            }
        }

        /// <summary>
        /// Subscribes the resolver to AppDomain.CurrentDomain.AssemblyResolve. Calling it more than once has no extra effect.
        /// </summary>
        public void Register()
        {
            lock (_sync)
            {
                if (_registered) return;
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                _registered = true;
            }

            if (Logger != null)
            {
                string installations = _installations.Count == 0
                    ? "none"
                    : string.Join(", ", _installations.Select(x => $"{x.Version} ({x.InstallDirectory})"));
                WriteLog($"Registered. Tekla Structures installations: {installations}");
            }
        }

        /// <summary>
        /// Unsubscribes the resolver from AppDomain.CurrentDomain.AssemblyResolve.
        /// </summary>
        public void Unregister()
        {
            lock (_sync)
            {
                if (!_registered) return;
                AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
                _registered = false;
            }

            WriteLog("Unregistered");
        }

        /// <summary>
        /// Unsubscribes the resolver (same as <see cref="Unregister"/>).
        /// </summary>
        public void Dispose()
        {
            Unregister();
        }

        /// <summary>
        /// Finds the DLL file that matches the AssemblyName (same name, compatible version, same PublicKeyToken).
        /// </summary>
        /// <param name="assemblyName">The AssemblyName to look for.</param>
        /// <returns>Full path to the matching .dll file; otherwise null.</returns>
        public string FindAssemblyPath(AssemblyName assemblyName)
        {
            if (assemblyName == null || string.IsNullOrEmpty(assemblyName.Name))
            {
                return null;
            }

            return FindAssemblyPath(assemblyName, IsTeklaVersion(assemblyName), out _, out _);
        }

        /// <summary>
        /// Loads the assembly that matches the AssemblyName. If a matching assembly is already loaded in the AppDomain, that assembly is returned.
        /// </summary>
        /// <param name="assemblyName">The AssemblyName to load.</param>
        /// <returns>The loaded assembly; null if no matching file is found.</returns>
        public Assembly LoadAssembly(AssemblyName assemblyName)
        {
            if (assemblyName == null || string.IsNullOrEmpty(assemblyName.Name))
            {
                return null;
            }

            bool isTeklaVersion = IsTeklaVersion(assemblyName);

            // Assemblies loaded with LoadFrom are not visible to the Load context, so the resolve event may fire again
            // for the same assembly. Return the already loaded one to avoid loading it twice.
            Assembly loaded = FindLoadedAssembly(assemblyName, isTeklaVersion);
            if (loaded != null)
            {
                WriteLog($"{assemblyName.Name}: already loaded from {GetLocation(loaded)}");
                if (isTeklaVersion)
                {
                    SetActiveInstallation(FindInstallationOf(loaded));
                }

                return loaded;
            }

            int generation;
            bool cachedNotFound;
            lock (_sync)
            {
                cachedNotFound = _notFound.Contains(assemblyName.FullName);
                generation = _cacheGeneration;
            }

            if (cachedNotFound)
            {
                WriteLog($"{assemblyName.Name}: not found (cached result)");
                return null;
            }

            // Search and load outside the lock: LoadFrom takes the runtime loader lock and can raise nested
            // AssemblyResolve events on other threads, which would deadlock if they had to wait for _sync.
            string path = FindAssemblyPath(assemblyName, isTeklaVersion, out TeklaInstallation installation, out bool readFailed);
            if (path == null)
            {
                // A file that could not be read (e.g. locked by an antivirus scan) is retried on the next request
                if (!readFailed)
                {
                    lock (_sync)
                    {
                        if (generation == _cacheGeneration)
                        {
                            _notFound.Add(assemblyName.FullName);
                        }
                    }
                }

                if (Logger != null)
                {
                    string searched = string.Join(", ", GetCandidateInstallations(assemblyName, isTeklaVersion).Select(x => x.Version));
                    WriteLog($"{assemblyName.Name}: not found in Tekla Structures {(searched.Length == 0 ? "(no matching installation)" : searched)}" +
                             (readFailed ? "; a file could not be read, will retry on the next request" : string.Empty));
                }

                return null;
            }

            Assembly assembly = Assembly.LoadFrom(path);
            WriteLog($"{assemblyName.Name}: loaded {path}");
            if (isTeklaVersion)
            {
                SetActiveInstallation(installation);
            }

            return assembly;
        }

        private Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            AssemblyName requested;
            try
            {
                requested = new AssemblyName(args.Name);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FileLoadException)
            {
                return null;
            }

            // Skip satellite assemblies that contain localized resources
            if (requested.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (Logger != null)
            {
                // RequestingAssembly is often null, e.g. for static references on .NET Framework
                string requestedBy = args.RequestingAssembly == null ? string.Empty : $" (requested by {args.RequestingAssembly.GetName().Name})";
                WriteLog($"Resolving {args.Name}{requestedBy}");
            }

            try
            {
                return LoadAssembly(requested);
            }
            catch (Exception ex)
            {
                // Never let an exception escape the AssemblyResolve handler, so the runtime reports a clear FileNotFoundException
                WriteLog($"{requested.Name}: failed to load - {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private string FindAssemblyPath(AssemblyName requested, bool isTeklaVersion, out TeklaInstallation installation, out bool readFailed)
        {
            installation = null;
            readFailed = false;
            string[] additionalDirectories = _additionalSearchDirectories.Snapshot();

            foreach (var candidate in GetCandidateInstallations(requested, isTeklaVersion))
            {
                string path = candidate.FindAssemblyPath(requested.Name, additionalDirectories);
                if (path == null)
                {
                    continue;
                }

                AssemblyName found = ReadAssemblyName(path, out Exception error);
                if (error != null)
                {
                    // BadImageFormatException means the file is not a .NET assembly; IO and access errors may be temporary
                    readFailed |= !(error is BadImageFormatException);
                    WriteLog($"{requested.Name}: cannot read {path} - {error.GetType().Name}: {error.Message}");
                    continue;
                }

                if (IsCompatible(requested, found, isTeklaVersion))
                {
                    installation = candidate;
                    return path;
                }

                WriteLog($"{requested.Name}: skipped {path} - file is {found.FullName}");
            }

            return null;
        }

        /// <summary>
        /// Determines the order in which installations are searched for the AssemblyName.
        /// </summary>
        private IEnumerable<TeklaInstallation> GetCandidateInstallations(AssemblyName requested, bool isTeklaVersion)
        {
            if (isTeklaVersion)
            {
                int major = requested.Version.Major;
                var sameVersion = _installations.Where(x => x.MajorVersion == major).ToList();

                return AllowVersionFallback
                    ? sameVersion.Concat(_installations.Except(sameVersion))
                    : sameVersion;
            }

            // Dependency: search only the installation in use, so DLLs of different Tekla versions are never mixed.
            // Other installations are searched only with AllowVersionFallback, or while the installation in use is unknown.
            TeklaInstallation active = GetActiveInstallation();
            if (active == null)
            {
                return _installations;
            }

            return AllowVersionFallback
                ? new[] { active }.Concat(_installations.Where(x => x != active))
                : new[] { active };
        }

        /// <summary>
        /// Returns <see cref="ActiveInstallation"/>. If it is not known yet, it is taken from a Tekla assembly that is already
        /// loaded in the AppDomain (e.g. loaded by the host application or copied next to the executable).
        /// </summary>
        private TeklaInstallation GetActiveInstallation()
        {
            TeklaInstallation active = _activeInstallation;
            if (active != null)
            {
                return active;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || !IsTeklaVersion(assembly.GetName()))
                {
                    continue;
                }

                TeklaInstallation installation = FindInstallationOf(assembly);
                if (installation != null)
                {
                    SetActiveInstallation(installation);
                    return _activeInstallation;
                }
            }

            return null;
        }

        /// <summary>
        /// Forgets earlier failed lookups, because they may succeed with the new settings. Must be called inside lock (_sync).
        /// </summary>
        private void InvalidateNotFoundCache()
        {
            _notFound.Clear();
            _cacheGeneration++;
        }

        private void SetActiveInstallation(TeklaInstallation installation)
        {
            if (installation == null || _activeInstallation != null)
            {
                return;
            }

            bool changed = false;
            lock (_sync)
            {
                if (_activeInstallation == null)
                {
                    _activeInstallation = installation;
                    changed = true;
                }
            }

            if (changed)
            {
                WriteLog($"Active installation: Tekla Structures {installation.Version} ({installation.InstallDirectory})");
            }
        }

        /// <summary>
        /// Sends a message to <see cref="Logger"/>. Never called inside lock (_sync), so a logger that loads assemblies cannot deadlock.
        /// </summary>
        private void WriteLog(string message)
        {
            Action<string> logger = Logger;

            // A logger that itself needs a missing DLL raises AssemblyResolve again on this thread; do not log recursively
            if (logger == null || _isWritingLog)
            {
                return;
            }

            _isWritingLog = true;
            try
            {
                logger("[TeklaAssemblyResolver] " + message);
            }
            catch (Exception)
            {
                // A failing logger must never break assembly resolution
            }
            finally
            {
                _isWritingLog = false;
            }
        }

        private static string GetLocation(Assembly assembly)
        {
            try
            {
                return string.IsNullOrEmpty(assembly.Location) ? "(no file)" : assembly.Location;
            }
            catch (NotSupportedException)
            {
                return "(no file)";
            }
        }

        /// <summary>
        /// Finds the installation a loaded Tekla assembly belongs to: by its file location, or by its major version when the
        /// file is not inside an installation directory.
        /// </summary>
        private TeklaInstallation FindInstallationOf(Assembly assembly)
        {
            string location;
            try
            {
                location = assembly.Location;
            }
            catch (NotSupportedException)
            {
                location = null;
            }

            if (!string.IsNullOrEmpty(location))
            {
                TeklaInstallation byLocation = _installations.FirstOrDefault(x => IsInDirectory(location, x.InstallDirectory));
                if (byLocation != null)
                {
                    return byLocation;
                }
            }

            int major = assembly.GetName().Version?.Major ?? 0;
            return _installations.FirstOrDefault(x => x.MajorVersion == major);
        }

        /// <summary>
        /// An AssemblyName carries a Tekla version if it is a Tekla assembly (name starts with "Tekla." or it is signed with
        /// the Tekla public key) and its major version is a year (2000-2099) or equals the major version of an installed
        /// Tekla version (old numbering such as 21.1). Third-party DLLs with similar version numbers (e.g. DevExpress v21.1
        /// or a library versioned 2019.x) are treated as dependencies.
        /// </summary>
        private bool IsTeklaVersion(AssemblyName assemblyName)
        {
            int major = assemblyName.Version?.Major ?? 0;
            if (major <= 0 || !IsTeklaAssembly(assemblyName))
            {
                return false;
            }

            return (major >= 2000 && major <= 2099) || _installedMajorVersions.Contains(major);
        }

        private static bool IsTeklaAssembly(AssemblyName assemblyName)
        {
            if (assemblyName.Name != null && assemblyName.Name.StartsWith("Tekla.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            byte[] token = assemblyName.GetPublicKeyToken();
            if (token == null || token.Length == 0)
            {
                return false;
            }

            string hex = BitConverter.ToString(token).Replace("-", string.Empty);
            return string.Equals(hex, TeklaPublicKeyToken, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCompatible(AssemblyName requested, AssemblyName found, bool isTeklaVersion)
        {
            if (!string.Equals(requested.Name, found.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            byte[] requestedToken = requested.GetPublicKeyToken();
            if (requestedToken != null && requestedToken.Length > 0)
            {
                byte[] foundToken = found.GetPublicKeyToken() ?? new byte[0];
                if (!requestedToken.SequenceEqual(foundToken))
                {
                    return false;
                }
            }

            if (requested.Version == null || found.Version == null)
            {
                return true;
            }

            if (isTeklaVersion)
            {
                return AllowVersionFallback || found.Version.Major == requested.Version.Major;
            }

            return found.Version >= requested.Version;
        }

        private Assembly FindLoadedAssembly(AssemblyName requested, bool isTeklaVersion)
        {
            // Cheap check on FullName ("Name, Version=...") before the more expensive GetName()
            string namePrefix = requested.Name + ",";

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || !assembly.FullName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsCompatible(requested, assembly.GetName(), isTeklaVersion))
                {
                    return assembly;
                }
            }

            return null;
        }

        private static bool IsInDirectory(string filePath, string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            try
            {
                string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return Path.GetFullPath(filePath).StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>
        /// Reads the AssemblyName from the file's metadata without loading the assembly into the AppDomain.
        /// </summary>
        /// <param name="path">Path of the DLL file.</param>
        /// <param name="error">
        /// The exception if the file could not be read: <see cref="BadImageFormatException"/> when it is not a .NET assembly,
        /// or an IO / access error, which may be temporary. Null on success.
        /// </param>
        private static AssemblyName ReadAssemblyName(string path, out Exception error)
        {
            error = null;
            try
            {
                return AssemblyName.GetAssemblyName(path);
            }
            catch (Exception ex) when (ex is BadImageFormatException || ex is IOException || ex is UnauthorizedAccessException)
            {
                error = ex;
                return null;
            }
        }

        /// <summary>
        /// List of additional search directories that keeps the resolver's cache in sync when it changes.
        /// </summary>
        private sealed class SearchDirectoryCollection : Collection<string>
        {
            private readonly TeklaAssemblyResolver _owner;

            public SearchDirectoryCollection(TeklaAssemblyResolver owner)
            {
                _owner = owner;
            }

            public string[] Snapshot()
            {
                lock (_owner._sync)
                {
                    return this.ToArray();
                }
            }

            protected override void InsertItem(int index, string item)
            {
                ThrowIfEmpty(item);
                lock (_owner._sync)
                {
                    base.InsertItem(index, item);
                    _owner.InvalidateNotFoundCache();
                }
            }

            protected override void SetItem(int index, string item)
            {
                ThrowIfEmpty(item);
                lock (_owner._sync)
                {
                    base.SetItem(index, item);
                    _owner.InvalidateNotFoundCache();
                }
            }

            protected override void RemoveItem(int index)
            {
                lock (_owner._sync)
                {
                    base.RemoveItem(index);
                    _owner.InvalidateNotFoundCache();
                }
            }

            protected override void ClearItems()
            {
                lock (_owner._sync)
                {
                    base.ClearItems();
                    _owner.InvalidateNotFoundCache();
                }
            }

            private static void ThrowIfEmpty(string item)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    throw new ArgumentException("The directory must not be null or empty.", nameof(item));
                }
            }
        }
    }
}
