using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

/// <summary>A Unity bundle load failure that a network retry cannot repair.</summary>
public sealed class TapBundleLoadException : InvalidOperationException
{
    public TapBundleLoadException(string message, Exception inner = null) : base(message, inner) { }
}
/// <summary>TapTap user-file storage, isolated from SaveData and Screenshots.</summary>
public static class TapPersistentBundleCache
{
    public const string RelativeDirectory = "ResourceCache";

    public static bool IsAvailable
    {
        get
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return VNovelizer_TapBundleCacheAvailable() == 1;
#else
            return false;
#endif
        }
    }

    public static bool IsMiniGame
    {
        get
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return VNovelizer_TapBundleIsMiniGame() == 1;
#else
            return false;
#endif
        }
    }

    public static bool AreDependenciesCached(string label, out int bundleCount, out int cachedCount)
    {
        bundleCount = 0;
        cachedCount = 0;
#if UNITY_WEBGL && !UNITY_EDITOR
        if (!IsAvailable) return false;
        var bundles = new List<IResourceLocation>();
        var visited = new HashSet<IResourceLocation>();
        bool found = false;
        foreach (var locator in Addressables.ResourceLocators)
        {
            if (!locator.Locate(label, null, out var locations)) continue;
            found = true;
            foreach (var location in locations) CollectRemoteDownloads(location, visited, bundles);
        }
        bundleCount = bundles.Count;
        foreach (var location in bundles)
        {
            var options = (AssetBundleRequestOptions)location.Data;
            if (IsCached(CreateKey(location.InternalId, options), options.BundleSize)) cachedCount++;
        }
        return found && bundleCount > 0 && cachedCount == bundleCount;
#else
        return false;
#endif
    }

    // The catalog content hash/CRC/size distinguish versions. Query tokens and CDN
    // failover hosts must not change the cache identity or appear on disk.
    public static string CreateKey(string internalId, AssetBundleRequestOptions options)
    {
        if (options == null || !Uri.TryCreate(internalId, UriKind.Absolute, out Uri uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("A remote bundle URL and catalog options are required.");
        string identity = uri.AbsolutePath + "\n" + options.Hash + "\n"
            + options.Crc.ToString(CultureInfo.InvariantCulture) + "\n"
            + options.BundleSize.ToString(CultureInfo.InvariantCulture);
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity)))
                .Replace("-", "").ToLowerInvariant();
    }

    public static bool IsCached(string key, long expectedSize)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return VNovelizer_TapBundleIsCached(key, expectedSize) == 1;
#else
        return false;
#endif
    }

    public static long SpaceOccupied
    {
        get
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return (long)VNovelizer_TapBundleCacheSize();
#else
            return -1;
#endif
        }
    }

    public static bool Clear()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return VNovelizer_TapBundleClearCache() == 1;
#else
        return false;
#endif
    }

    public static AsyncOperationHandle PrefetchDependencies(string label)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        if (IsAvailable)
        {
            var bundles = new List<IResourceLocation>();
            var visited = new HashSet<IResourceLocation>();
            bool found = false;
            foreach (var locator in Addressables.ResourceLocators)
            {
                if (!locator.Locate(label, null, out var locations)) continue;
                found = true;
                foreach (var location in locations) CollectRemoteDownloads(location, visited, bundles);
            }
            if (!found || bundles.Count == 0) throw new TapBundleLoadException("No remote bundles found for required preload label: " + label);
            return Addressables.ResourceManager.ProvideResources<IAssetBundleResource>(bundles, true);
        }
#endif
        return Addressables.DownloadDependenciesAsync(label, false);
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    private static void CollectRemoteDownloads(IResourceLocation location, HashSet<IResourceLocation> visited, List<IResourceLocation> bundles)
    {
        if (!visited.Add(location)) return;
        if (location.ResourceType == typeof(IAssetBundleResource) && location.ProviderId == TapPersistentAssetBundleProvider.ID)
            bundles.Add(new TapDownloadOnlyLocation(location));
        if (location.HasDependencies)
            foreach (var dependency in location.Dependencies) CollectRemoteDownloads(dependency, visited, bundles);
    }
