﻿using Prise.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Prise
{
    public class DefaultAssemblyLoadContext<T> : InMemoryAssemblyLoadContext, IAssemblyLoadContext
    {
        protected IHostFrameworkProvider hostFrameworkProvider;
        protected IHostTypesProvider hostTypesProvider;
        protected IRemoteTypesProvider<T> remoteTypesProvider;
        protected IDependencyPathProvider<T> dependencyPathProvider;
        protected IProbingPathsProvider<T> probingPathsProvider;
        protected IRuntimePlatformContext runtimePlatformContext;
        protected IDepsFileProvider<T> depsFileProvider;
        protected IPluginDependencyResolver<T> pluginDependencyResolver;
        protected IPluginDependencyContext pluginDependencyContext;
        protected INativeAssemblyUnloader nativeAssemblyUnloader;
        protected IAssemblyLoadStrategyProvider assemblyLoadStrategyProvider;

        internal IAssemblyLoadStrategy assemblyLoadStrategy;
        internal IAssemblyLoadOptions<T> options;
        protected ConcurrentDictionary<string, IntPtr> loadedNativeLibraries;
        protected bool disposed = false;
        protected bool disposing = false;

        public DefaultAssemblyLoadContext(
            IAssemblyLoadOptions<T> options,
            IHostFrameworkProvider hostFrameworkProvider,
            IHostTypesProvider hostTypesProvider,
            IRemoteTypesProvider<T> remoteTypesProvider,
            IDependencyPathProvider<T> dependencyPathProvider,
            IProbingPathsProvider<T> probingPathsProvider,
            IRuntimePlatformContext runtimePlatformContext,
            IDepsFileProvider<T> depsFileProvider,
            IPluginDependencyResolver<T> pluginDependencyResolver,
            INativeAssemblyUnloader nativeAssemblyUnloader,
            IAssemblyLoadStrategyProvider assemblyLoadStrategyProvider)
        {
            this.options = options;
            this.hostFrameworkProvider = hostFrameworkProvider;
            this.hostTypesProvider = hostTypesProvider;
            this.remoteTypesProvider = remoteTypesProvider;
            this.dependencyPathProvider = dependencyPathProvider;
            this.probingPathsProvider = probingPathsProvider;
            this.runtimePlatformContext = runtimePlatformContext;
            this.depsFileProvider = depsFileProvider;
            this.pluginDependencyResolver = pluginDependencyResolver;
            this.nativeAssemblyUnloader = nativeAssemblyUnloader;
            this.assemblyLoadStrategyProvider = assemblyLoadStrategyProvider;
            this.loadedNativeLibraries = new ConcurrentDictionary<string, IntPtr>();
        }

        public virtual Assembly LoadPluginAssembly(IPluginLoadContext pluginLoadContext)
        {
            if (this.pluginDependencyContext != null)
                throw new PrisePluginException($"Plugin {pluginLoadContext.PluginAssemblyName} was already loaded");

            this.pluginDependencyContext = PluginDependencyContext.FromPluginAssembly<T>(
                pluginLoadContext,
                this.hostFrameworkProvider,
                this.hostTypesProvider.ProvideHostTypes(),
                this.remoteTypesProvider.ProvideRemoteTypes(),
                this.runtimePlatformContext,
                this.depsFileProvider,
                this.options.IgnorePlatformInconsistencies);

            using (var pluginStream = LoadFileFromLocalDisk(pluginLoadContext.PluginAssemblyPath, pluginLoadContext.PluginAssemblyName))
            {
                this.assemblyLoadStrategy = this.assemblyLoadStrategyProvider.ProvideAssemblyLoadStrategy(pluginLoadContext, this.pluginDependencyContext);

                return base.LoadFromStream(pluginStream); // ==> AssemblyLoadContext.LoadFromStream(Stream stream);
            }
        }

        public virtual async Task<Assembly> LoadPluginAssemblyAsync(IPluginLoadContext pluginLoadContext)
        {
            if (this.pluginDependencyContext != null)
                throw new PrisePluginException($"Plugin {pluginLoadContext.PluginAssemblyName} was already loaded");

            this.pluginDependencyContext = await PluginDependencyContext.FromPluginAssemblyAsync(
                pluginLoadContext,
                this.hostFrameworkProvider,
                this.hostTypesProvider.ProvideHostTypes(),
                this.remoteTypesProvider.ProvideRemoteTypes(),
                this.runtimePlatformContext,
                this.depsFileProvider,
                this.options.IgnorePlatformInconsistencies);

            using (var pluginStream = await LoadFileFromLocalDiskAsync(pluginLoadContext.PluginAssemblyPath, pluginLoadContext.PluginAssemblyName))
            {
                this.assemblyLoadStrategy = this.assemblyLoadStrategyProvider.ProvideAssemblyLoadStrategy(pluginLoadContext, this.pluginDependencyContext);

                return base.LoadFromStream(pluginStream); // ==> AssemblyLoadContext.LoadFromStream(Stream stream);
            }
        }

        protected virtual ValueOrProceed<Assembly> LoadFromDefaultContext(IPluginLoadContext pluginLoadContext, AssemblyName assemblyName)
        {
            try
            {
                var assembly = Default.LoadFromAssemblyName(assemblyName);
                if (assembly != null)
                    return ValueOrProceed<Assembly>.FromValue(assembly, false);
            }
            catch (FileNotFoundException) { }

            return ValueOrProceed<Assembly>.Proceed();
        }

        protected virtual ValueOrProceed<Assembly> LoadFromRemote(IPluginLoadContext pluginLoadContext, AssemblyName assemblyName)
        {
            var assemblyFileName = $"{assemblyName.Name}.dll";
            if (File.Exists(Path.Combine(pluginLoadContext.PluginAssemblyPath, assemblyFileName)))
            {
                return LoadDependencyFromLocalDisk(pluginLoadContext, assemblyFileName);
            }
            return ValueOrProceed<Assembly>.Proceed();
        }

        protected virtual bool IsResourceAssembly(AssemblyName assemblyName)
        {
            return !string.IsNullOrEmpty(assemblyName.CultureName) && !string.Equals("neutral", assemblyName.CultureName);
        }

        protected virtual ValueOrProceed<Assembly> LoadFromDependencyContext(IPluginLoadContext pluginLoadContext, AssemblyName assemblyName)
        {
            if (IsResourceAssembly(assemblyName))
            {
                foreach (var resourceDependency in this.pluginDependencyContext.PluginResourceDependencies)
                {
                    var resourcePath = Path.Combine(resourceDependency.Path, assemblyName.CultureName, assemblyName.Name + ".dll");
                    if (File.Exists(resourcePath))
                    {
                        return ValueOrProceed<Assembly>.FromValue(LoadFromAssemblyPath(resourcePath), false);
                    }
                }

                // Do not proceed probing
                return ValueOrProceed<Assembly>.FromValue(null, false);
            }

            var pluginDependency = this.pluginDependencyContext.PluginDependencies.FirstOrDefault(d => d.DependencyNameWithoutExtension == assemblyName.Name);
            if (pluginDependency != null)
            {
                var dependencyPath = this.dependencyPathProvider.GetDependencyPath();
                if (String.IsNullOrEmpty(dependencyPath))
                    dependencyPath = pluginLoadContext.PluginAssemblyPath;
                var probingPaths = this.probingPathsProvider.GetProbingPaths();
                var dependency = this.pluginDependencyResolver.ResolvePluginDependencyToPath(dependencyPath, probingPaths, pluginDependency);
                if (dependency != null)
                    return ValueOrProceed<Assembly>.FromValue(LoadFromStream(dependency), false);
            }

            var localFile = Path.Combine(this.dependencyPathProvider.GetDependencyPath(), assemblyName.Name + ".dll");
            if (File.Exists(localFile))
            {
                return ValueOrProceed<Assembly>.FromValue(LoadFromAssemblyPath(localFile), false);
            }

            return ValueOrProceed<Assembly>.Proceed();
        }

        protected virtual ValueOrProceed<string> LoadUnmanagedFromDependencyContext(IPluginLoadContext pluginLoadContext, string unmanagedDllName)
        {
            var unmanagedDllNameWithoutFileExtension = Path.GetFileNameWithoutExtension(unmanagedDllName);
            var platformDependency = this.pluginDependencyContext.PlatformDependencies.FirstOrDefault(d => d.DependencyNameWithoutExtension == unmanagedDllNameWithoutFileExtension);
            if (platformDependency != null)
            {
                var dependencyPath = this.dependencyPathProvider.GetDependencyPath();

                if (String.IsNullOrEmpty(dependencyPath))
                    dependencyPath = pluginLoadContext.PluginAssemblyPath;

                var probingPaths = this.probingPathsProvider.GetProbingPaths();
                var pathToDependency = this.pluginDependencyResolver.ResolvePlatformDependencyToPath(dependencyPath, probingPaths, platformDependency);
                if (!String.IsNullOrEmpty(pathToDependency))
                {
                    string runtimeCandidate = null;
                    if (this.options.NativeDependencyLoadPreference == NativeDependencyLoadPreference.PreferInstalledRuntime)
                        // Prefer loading from runtime folder
                        runtimeCandidate = this.pluginDependencyResolver.ResolvePlatformDependencyPathToRuntime(this.options.PluginPlatformVersion, pathToDependency);

                    return ValueOrProceed<string>.FromValue(runtimeCandidate ?? pathToDependency, false);
                }
            }

            return ValueOrProceed<string>.FromValue(String.Empty, true);
        }

        protected virtual ValueOrProceed<string> LoadUnmanagedFromRemote(IPluginLoadContext pluginLoadContext, string unmanagedDllName)
        {
            var assemblyFileName = $"{unmanagedDllName}.dll";
            var pathToDependency = Path.Combine(pluginLoadContext.PluginAssemblyPath, assemblyFileName);
            if (File.Exists(pathToDependency))
            {
                return ValueOrProceed<string>.FromValue(pathToDependency, false);
            }
            return ValueOrProceed<string>.FromValue(String.Empty, true);
        }

        protected virtual ValueOrProceed<IntPtr> LoadUnmanagedFromDefault(IPluginLoadContext pluginLoadContext, string unmanagedDllName)
        {
            var resolution = base.LoadUnmanagedDll(unmanagedDllName);
            if (resolution == default(IntPtr))
                return ValueOrProceed<IntPtr>.Proceed();

            return ValueOrProceed<IntPtr>.FromValue(resolution, false);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            // This fixes the issue where the ALC is still alive and utilized in the host
            if (this.disposed || this.disposing)
                return null;

            return assemblyLoadStrategy.LoadAssembly(
                    assemblyName,
                    LoadFromDependencyContext,
                    LoadFromRemote,
                    LoadFromDefaultContext
                );
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            // This fixes the issue where the ALC is still alive and utilized in the host
            if (this.disposed || this.disposing)
                return IntPtr.Zero; 

            IntPtr library = IntPtr.Zero;

            var nativeAssembly = assemblyLoadStrategy.LoadUnmanagedDll(
                    unmanagedDllName,
                    LoadUnmanagedFromDependencyContext,
                    LoadUnmanagedFromRemote,
                    LoadUnmanagedFromDefault
                );

            if (!String.IsNullOrEmpty(nativeAssembly.Path))
                // Load via assembly path
                library = LoadUnmanagedDllFromDependencyLookup(Path.GetFullPath(nativeAssembly.Path));
            else
                // Load via provided pointer
                library = nativeAssembly.Pointer;

            if (library != IntPtr.Zero && // If the library was found
                !String.IsNullOrEmpty(nativeAssembly.Path) && // and it was found via the dependency lookup
                !this.loadedNativeLibraries.ContainsKey(nativeAssembly.Path)) // and it was not already loaded
                this.loadedNativeLibraries[nativeAssembly.Path] = library; // Add it to the list in order to have it unloaded later

            return library;
        }

        /// <summary>
        /// Load the assembly using the base.LoadUnmanagedDllFromPath functionality 
        /// </summary>
        /// <param name="fullPathToNativeAssembly"></param>
        /// <returns>A loaded native library pointer</returns>
        protected virtual IntPtr LoadUnmanagedDllFromDependencyLookup(string fullPathToNativeAssembly) => base.LoadUnmanagedDllFromPath(fullPathToNativeAssembly);

        protected virtual ValueOrProceed<Assembly> LoadDependencyFromLocalDisk(IPluginLoadContext pluginLoadContext, string assemblyFileName)
        {
            var dependency = LoadFileFromLocalDisk(pluginLoadContext.PluginAssemblyPath, assemblyFileName);

            if (dependency == null)
                return ValueOrProceed<Assembly>.Proceed();

            return ValueOrProceed<Assembly>.FromValue(Assembly.Load(ToByteArray(dependency)), false);
        }

        internal static Stream LoadFileFromLocalDisk(string loadPath, string pluginAssemblyName)
        {
            var probingPath = EnsureFileExists(loadPath, pluginAssemblyName);
            var memoryStream = new MemoryStream();
            using (var stream = new FileStream(probingPath, FileMode.Open, FileAccess.Read))
            {
                memoryStream.SetLength(stream.Length);
                stream.Read(memoryStream.GetBuffer(), 0, (int)stream.Length);
            }
            return memoryStream;
        }

        internal static async Task<Stream> LoadFileFromLocalDiskAsync(string loadPath, string pluginAssemblyName)
        {
            var probingPath = EnsureFileExists(loadPath, pluginAssemblyName);
            var memoryStream = new MemoryStream();
            using (var stream = new FileStream(probingPath, FileMode.Open, FileAccess.Read))
            {
                memoryStream.SetLength(stream.Length);
                await stream.ReadAsync(memoryStream.GetBuffer(), 0, (int)stream.Length);
            }
            return memoryStream;
        }

        private static string EnsureFileExists(string loadPath, string pluginAssemblyName)
        {
            var probingPath = Path.GetFullPath(Path.Combine(loadPath, pluginAssemblyName));
            if (!File.Exists(probingPath))
                throw new PrisePluginException($"Plugin assembly does not exist in path : {probingPath}");
            return probingPath;
        }

        internal static byte[] ToByteArray(Stream stream)
        {
            stream.Position = 0;
            byte[] buffer = new byte[stream.Length];
            for (int totalBytesCopied = 0; totalBytesCopied < stream.Length;)
                totalBytesCopied += stream.Read(buffer, totalBytesCopied, Convert.ToInt32(stream.Length) - totalBytesCopied);
            return buffer;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed && disposing)
            {
                this.disposing = true;

                GC.Collect();
                GC.WaitForPendingFinalizers();

                this.hostTypesProvider = null;
                this.remoteTypesProvider = null;
                this.dependencyPathProvider = null;
                this.probingPathsProvider = null;
                this.runtimePlatformContext = null;
                this.depsFileProvider = null;
                this.pluginDependencyResolver = null;
                this.pluginDependencyContext = null;
                this.assemblyLoadStrategy = null;

                // Unload any loaded native assemblies
                foreach (var nativeAssembly in this.loadedNativeLibraries)
                    this.nativeAssemblyUnloader.UnloadNativeAssembly(nativeAssembly.Key, nativeAssembly.Value);

                this.loadedNativeLibraries = null;
                this.nativeAssemblyUnloader = null;
            }
            this.disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

#if NETCOREAPP2_1
        public void Unload()
        {
            // What to do for unloading in NETCOREAPP2_1?
            // ==> Nothing, this is only available in .NET Core 3.0+
        }
#endif
    }
}