#endif
    public static void Install()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        if (!IsAvailable) return;
        Debug.Log("[TapResourceCache] Installing persistent bundle provider.");
        var providers = Addressables.ResourceManager.ResourceProviders;
        bool registered = false;
        foreach (var provider in providers)
            if (provider.ProviderId == TapPersistentAssetBundleProvider.ID) registered = true;
        if (!registered) providers.Add(new TapPersistentAssetBundleProvider());

        // Use a dedicated provider ID, so an earlier default-provider lookup cannot
        // bypass this cache. Preserve catalog metadata for future catalog updates.
        var locators = new List<IResourceLocator>(Addressables.ResourceLocators);
        foreach (var locator in locators)
        {
            if (locator is TapPersistentResourceLocator) continue;
            ResourceLocatorInfo info = Addressables.GetLocatorInfo(locator);
            var wrapped = new TapPersistentResourceLocator(locator);
            Addressables.RemoveResourceLocator(locator);
            Addressables.AddResourceLocator(wrapped, info?.LocalHash, info?.CatalogLocation);
        }
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    internal static bool HasDownloadedFile(string key, long expectedSize)
    {
        int size = VNovelizer_TapBundleFileSize(key);
        return size > 0 && (expectedSize <= 0 || size == expectedSize);
    }

    internal static byte[] Read(string key)
    {
        int size = VNovelizer_TapBundleFileSize(key);
        if (size <= 0) throw new InvalidOperationException("Resource storage file is missing or empty.");
        byte[] bytes = new byte[size];
        if (VNovelizer_TapBundleRead(key, bytes, size) != size)
            throw new InvalidOperationException("Resource storage read was incomplete.");
        return bytes;
    }

    internal static int BeginDownload(string url, string key, int timeoutMs) =>
        VNovelizer_TapBundleBeginDownload(url, key, timeoutMs);
    internal static int Poll(int token) => VNovelizer_TapBundleDownloadState(token);
    internal static long DownloadedBytes(int token) => (long)VNovelizer_TapBundleDownloadedBytes(token);
    internal static void EndDownload(int token) => VNovelizer_TapBundleEndDownload(token);
    internal static bool Commit(string key, long size) => VNovelizer_TapBundleCommit(key, size) == 1;
    internal static void Invalidate(string key) => VNovelizer_TapBundleInvalidate(key);
    internal static string DownloadError(int token)
    {
        IntPtr ptr = VNovelizer_TapBundleDownloadError(token);
        if (ptr == IntPtr.Zero) return "Resource download failed.";
        try
        {
            int length = 0;
            while (Marshal.ReadByte(ptr, length) != 0) length++;
            byte[] bytes = new byte[length];
            Marshal.Copy(ptr, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { VNovelizer_TapBundleFree(ptr); }
    }

    [DllImport("__Internal")] private static extern int VNovelizer_TapBundleCacheAvailable();
    [DllImport("__Internal")] private static extern int VNovelizer_TapBundleIsMiniGame();
    [DllImport("__Internal")] private static extern int VNovelizer_TapBundleIsCached(string key, double expectedSize);
    [DllImport("__Internal")] private static extern double VNovelizer_TapBundleCacheSize();
    [DllImport("__Internal")] private static extern int VNovelizer_TapBundleClearCache();
    [DllImport("__Internal")] private static extern int VNovelizer_TapBundleFileSize(string key);
    [DllImport("__Internal")] private static extern int VNovelizer_TapBundleRead(string key, byte[] bytes, int length);
    [DllImport("__Internal")] private static extern int VNovelizer_TapBundleBeginDownload(string url, string key, int timeoutMs);
    [DllImport("__Internal")] private static extern int VNovelizer_TapBundleDownloadState(int token);
    [DllImport("__Internal")] private static extern double VNovelizer_TapBundleDownloadedBytes(int token);
    [DllImport("__Internal")] private static extern IntPtr VNovelizer_TapBundleDownloadError(int token);
    [DllImport("__Internal")] private static extern void VNovelizer_TapBundleEndDownload(int token);
    [DllImport("__Internal")] private static extern int VNovelizer_TapBundleCommit(string key, double size);
    [DllImport("__Internal")] private static extern void VNovelizer_TapBundleInvalidate(string key);
    [DllImport("__Internal")] private static extern void VNovelizer_TapBundleFree(IntPtr ptr);
#endif
}

#if UNITY_WEBGL && !UNITY_EDITOR
internal sealed class TapPersistentResourceLocator : IResourceLocator
{
    private readonly IResourceLocator original;
    private readonly Dictionary<IResourceLocation, IResourceLocation> wrapped = new Dictionary<IResourceLocation, IResourceLocation>();
    public TapPersistentResourceLocator(IResourceLocator original) { this.original = original; }
    public string LocatorId => original.LocatorId;
    public IEnumerable<object> Keys => original.Keys;
#if ENABLE_BINARY_CATALOG
    public IEnumerable<IResourceLocation> AllLocations
    {
        get { foreach (var location in original.AllLocations) yield return Wrap(location); }
    }
#endif
    public bool Locate(object key, Type type, out IList<IResourceLocation> locations)
    {
        if (!original.Locate(key, type, out var originals)) { locations = null; return false; }
        locations = new List<IResourceLocation>(originals.Count);
        foreach (var location in originals) locations.Add(Wrap(location));
        return true;
    }

    private IResourceLocation Wrap(IResourceLocation location)
    {
        if (wrapped.TryGetValue(location, out var cached)) return cached;
        bool remoteBundle = location.ResourceType == typeof(IAssetBundleResource)
            && location.Data is AssetBundleRequestOptions
            && Uri.TryCreate(location.InternalId, UriKind.Absolute, out Uri uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && !IsBuiltInBundle((AssetBundleRequestOptions)location.Data);
        if (!remoteBundle && !location.HasDependencies) { wrapped.Add(location, location); return location; }
        var result = new TapCacheLocation(location.PrimaryKey, location.InternalId,
            remoteBundle ? TapPersistentAssetBundleProvider.ID : location.ProviderId, location.ResourceType);
        wrapped.Add(location, result);
        result.Data = remoteBundle
            ? new TapBundleRequestOptions((AssetBundleRequestOptions)location.Data)
            : location.Data;
        bool dependenciesChanged = false;
        if (location.HasDependencies)
            foreach (var dependency in location.Dependencies)
            {
                var wrappedDependency = Wrap(dependency);
                dependenciesChanged |= !ReferenceEquals(dependency, wrappedDependency);
                result.Dependencies.Add(wrappedDependency);
            }
        // Preserve default provider operation identity for built-in bundles and UI.
        if (!remoteBundle && !dependenciesChanged) { wrapped[location] = location; return location; }
        result.ComputeDependencyHash();
        return result;
    }

    private static bool IsBuiltInBundle(AssetBundleRequestOptions options)
    {
        // TapTap also serves ordinary StreamingAssets bundles over HTTP. A RuntimePath
        // prefix cannot distinguish them from Unity's generated shader/script bundles.
        string name = options.BundleName ?? string.Empty;
        return name.EndsWith("_unitybuiltinshaders", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_monoscripts", StringComparison.OrdinalIgnoreCase);
    }
    private sealed class TapCacheLocation : ResourceLocationBase
    {
        public TapCacheLocation(string key, string id, string provider, Type type) : base(key, id, provider, type) { }
    }
}

internal sealed class TapBundleRequestOptions : AssetBundleRequestOptions
{
    public TapBundleRequestOptions(AssetBundleRequestOptions source)
    {
        Hash = source.Hash; Crc = source.Crc; BundleName = source.BundleName;
        BundleSize = source.BundleSize; Timeout = source.Timeout;
        RetryCount = source.RetryCount; RedirectLimit = source.RedirectLimit;
        ChunkedTransfer = source.ChunkedTransfer; AssetLoadMode = source.AssetLoadMode;
        UseCrcForCachedBundle = source.UseCrcForCachedBundle;
        UseUnityWebRequestForLocalBundles = source.UseUnityWebRequestForLocalBundles;
        ClearOtherCachedVersionsWhenLoaded = source.ClearOtherCachedVersionsWhenLoaded;
    }
    public override long ComputeSize(IResourceLocation location, ResourceManager resourceManager)
    {
        string key = TapPersistentBundleCache.CreateKey(location.InternalId, this);
        return TapPersistentBundleCache.IsCached(key, BundleSize) ? 0 : BundleSize;
    }
}

// Pre-download opens only the remote bundle file. Built-in shader/UI dependencies
// remain under their original provider and are loaded normally when an asset needs them.
internal sealed class TapDownloadOnlyLocation : ResourceLocationBase
{
    public TapDownloadOnlyLocation(IResourceLocation source)
        : base(source.PrimaryKey, source.InternalId, source.ProviderId, source.ResourceType)
    {
        Data = source.Data;
    }
}
internal sealed class TapPersistentAssetBundleProvider : AssetBundleProvider
{
    public const string ID = "VNovelizer.TapPersistentAssetBundleProvider";
    private static bool bundleReadInProgress;
    private static readonly Dictionary<string, BundleOperation> inFlight = new Dictionary<string, BundleOperation>();
    private static readonly Dictionary<string, SharedBundle> loaded = new Dictionary<string, SharedBundle>();
    public override string ProviderId => ID;

    public override void Provide(ProvideHandle handle)
    {
        try { MonoManager.GetInstance().StartCoroutine(new BundleOperation(handle).RunSafely()); }
        catch (Exception e) { handle.Complete<IAssetBundleResource>(null, false, e); }
    }

    public override void Release(IResourceLocation location, object asset)
    {
        if (asset is BundleResource resource) resource.Release();
        else base.Release(location, asset);
    }

    private sealed class SharedBundle
    {
        public readonly string Key;
        public readonly AssetBundle Bundle;
        public int References;
        public SharedBundle(string key, AssetBundle bundle) { Key = key; Bundle = bundle; }
    }

    private sealed class BundleResource : IAssetBundleResource
    {
        private SharedBundle shared;
        public BundleResource(SharedBundle shared) { this.shared = shared; if (shared != null) shared.References++; }
        public AssetBundle GetAssetBundle() => shared?.Bundle;
        public void Release()
        {
            var entry = shared;
            shared = null;
            if (entry == null || --entry.References > 0) return;
            if (loaded.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry)) loaded.Remove(entry.Key);
            entry.Bundle.Unload(true);
        }
    }

    // A null load can mean a conflict, unsupported data or memory exhaustion.
    // Delete a download only when Unity explicitly reports a CRC mismatch.
    private sealed class LoadDiagnostics : IDisposable
    {
        public string Message { get; private set; }
        public bool IntegrityFailure { get; private set; }
        public bool Conflict { get; private set; }
        public LoadDiagnostics() { Application.logMessageReceived += Observe; }
        private void Observe(string message, string stack, LogType type)
        {
            if (type == LogType.Log) return;
            string lower = (message ?? "").ToLowerInvariant();
            if (lower.Contains("assetbundle") || lower.Contains("crc"))
            {
                Message = message;
                Conflict |= lower.Contains("already loaded") || lower.Contains("same files");
                IntegrityFailure |= lower.Contains("crc") && (lower.Contains("mismatch") || lower.Contains("failed")
                    || lower.Contains("error") || lower.Contains("does not match"));
            }
        }
        public void Dispose() { Application.logMessageReceived -= Observe; }
    }

    private sealed class BundleOperation
    {
        private readonly ProvideHandle handle;
        private readonly AssetBundleRequestOptions options;
        private readonly string key;
        private readonly string identity;
        private readonly bool downloadOnly;
        private long downloadedBytes;
        private long totalBytes;
        private bool done;
        private int downloadToken;
        private AssetBundle bundle;
        private bool holdsReadSlot;
        private bool ownsFlight;
        private bool transferredBundle;
        private Exception failure;
        private BundleOperation waitingOn;
        private LoadDiagnostics diagnostics;

        public BundleOperation(ProvideHandle handle)
        {
            this.handle = handle;
            downloadOnly = handle.Location is TapDownloadOnlyLocation || handle.Location.GetType().Name == "DownloadOnlyLocation";
            options = (AssetBundleRequestOptions)handle.Location.Data;
            key = TapPersistentBundleCache.CreateKey(handle.Location.InternalId, options);
            // Catalog aliases can point at the same native bundle using different URLs.
            identity = string.IsNullOrEmpty(options.BundleName) ? key : options.BundleName + "\n" + options.Hash
                + "\n" + options.Crc.ToString(CultureInfo.InvariantCulture) + "\n" + options.BundleSize.ToString(CultureInfo.InvariantCulture);
            totalBytes = TapPersistentBundleCache.HasDownloadedFile(key, options.BundleSize) ? 0 : options.BundleSize;
            handle.SetProgressCallback(() => done ? 1f : waitingOn != null ? waitingOn.Progress()
                : totalBytes > 0 ? Mathf.Min(0.95f, (float)downloadedBytes / totalBytes) : 0f);
            handle.SetDownloadProgressCallbacks(() => waitingOn != null && !done ? waitingOn.Status()
                : new DownloadStatus { TotalBytes = totalBytes, DownloadedBytes = downloadedBytes, IsDone = done });
            handle.SetWaitForCompletionCallback(() => { throw new TapBundleLoadException("TapTap bundle loading requires asynchronous loading."); });
        }

        private float Progress() => done ? 1f : totalBytes > 0 ? Mathf.Min(0.95f, (float)downloadedBytes / totalBytes) : 0f;

        private DownloadStatus Status() => new DownloadStatus { TotalBytes = totalBytes, DownloadedBytes = downloadedBytes, IsDone = done };

        public IEnumerator RunSafely()
        {
            IEnumerator flow = Run();
            while (true)
            {
                Exception error = null;
                bool running = false;
                try { running = flow.MoveNext(); }
                catch (Exception e) { error = e; }
                if (error != null)
                {
                    failure = error;
                    (flow as IDisposable)?.Dispose();
                    if (!transferredBundle) bundle?.Unload(true);
                    done = true;
                    handle.Complete<IAssetBundleResource>(null, false, error);
                    yield break;
                }
                if (!running) yield break;
                yield return flow.Current;
            }
        }

        private IEnumerator Run()
        {
            try
            {
                while (inFlight.TryGetValue(identity, out var pending))
                {
                    waitingOn = pending;
                    while (!pending.done) yield return null;
                    if (pending.failure != null) throw pending.failure;
                    waitingOn = null;
                }
                if (loaded.TryGetValue(identity, out var shared))
                {
                    totalBytes = downloadedBytes = 0;
                    Complete(downloadOnly ? null : shared);
                    yield break;
                }
                if (downloadOnly && TapPersistentBundleCache.IsCached(key, options.BundleSize))
                {
                    Complete(null);
                    yield break;
                }
                inFlight.Add(identity, this);
                ownsFlight = true;

                bool verified = TapPersistentBundleCache.IsCached(key, options.BundleSize);
                if (verified || TapPersistentBundleCache.HasDownloadedFile(key, options.BundleSize))
                {
                    while (bundleReadInProgress) yield return null;
                    bundleReadInProgress = holdsReadSlot = true;
                    byte[] cachedBytes = null;
                    try { cachedBytes = TapPersistentBundleCache.Read(key); }
                    catch (Exception e) { Debug.LogWarning("[TapResourceCache] Cached bundle read failed: " + e.Message); }
                    if (cachedBytes != null)
                    {
                        AssetBundleCreateRequest request = null;
                        Exception loadFailure = null;
                        try { request = BeginLoad(cachedBytes); }
                        catch (Exception e) { loadFailure = InterpretLoadFailure(e); }
                        cachedBytes = null;
                        if (request != null)
                        {
                            yield return request;
                            bundle = request.assetBundle;
                            if (bundle == null) loadFailure = InterpretLoadFailure(null);
                        }
                        if (request == null && loadFailure == null) loadFailure = InterpretLoadFailure(null);
                        StopDiagnostics();
                        if (loadFailure is TapBundleLoadException) throw loadFailure;
                        if (bundle != null)
                        {
                            if (!verified && !TapPersistentBundleCache.Commit(key, options.BundleSize))
                                throw new InvalidOperationException("Resource storage commit failed; check storage quota or disk space.");
                            ReleaseReadSlot();
                            Debug.Log("[TapResourceCache] Cache hit: " + key);
                            CompleteLoadedBundle();
                            yield break;
                        }
                    }
                    ReleaseReadSlot();
                    // This branch is reached only for an unreadable file or explicit CRC failure.
                    TapPersistentBundleCache.Invalidate(key);
                    totalBytes = options.BundleSize;
                }

                string url = handle.ResourceManager.TransformInternalId(handle.Location);
                Debug.Log("[TapResourceCache] Download: " + DiagnosticUrl(url) + "; key=" + key);
                downloadToken = TapPersistentBundleCache.BeginDownload(url, key,
                    options.Timeout > 0 ? options.Timeout * 1000 : 180000);
                if (downloadToken <= 0) throw new InvalidOperationException("Resource storage download could not start.");
                float deadline = Time.realtimeSinceStartup + 180f;
                while (TapPersistentBundleCache.Poll(downloadToken) == 0 && Time.realtimeSinceStartup < deadline)
                {
                    downloadedBytes = Math.Min(totalBytes, TapPersistentBundleCache.DownloadedBytes(downloadToken));
                    yield return null;
                }
                if (TapPersistentBundleCache.Poll(downloadToken) == 0) throw new TimeoutException("Resource download timed out.");
                if (TapPersistentBundleCache.Poll(downloadToken) != 1)
                    throw new InvalidOperationException(TapPersistentBundleCache.DownloadError(downloadToken) + "; resource=" + DiagnosticUrl(url));
                while (bundleReadInProgress) yield return null;
                bundleReadInProgress = holdsReadSlot = true;
                byte[] bytes = TapPersistentBundleCache.Read(key);
                if (options.BundleSize > 0 && bytes.LongLength != options.BundleSize)
                {
                    long actualSize = bytes.LongLength;
                    TapPersistentBundleCache.Invalidate(key);
                    throw new InvalidOperationException("Resource bundle size integrity mismatch; expected=" + options.BundleSize
                        + ", actual=" + actualSize + ", resource=" + DiagnosticUrl(url));
                }
                AssetBundleCreateRequest load = null;
                Exception downloadLoadFailure = null;
                try { load = BeginLoad(bytes); }
                catch (Exception e) { downloadLoadFailure = InterpretLoadFailure(e); }
                bytes = null;
                if (load != null)
                {
                    yield return load;
                    bundle = load.assetBundle;
                    if (bundle == null) downloadLoadFailure = InterpretLoadFailure(null);
                }
                if (load == null && downloadLoadFailure == null) downloadLoadFailure = InterpretLoadFailure(null);
                StopDiagnostics();
                if (downloadLoadFailure != null)
                {
                    if (!(downloadLoadFailure is TapBundleLoadException)) TapPersistentBundleCache.Invalidate(key);
                    throw downloadLoadFailure;
                }
                if (!TapPersistentBundleCache.Commit(key, options.BundleSize))
                    throw new InvalidOperationException("Resource storage commit failed; check storage quota or disk space.");
                ReleaseReadSlot();
                downloadedBytes = totalBytes;
                Debug.Log("[TapResourceCache] Downloaded and persisted: " + key);
                CompleteLoadedBundle();
            }
            finally
            {
                StopDiagnostics();
                ReleaseReadSlot();
                if (downloadToken > 0) TapPersistentBundleCache.EndDownload(downloadToken);
                if (ownsFlight) { inFlight.Remove(identity); ownsFlight = false; }
            }
        }

        private AssetBundleCreateRequest BeginLoad(byte[] bytes)
        {
            foreach (var existing in AssetBundle.GetAllLoadedAssetBundles())
                if (!string.IsNullOrEmpty(options.BundleName) && existing.name == options.BundleName)
                    throw new TapBundleLoadException("Resource bundle is already loaded by another provider; bundle=" + options.BundleName + "; key=" + key);
            diagnostics = new LoadDiagnostics();
            return AssetBundle.LoadFromMemoryAsync(bytes, options.Crc);
        }

        private Exception InterpretLoadFailure(Exception error)
        {
            if (error is TapBundleLoadException) return error;
            string reason = error?.Message ?? diagnostics?.Message ?? "Unity returned an empty AssetBundle";
            string lower = reason.ToLowerInvariant();
            if (diagnostics?.Conflict != true && (diagnostics?.IntegrityFailure == true
                || (lower.Contains("crc") && (lower.Contains("error") || lower.Contains("failed") || lower.Contains("mismatch")))))
                return new InvalidOperationException("Resource bundle CRC integrity validation failed; key=" + key + "; " + reason, error);
            return new TapBundleLoadException("Resource bundle load failed; bundle=" + options.BundleName + "; key=" + key + "; " + reason, error);
        }

        private void StopDiagnostics() { diagnostics?.Dispose(); diagnostics = null; }
        private void ReleaseReadSlot() { if (holdsReadSlot) { holdsReadSlot = false; bundleReadInProgress = false; } }
        private static string DiagnosticUrl(string url) => url.Split('?')[0].Split('#')[0];

        private void CompleteLoadedBundle()
        {
            if (downloadOnly)
            {
                bundle.Unload(true);
                bundle = null;
                Complete(null);
            }
            else
            {
                var shared = new SharedBundle(identity, bundle);
                loaded.Add(identity, shared);
                transferredBundle = true;
                Complete(shared);
            }
        }

        private void Complete(SharedBundle shared)
        {
            // Release the operation lock before completing: completion callbacks may load the same bundle.
            if (ownsFlight) { inFlight.Remove(identity); ownsFlight = false; }
            done = true;
            handle.Complete<IAssetBundleResource>(new BundleResource(shared), true, null);
        }
    }
}
#endif
