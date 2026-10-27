using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.Networking;

public enum RemoteContentLocationState
{
    Found,
    NotFound,
    Failed
}

public enum RemoteContentErrorKind
{
    None,
    Offline,
    Timeout,
    ServerUnavailable,
    ResourceMissing,
    IntegrityFailure,
    StorageFailure,
    Unknown,
    ResourceLoadFailure
}

public enum RemoteContentOperationStage
{
    None,
    Initialize,
    CatalogCheck,
    CatalogUpdate,
    LocationLookup,
    SizeQuery,
    Download,
    RequestCleanup
}

public struct RemoteContentOperationResult
{
    public RemoteContentOperationStage Stage;
    public RemoteContentErrorKind ErrorKind;
    public string Label;
    public string Error;
    public bool CanRetry;
    public int Attempt;

    public bool Succeeded => ErrorKind == RemoteContentErrorKind.None;
}

public class RemoteContentPreloadManager : BaseManager<RemoteContentPreloadManager>
{
    private const string LoadingTaskID = "remote_content_preload";
    private const string LoadingTaskName = "下载必要资源";
    private const string RequiredResourceLabel = "res_required";
    private const float LoadingTaskWeight = 1f;
    private const float CatalogOperationTimeoutSeconds = 20f;
    private const float PendingOperationDrainTimeoutSeconds = 20f;
    private const float RemoteOperationTimeoutSeconds = 180f;
    private const float NetworkRecoveryPollSeconds = 5f;
    private const float RetryBaseDelaySeconds = 2f;
    private const int BundleRequestTimeoutSeconds = 30;
    private const int MaxRequiredDownloadAttempts = 3;

    // WebGL download-only operations do not keep bundles available to asset loads.
    // Retain bundle containers for the session, without decoding every texture/audio asset.
#if UNITY_WEBGL && !UNITY_EDITOR
    private const bool KeepPreloadedBundles = true;
#else
    private const bool KeepPreloadedBundles = false;
#endif
    // TapTap can reopen persistent bundles on demand; only plain WebGL retains them.
    private bool ShouldKeepPreloadedBundles => KeepPreloadedBundles && !TapPersistentBundleCache.IsAvailable;

    private readonly Dictionary<string, AsyncOperationHandle> preloadedBundles = new Dictionary<string, AsyncOperationHandle>();

    private readonly HashSet<string> completedLabels = new HashSet<string>();
    private readonly HashSet<string> failedLabels = new HashSet<string>();
    private bool catalogsReady;
    private bool hasStarted;
    private bool isReady;
    private bool isPreloading;
    private bool hasFailed;
    private float progress;
    private long totalDownloadBytes;
    private long downloadedBytes;
    private string lastError;
    private bool lastPreloadSucceeded = true;
    private bool lastSizeQueryFailed;
    private bool currentPreloadIsRequired;
    private int pendingTimedOutOperations;
    private RemoteContentOperationStage currentStage;
    private string primaryCdnRoot;
    private string backupCdnRoot;
    private bool useBackupCdn;
    private RemoteContentOperationResult lastOperationResult;

    // Resolve assets only after startup has finished replacing catalogs/providers.
    // Open before downloads so Addressable loading UI remains usable.
    public bool CatalogsReady => catalogsReady;
    public IEnumerator WaitForCatalogs()
    {
        if (!hasStarted) StartMainMenuPreload();
        while (!catalogsReady && !hasFailed) yield return null;
    }

    public bool HasStarted => hasStarted;
    public bool IsReady => isReady;
    public bool IsPreloading => isPreloading;
    public bool HasFailed => hasFailed;
    public float Progress => progress;
    public long TotalDownloadBytes => totalDownloadBytes;
    public long DownloadedBytes => downloadedBytes;
    public string LastError => lastError;
    public RemoteContentOperationResult LastOperationResult => lastOperationResult;
    public RemoteContentErrorKind LastErrorKind => lastOperationResult.ErrorKind;
    public int LastAttemptCount => lastOperationResult.Attempt;
    public NetworkReachability CurrentNetwork => Application.internetReachability;
    public long CacheSpaceOccupied
    {
        get
        {
            if (TapPersistentBundleCache.IsAvailable) return TapPersistentBundleCache.SpaceOccupied;
#if ENABLE_CACHING
            return Caching.ready ? Caching.currentCacheForWriting.spaceOccupied : -1;
#else
            // Some build targets do not expose Unity's native bundle cache API.
            return -1;
#endif
        }
    }

    public RemoteContentPreloadManager()
    {
        ConfigureAddressablesWebRequests();
    }

    public void StartMainMenuPreload()
    {
        if (isPreloading)
        {
            return;
        }

        hasStarted = true;

        if (isReady)
        {
            return;
        }

        MonoManager.GetInstance().StartCoroutine(RunPreloadSafely(PreloadRequiredContent()));
    }

    public IEnumerator WaitUntilReady()
    {
        if (!isReady && !isPreloading)
        {
            StartMainMenuPreload();
        }

        float nextDiagnosticAt = Time.realtimeSinceStartup + 10f;
        while (!isReady && !hasFailed)
        {
            if (Time.realtimeSinceStartup >= nextDiagnosticAt)
            {
                nextDiagnosticAt = Time.realtimeSinceStartup + 10f;
                Debug.Log($"[RemoteContentPreload] Waiting: stage={currentStage} pending={pendingTimedOutOperations} bytes={downloadedBytes}/{totalDownloadBytes}");
            }
            yield return null;
        }
    }

    public void RetryRequiredContent()
    {
        if (isPreloading)
        {
            return;
        }

        failedLabels.Clear();
        hasFailed = false;
        lastError = null;
        isReady = false;
        hasStarted = true;
        MonoManager.GetInstance().StartCoroutine(RunPreloadSafely(PreloadRequiredContent()));
    }

    // Unity stops a coroutine when a nested iterator throws. Always release the menu wait.
    private IEnumerator RunPreloadSafely(IEnumerator routine)
    {
        var stack = new Stack<IEnumerator>();
        stack.Push(routine);
        while (stack.Count > 0)
        {
            IEnumerator current = stack.Peek();
            object yielded = null;
            bool moved = false;
            System.Exception failure = null;
            try
            {
                moved = current.MoveNext();
                if (moved) yielded = current.Current;
            }
            catch (System.Exception e)
            {
                failure = e;
            }

            if (failure != null)
            {
                Debug.LogError($"[RemoteContentPreload] Startup exception at {currentStage}: {failure}");
                if (!isReady)
                {
                    RecordFailure(currentStage, RemoteContentErrorKind.ResourceLoadFailure, null, failure.ToString(), false, 1);
                    FinishRequiredContentFailure();
                }
                yield break;
            }
            if (!moved)
            {
                stack.Pop();
                continue;
            }
            if (yielded is IEnumerator nested)
                stack.Push(nested);
            else
                yield return yielded;
        }
    }

    private void FinishRequiredContentFailure()
    {
        isPreloading = false;
        isReady = false;
        hasFailed = true;
        if (string.IsNullOrEmpty(lastError))
            lastError = BuildUserFacingError(lastOperationResult);
        RegisterOrUpdateLoadingTask(progress, lastError);
        Debug.LogWarning($"[RemoteContentPreload] Required content failed at {lastOperationResult.Stage}. Failed labels: {string.Join(", ", failedLabels)}.");
    }

    private IEnumerator PreloadRequiredContent()
    {
        isPreloading = true;
        currentPreloadIsRequired = true;
        isReady = false;
        hasFailed = false;
        lastError = null;
        lastOperationResult = default;
        progress = 0f;
        downloadedBytes = 0;
        yield return WaitForPendingTimedOutOperations(true);
        if (pendingTimedOutOperations > 0)
        {
            FinishRequiredContentFailure();
            yield break;
        }
        catalogsReady = false;
        bool initialized = false;
        lastPreloadSucceeded = false;
        yield return InitializeAndUpdateCatalogs(true, succeeded => initialized = succeeded);
        // A timed-out update can still replace locators. Drain it before asset lookups.
        yield return WaitForPendingTimedOutOperations(true);
        if (pendingTimedOutOperations > 0) initialized = false;
        if (initialized)
        {
            TapPersistentBundleCache.Install();
            catalogsReady = true;
            yield return PreloadLabels(
                BuildRequiredPreloadLabels(),
                true,
                "准备下载游戏与画廊资源",
                "游戏与画廊资源已缓存",
                "游戏与画廊资源下载完成");
        }

        if (lastPreloadSucceeded && TapPersistentBundleCache.IsAvailable
            && !TapPersistentBundleCache.AreDependenciesCached(RequiredResourceLabel, out int requiredBundles, out int cachedBundles))
        {
            lastPreloadSucceeded = false;
            RecordFailure(RemoteContentOperationStage.Download, RemoteContentErrorKind.StorageFailure,
                RequiredResourceLabel, $"Required cache incomplete before menu: {cachedBundles}/{requiredBundles}", false, 1);
        }

        if (!lastPreloadSucceeded)
        {
            FinishRequiredContentFailure();
            yield break;
        }

        isPreloading = false;
        isReady = true;
        Debug.Log($"[RemoteContentPreload] Required content ready. Downloaded {FormatBytes(downloadedBytes)} / {FormatBytes(totalDownloadBytes)}.");
        yield return PreloadCommonUIPanels();
    }

    public bool ConfigureCdnFailover(string primaryRoot, string backupRoot)
    {
        primaryRoot = NormalizeCdnRoot(primaryRoot);
        backupRoot = NormalizeCdnRoot(backupRoot);
        if (string.IsNullOrEmpty(primaryRoot) || string.IsNullOrEmpty(backupRoot)
            || !string.Equals(ExtractContentVersion(primaryRoot), ExtractContentVersion(backupRoot), System.StringComparison.Ordinal))
        {
            Debug.LogError("[RemoteContentPreload] CDN failover roots must be valid and use the same content version.");
            return false;
        }

        primaryCdnRoot = primaryRoot;
        backupCdnRoot = backupRoot;
        useBackupCdn = false;
        Addressables.InternalIdTransformFunc = location => TransformCdnInternalId(location.InternalId);
        return true;
    }

    public void ClearRemoteContentCache(System.Action<bool> completedCallback = null)
    {
        if (isPreloading || pendingTimedOutOperations > 0)
        {
            completedCallback?.Invoke(false);
            return;
        }

        MonoManager.GetInstance().StartCoroutine(ClearRemoteContentCacheCoroutine(completedCallback));
    }

    private IEnumerator ClearRemoteContentCacheCoroutine(System.Action<bool> completedCallback)
    {
        ReleasePreloadedBundles();
        if (TapPersistentBundleCache.IsAvailable)
        {
            ResourcesManager.GetInstance().ReleaseCachedAssets();
            bool cleared = TapPersistentBundleCache.Clear();
            completedLabels.Clear();
            failedLabels.Clear();
            isReady = false;
            completedCallback?.Invoke(cleared);
            yield break;
        }
        completedLabels.Clear();
        isReady = false;
        bool succeeded = true;
        string[] labels = { RequiredResourceLabel };
        foreach (string label in labels)
        {
            AsyncOperationHandle<bool> clearHandle = Addressables.ClearDependencyCacheAsync(label, false);
            yield return WaitForAddressablesOperation(clearHandle, CatalogOperationTimeoutSeconds);

            if (!clearHandle.IsDone)
            {
                succeeded = false;
                DrainTimedOutOperation(clearHandle);
                continue;
            }

            succeeded &= clearHandle.Status == AsyncOperationStatus.Succeeded && clearHandle.Result;
            ReleaseIfValid(clearHandle);
        }

        if (succeeded)
        {
            completedLabels.Clear();
            failedLabels.Clear();
            isReady = false;
        }

        completedCallback?.Invoke(succeeded);
    }

    private IEnumerator PreloadCommonUIPanels()
    {
        VNProjectConfig config = VNProjectConfig.Instance;
        if (config == null)
        {
            yield break;
        }

        UIManager uiManager = UIManager.GetInstance();
        yield return new WaitForSecondsRealtime(1f);
        uiManager.PreloadPanelPrefab("SettingsPanel", config.UI_SettingsPath);
        yield return new WaitForSecondsRealtime(0.5f);

        uiManager.PreloadPanelPrefab("GalleryPanel", config.UI_GalleryPath);
    }

    private List<string> BuildRequiredPreloadLabels()
    {
        List<string> labels = new List<string>();
        AddLabel(labels, RequiredResourceLabel);
        return labels;
    }

    private void AddLabel(List<string> labels, string label)
    {
        if (string.IsNullOrWhiteSpace(label) || labels.Contains(label))
        {
            return;
        }

        labels.Add(label);
    }

    private IEnumerator InitializeAndUpdateCatalogs(bool showProgressTask, System.Action<bool> initializedCallback)
    {
        if (showProgressTask)
        {
            RegisterOrUpdateLoadingTask(0f, "检查资源目录");
        }

        currentStage = RemoteContentOperationStage.Initialize;
        initializedCallback(false);
        AsyncOperationHandle initializeHandle;
        try
        {
            Debug.Log($"[RemoteContentPreload] Initialize: streamingAssetsPath='{Application.streamingAssetsPath}', runtimePath='{Addressables.RuntimePath}', timeout={CatalogOperationTimeoutSeconds}s");
            initializeHandle = Addressables.InitializeAsync(false);
        }
        catch (System.Exception e)
        {
            RecordFailure(RemoteContentOperationStage.Initialize, ClassifyFailure(e, false),
                null, e.Message, true, 1);
            yield break;
        }
        yield return WaitForAddressablesOperation(initializeHandle, CatalogOperationTimeoutSeconds);

        if (!initializeHandle.IsDone)
        {
            RecordFailure(RemoteContentOperationStage.Initialize, RemoteContentErrorKind.Timeout, null, "Addressables 初始化超时", true, 1);
            Debug.LogWarning("[RemoteContentPreload] Addressables initialization timed out. Skipping catalog update.");
            DrainTimedOutOperation(initializeHandle);
            yield break;
        }

        if (initializeHandle.Status != AsyncOperationStatus.Succeeded)
        {
            RecordFailure(RemoteContentOperationStage.Initialize, ClassifyFailure(initializeHandle.OperationException, false), null, initializeHandle.OperationException?.Message, true, 1);
            Debug.LogWarning($"[RemoteContentPreload] Failed to initialize Addressables: {initializeHandle.OperationException?.Message}");
            ReleaseIfValid(initializeHandle);
            yield break;
        }

        ReleaseIfValid(initializeHandle);
        TapPersistentBundleCache.Install();

        initializedCallback(true);
        currentStage = RemoteContentOperationStage.CatalogCheck;
        if (showProgressTask) RegisterOrUpdateLoadingTask(0f, "检查资源更新");
        AsyncOperationHandle<List<string>> checkHandle = Addressables.CheckForCatalogUpdates(false);
        yield return WaitForAddressablesOperation(checkHandle, CatalogOperationTimeoutSeconds);

        if (!checkHandle.IsDone)
        {
            RecordFailure(RemoteContentOperationStage.CatalogCheck, RemoteContentErrorKind.Timeout, null, "Catalog 检查超时", true, 1);
            Debug.LogWarning("[RemoteContentPreload] Catalog update check timed out. Continuing with the currently loaded catalog.");
            DrainTimedOutOperation(checkHandle);
            yield break;
        }

        if (checkHandle.Status != AsyncOperationStatus.Succeeded)
        {
            RecordFailure(RemoteContentOperationStage.CatalogCheck, ClassifyFailure(checkHandle.OperationException, false), null, checkHandle.OperationException?.Message, true, 1);
            Debug.LogWarning($"[RemoteContentPreload] Failed to check catalog updates: {checkHandle.OperationException?.Message}");
            ReleaseIfValid(checkHandle);
            yield break;
        }

        List<string> catalogsToUpdate = checkHandle.Result;
        if (catalogsToUpdate == null || catalogsToUpdate.Count == 0)
        {
            Addressables.Release(checkHandle);
            yield break;
        }

        if (showProgressTask)
        {
            RegisterOrUpdateLoadingTask(0f, "更新资源目录");
        }

        currentStage = RemoteContentOperationStage.CatalogUpdate;
        AsyncOperationHandle<List<IResourceLocator>> updateHandle = Addressables.UpdateCatalogs(catalogsToUpdate, false);
        yield return WaitForAddressablesOperation(updateHandle, CatalogOperationTimeoutSeconds);

        if (!updateHandle.IsDone)
        {
            RecordFailure(RemoteContentOperationStage.CatalogUpdate, RemoteContentErrorKind.Timeout, null, "Catalog 更新超时", true, 1);
            Debug.LogWarning("[RemoteContentPreload] Catalog update timed out. Continuing with the currently loaded catalog.");
            DrainTimedOutOperation(updateHandle);
            ReleaseIfValid(checkHandle);
            yield break;
        }

        if (updateHandle.Status == AsyncOperationStatus.Succeeded)
        {
            completedLabels.Clear();
            failedLabels.Clear();
            ReleasePreloadedBundles();
            ResourcesManager.GetInstance().ReleaseCachedAssets();
            TapPersistentBundleCache.Install();
            Debug.Log($"[RemoteContentPreload] Updated {catalogsToUpdate.Count} remote catalog(s).");
        }
        else
        {
            RecordFailure(RemoteContentOperationStage.CatalogUpdate, ClassifyFailure(updateHandle.OperationException, false), null, updateHandle.OperationException?.Message, true, 1);
            Debug.LogWarning($"[RemoteContentPreload] Failed to update catalogs: {updateHandle.OperationException?.Message}");
        }

        ReleaseIfValid(updateHandle);
        ReleaseIfValid(checkHandle);
    }

    private IEnumerator WaitForAddressablesOperation(AsyncOperationHandle handle, float timeoutSeconds)
    {
        float startedAt = Time.realtimeSinceStartup;
        while (!handle.IsDone && Time.realtimeSinceStartup - startedAt < timeoutSeconds)
        {
            yield return null;
        }
    }

    private IEnumerator PreloadLabels(List<string> labels, bool showProgressTask, string preparingTaskName, string cachedTaskName, string completedTaskName)
    {
        lastPreloadSucceeded = false;
        yield return PreloadLabels(labels, showProgressTask, preparingTaskName, cachedTaskName, completedTaskName, succeeded => lastPreloadSucceeded = succeeded);
    }

    private IEnumerator PreloadLabels(List<string> labels, bool showProgressTask, string preparingTaskName, string cachedTaskName, string completedTaskName, System.Action<bool> completedCallback)
    {
        if (labels == null || labels.Count == 0)
        {
            completedCallback?.Invoke(true);
            yield break;
        }

        progress = 0f;
        downloadedBytes = 0;

        if (showProgressTask && !string.IsNullOrEmpty(preparingTaskName))
        {
            RegisterOrUpdateLoadingTask(0f, preparingTaskName);
        }

        yield return CalculateTotalDownloadSize(labels);

        if (lastSizeQueryFailed)
        {
            if (showProgressTask)
            {
                RegisterOrUpdateLoadingTask(progress, "资源下载失败，请检查网络后重试");
            }
            completedCallback?.Invoke(false);
            yield break;
        }

        if (totalDownloadBytes <= 0 && !ShouldKeepPreloadedBundles)
        {
            progress = 1f;
            if (showProgressTask && !string.IsNullOrEmpty(cachedTaskName))
            {
                CompleteLoadingTask(cachedTaskName);
            }
            Debug.Log("[RemoteContentPreload] No remote content needs downloading.");
            completedCallback?.Invoke(true);
            yield break;
        }

        if (showProgressTask)
        {
            RegisterOrUpdateLoadingTask(0f, $"{LoadingTaskName} 0% ({FormatBytes(0)} / {FormatBytes(totalDownloadBytes)})");
        }

        long completedBytesBeforeLabel = 0;
        bool allSucceeded = true;

        foreach (string label in labels)
        {
            if (completedLabels.Contains(label))
            {
                continue;
            }

            long labelSize = 0;
            yield return GetDownloadSize(label, currentPreloadIsRequired, size => labelSize = size);

            if (lastSizeQueryFailed)
            {
                allSucceeded = false;
                break;
            }

            if (labelSize <= 0 && !ShouldKeepPreloadedBundles)
            {
                completedLabels.Add(label);
                continue;
            }

            bool labelSucceeded = false;
            yield return DownloadLabel(label, labelSize, completedBytesBeforeLabel, showProgressTask, succeeded => labelSucceeded = succeeded);
            if (!labelSucceeded)
            {
                allSucceeded = false;
                if (showProgressTask)
                {
                    RegisterOrUpdateLoadingTask(progress, $"资源下载失败，请检查网络后重试: {label}");
                }
                break;
            }

            completedBytesBeforeLabel += labelSize;
            downloadedBytes = System.Math.Min(totalDownloadBytes, completedBytesBeforeLabel);
            progress = totalDownloadBytes > 0 ? (float)downloadedBytes / totalDownloadBytes : 1f;

            if (showProgressTask)
            {
                RegisterOrUpdateLoadingTask(progress, BuildProgressTaskName(label));
            }
        }

        if (allSucceeded)
        {
            progress = 1f;
            if (showProgressTask && !string.IsNullOrEmpty(completedTaskName))
            {
                CompleteLoadingTask(completedTaskName);
            }
        }

        completedCallback?.Invoke(allSucceeded);
    }

    private IEnumerator CalculateTotalDownloadSize(List<string> labels)
    {
        totalDownloadBytes = 0;
        lastSizeQueryFailed = false;

        foreach (string label in labels)
        {
            if (completedLabels.Contains(label))
            {
                continue;
            }

            long size = 0;
            yield return GetDownloadSize(label, currentPreloadIsRequired, result => size = result);
            if (lastSizeQueryFailed)
            {
                yield break;
            }
            totalDownloadBytes += size;
        }

        Debug.Log($"[RemoteContentPreload] Preload size: {FormatBytes(totalDownloadBytes)}.");
    }

    private bool ValidateLocationState(string label, bool required, RemoteContentLocationState state)
    {
        if (state == RemoteContentLocationState.Found)
        {
            return true;
        }

        if (state == RemoteContentLocationState.Failed || required)
        {
            lastSizeQueryFailed = true;
            failedLabels.Add(label);
            if (state == RemoteContentLocationState.NotFound)
            {
                RecordFailure(RemoteContentOperationStage.LocationLookup, RemoteContentErrorKind.ResourceMissing,
                    label, $"必需资源标签不存在或为空: {label}", false, 1);
            }
        }
        else
        {
            Debug.LogWarning($"[RemoteContentPreload] Optional label is absent or empty; skipping: {label}");
        }

        return false;
    }

    private IEnumerator GetDownloadSize(string label, bool required, System.Action<long> callback)
    {
        RemoteContentLocationState locationState = RemoteContentLocationState.Failed;
        yield return HasLocations(label, result => locationState = result);

        if (!ValidateLocationState(label, required, locationState))
        {
            callback?.Invoke(0);
            yield break;
        }

        currentStage = RemoteContentOperationStage.SizeQuery;
        AsyncOperationHandle<long> sizeHandle;
        try
        {
            sizeHandle = Addressables.GetDownloadSizeAsync(label);
        }
        catch (System.Exception e)
        {
            failedLabels.Add(label);
            lastSizeQueryFailed = true;
            RecordFailure(RemoteContentOperationStage.SizeQuery, ClassifyFailure(e, false), label, e.Message, true, 1);
            callback?.Invoke(0);
            yield break;
        }
        yield return WaitForAddressablesOperation(sizeHandle, CatalogOperationTimeoutSeconds);

        if (!sizeHandle.IsDone)
        {
            Debug.LogWarning($"[RemoteContentPreload] Download size query timed out: {label}");
            failedLabels.Add(label);
            lastSizeQueryFailed = true;
            RecordFailure(RemoteContentOperationStage.SizeQuery, RemoteContentErrorKind.Timeout, label, "资源大小查询超时", true, 1);
            DrainTimedOutOperation(sizeHandle);
            callback?.Invoke(0);
            yield break;
        }

        if (sizeHandle.Status == AsyncOperationStatus.Succeeded)
        {
            callback?.Invoke(sizeHandle.Result);
        }
        else
        {
            Debug.LogWarning($"[RemoteContentPreload] Failed to query download size: {label}");
            failedLabels.Add(label);
            lastSizeQueryFailed = true;
            RecordFailure(RemoteContentOperationStage.SizeQuery, ClassifyFailure(sizeHandle.OperationException, false), label, sizeHandle.OperationException?.Message, true, 1);
            callback?.Invoke(0);
        }

        ReleaseIfValid(sizeHandle);
    }

    private IEnumerator HasLocations(string label, System.Action<RemoteContentLocationState> callback)
    {
        currentStage = RemoteContentOperationStage.LocationLookup;
        AsyncOperationHandle<IList<IResourceLocation>> locationHandle;
        try
        {
            locationHandle = Addressables.LoadResourceLocationsAsync(label);
        }
        catch (System.Exception e)
        {
            RecordFailure(RemoteContentOperationStage.LocationLookup, ClassifyFailure(e, false), label, e.Message, true, 1);
            callback?.Invoke(RemoteContentLocationState.Failed);
            yield break;
        }
        yield return WaitForAddressablesOperation(locationHandle, CatalogOperationTimeoutSeconds);

        if (!locationHandle.IsDone)
        {
            Debug.LogWarning($"[RemoteContentPreload] Location lookup timed out: {label}");
            RecordFailure(RemoteContentOperationStage.LocationLookup, RemoteContentErrorKind.Timeout, label, "资源位置查询超时", true, 1);
            DrainTimedOutOperation(locationHandle);
            callback?.Invoke(RemoteContentLocationState.Failed);
            yield break;
        }

        if (locationHandle.Status != AsyncOperationStatus.Succeeded)
        {
            RecordFailure(RemoteContentOperationStage.LocationLookup, ClassifyFailure(locationHandle.OperationException, false), label, locationHandle.OperationException?.Message, true, 1);
            ReleaseIfValid(locationHandle);
            callback?.Invoke(RemoteContentLocationState.Failed);
            yield break;
        }

        bool hasLocations = locationHandle.Result != null && locationHandle.Result.Count > 0;
        ReleaseIfValid(locationHandle);
        callback?.Invoke(hasLocations ? RemoteContentLocationState.Found : RemoteContentLocationState.NotFound);
    }

    private IEnumerator DownloadLabel(string label, long labelSize, long completedBytesBeforeLabel, bool showProgressTask, System.Action<bool> completedCallback)
    {
        int maxAttempts = MaxRequiredDownloadAttempts;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Cached TapTap bundles still need to be opened, but need no network.
            if (labelSize > 0 && !IsNetworkAvailable())
            {
                RecordFailure(RemoteContentOperationStage.Download, RemoteContentErrorKind.Offline, label, "设备当前离线", true, attempt);
                if (attempt >= maxAttempts)
                {
                    completedCallback?.Invoke(false);
                    yield break;
                }

                yield return WaitForNetworkRecovery(currentPreloadIsRequired ? 30f : 0f);
                if (!IsNetworkAvailable())
                {
                    continue;
                }
            }

            bool attemptSucceeded = false;
            bool attemptCanRetry = true;
            yield return DownloadLabelAttempt(
                label,
                labelSize,
                completedBytesBeforeLabel,
                showProgressTask,
                attempt,
                (succeeded, canRetry) =>
                {
                    attemptSucceeded = succeeded;
                    attemptCanRetry = canRetry;
                });

            if (attemptSucceeded)
            {
                completedCallback?.Invoke(true);
                yield break;
            }

            if (!attemptCanRetry || attempt >= maxAttempts)
            {
                completedCallback?.Invoke(false);
                yield break;
            }

            TrySwitchToBackupCdn(lastOperationResult.ErrorKind);
            yield return WaitForPendingTimedOutOperations(showProgressTask);
            if (pendingTimedOutOperations > 0)
            {
                completedCallback?.Invoke(false);
                yield break;
            }
            yield return new WaitForSecondsRealtime(AddRetryJitter(RetryBaseDelaySeconds * Mathf.Pow(2f, attempt - 1)));
        }

        completedCallback?.Invoke(false);
    }

    private IEnumerator DownloadLabelAttempt(
        string label,
        long labelSize,
        long completedBytesBeforeLabel,
        bool showProgressTask,
        int attempt,
        System.Action<bool, bool> completedCallback)
    {
        currentStage = RemoteContentOperationStage.Download;
        Debug.Log($"[RemoteContentPreload] Downloading {label}: {FormatBytes(labelSize)}, attempt {attempt}.");

        AsyncOperationHandle downloadHandle;
        try
        {
            downloadHandle = BeginBundlePreload(label);
        }
        catch (System.Exception e)
        {
            RemoteContentErrorKind errorKind = ClassifyFailure(e, false);
            RecordFailure(RemoteContentOperationStage.Download, errorKind, label, e.Message, IsRetryable(errorKind), attempt);
            completedCallback?.Invoke(false, IsRetryable(errorKind));
            yield break;
        }
        float startedAt = Time.realtimeSinceStartup;
        float nextProgressUpdateAt = startedAt;

        while (!downloadHandle.IsDone && Time.realtimeSinceStartup - startedAt < RemoteOperationTimeoutSeconds)
        {
            var downloadStatus = downloadHandle.GetDownloadStatus();
            long currentLabelBytes = downloadStatus.TotalBytes > 0
                ? downloadStatus.DownloadedBytes
                : (long)(labelSize * downloadHandle.PercentComplete);
            currentLabelBytes = System.Math.Min(labelSize, currentLabelBytes);
            downloadedBytes = completedBytesBeforeLabel + currentLabelBytes;
            progress = totalDownloadBytes > 0 ? (float)downloadedBytes / totalDownloadBytes : 0f;
            if (showProgressTask && Time.realtimeSinceStartup >= nextProgressUpdateAt)
            {
                nextProgressUpdateAt = Time.realtimeSinceStartup + 0.1f;
                RegisterOrUpdateLoadingTask(progress, BuildProgressTaskName(label));
            }
            yield return null;
        }

        if (!downloadHandle.IsDone)
        {
            failedLabels.Add(label);
            Debug.LogWarning($"[RemoteContentPreload] Download timed out: {label}");
            RecordFailure(RemoteContentOperationStage.Download, RemoteContentErrorKind.Timeout, label, "资源下载超时", true, attempt);
            DrainTimedOutOperation(downloadHandle);
            completedCallback?.Invoke(false, true);
            yield break;
        }

        if (downloadHandle.Status == AsyncOperationStatus.Succeeded && TapPersistentBundleCache.IsAvailable)
        {
            bool cached = TapPersistentBundleCache.AreDependenciesCached(label, out int bundleCount, out int cachedCount);
            Debug.Log($"[TapResourceCache] Required cache verified: {cachedCount}/{bundleCount} bundles, label={label}.");
            if (!cached)
            {
                RecordFailure(RemoteContentOperationStage.Download, RemoteContentErrorKind.StorageFailure, label,
                    $"Download operation ended without a complete verified cache: {cachedCount}/{bundleCount}", false, attempt);
                ReleaseIfValid(downloadHandle);
                completedCallback?.Invoke(false, false);
                yield break;
            }
        }

        if (downloadHandle.Status == AsyncOperationStatus.Succeeded)
        {
            if (ShouldKeepPreloadedBundles)
            {
                preloadedBundles[label] = downloadHandle;
            }
            completedLabels.Add(label);
            failedLabels.Remove(label);
            Debug.Log($"[RemoteContentPreload] Downloaded {label}.");
            RecordSuccess(RemoteContentOperationStage.Download, label, attempt);
            completedCallback?.Invoke(true, false);
        }
        else
        {
            failedLabels.Add(label);
            Debug.LogWarning($"[RemoteContentPreload] Failed to download {label}: {downloadHandle.OperationException?.Message}");
            RemoteContentErrorKind errorKind = ClassifyFailure(downloadHandle.OperationException, false);
            bool canRetry = IsRetryable(errorKind)
                || (errorKind == RemoteContentErrorKind.ResourceMissing && !string.IsNullOrEmpty(backupCdnRoot));
            RecordFailure(RemoteContentOperationStage.Download, errorKind, label, downloadHandle.OperationException?.Message, canRetry, attempt);
            completedCallback?.Invoke(false, canRetry);
        }

        if (!ShouldKeepPreloadedBundles || downloadHandle.Status != AsyncOperationStatus.Succeeded)
        {
            ReleaseIfValid(downloadHandle);
        }
    }

    private AsyncOperationHandle BeginBundlePreload(string label)
    {
        if (TapPersistentBundleCache.IsMiniGame && !TapPersistentBundleCache.IsAvailable)
            throw new System.InvalidOperationException("TapTap resource storage API is unavailable; cannot verify download completion.");
        if (!ShouldKeepPreloadedBundles)
        {
            return TapPersistentBundleCache.PrefetchDependencies(label);
        }

        var visited = new HashSet<IResourceLocation>();
        var bundles = new List<IResourceLocation>();
        bool found = false;
        foreach (var locator in Addressables.ResourceLocators)
        {
            if (!locator.Locate(label, typeof(object), out var locations)) continue;
            found = true;
            foreach (var location in locations) CollectBundleLocations(location, visited, bundles);
        }
        if (!found) throw new System.InvalidOperationException($"Missing preload label: {label}");
        // Use original locations, not DownloadOnlyLocation wrappers, so normal asset
        // loads share these bundle operations and their retained reference counts.
        return Addressables.ResourceManager.ProvideResources<IAssetBundleResource>(bundles, true);
    }

    private static void CollectBundleLocations(IResourceLocation location,
        HashSet<IResourceLocation> visited, List<IResourceLocation> bundles)
    {
        if (!visited.Add(location)) return;
        if (location.ResourceType == typeof(IAssetBundleResource)) bundles.Add(location);
        if (!location.HasDependencies) return;
        foreach (var dependency in location.Dependencies)
            CollectBundleLocations(dependency, visited, bundles);
    }

    private void ReleasePreloadedBundles()
    {
        foreach (var handle in preloadedBundles.Values) ReleaseIfValid(handle);
        preloadedBundles.Clear();
    }

    private void ConfigureAddressablesWebRequests()
    {
        Addressables.ResourceManager.WebRequestOverride = request =>
        {
            // Exclude query parameters so signed URL credentials are not written to logs.
            string diagnosticUrl = request.url.Split('?')[0].Split('#')[0];
            Debug.Log($"[RemoteContentPreload] WebRequest: {request.method} {diagnosticUrl}");
            if (request.timeout <= 0)
            {
                request.timeout = BundleRequestTimeoutSeconds;
            }

            if (request.redirectLimit < 0)
            {
                request.redirectLimit = 4;
            }
        };
    }

    private bool IsNetworkAvailable()
    {
        return Application.internetReachability != NetworkReachability.NotReachable;
    }

    private bool TrySwitchToBackupCdn(RemoteContentErrorKind errorKind)
    {
        if (useBackupCdn || string.IsNullOrEmpty(backupCdnRoot))
        {
            return false;
        }

        if (errorKind != RemoteContentErrorKind.ServerUnavailable
            && errorKind != RemoteContentErrorKind.ResourceMissing)
        {
            return false;
        }

        useBackupCdn = true;
        Debug.LogWarning("[RemoteContentPreload] Switching Addressables requests to the configured backup CDN.");
        return true;
    }

    private string TransformCdnInternalId(string internalId)
    {
        if (string.IsNullOrEmpty(internalId) || string.IsNullOrEmpty(primaryCdnRoot)
            || !internalId.StartsWith(primaryCdnRoot, System.StringComparison.OrdinalIgnoreCase))
        {
            return internalId;
        }

        string selectedRoot = useBackupCdn ? backupCdnRoot : primaryCdnRoot;
        return selectedRoot + internalId.Substring(primaryCdnRoot.Length);
    }

    private string NormalizeCdnRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)
            || !System.Uri.TryCreate(root, System.UriKind.Absolute, out System.Uri uri)
            || (uri.Scheme != System.Uri.UriSchemeHttps && uri.Scheme != System.Uri.UriSchemeHttp))
        {
            return null;
        }

        return root.Trim().TrimEnd('/') + "/";
    }

    private string ExtractContentVersion(string root)
    {
        if (!System.Uri.TryCreate(root, System.UriKind.Absolute, out System.Uri uri))
        {
            return null;
        }

        string[] segments = uri.AbsolutePath.Split(new[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < segments.Length; i++)
        {
            if (string.Equals(segments[i], "prod", System.StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return null;
    }

    private IEnumerator WaitForNetworkRecovery(float maximumWaitSeconds)
    {
        float startedAt = Time.realtimeSinceStartup;
        while (!IsNetworkAvailable())
        {
            if (maximumWaitSeconds > 0f && Time.realtimeSinceStartup - startedAt >= maximumWaitSeconds)
            {
                yield break;
            }

            yield return new WaitForSecondsRealtime(NetworkRecoveryPollSeconds);
        }
    }

    private IEnumerator WaitForPendingTimedOutOperations(bool showProgressTask)
    {
        if (pendingTimedOutOperations <= 0) yield break;
        RemoteContentOperationStage previousStage = currentStage;
        currentStage = RemoteContentOperationStage.RequestCleanup;
        float startedAt = Time.realtimeSinceStartup;
        if (showProgressTask) RegisterOrUpdateLoadingTask(progress, "等待上一资源请求结束");
        while (pendingTimedOutOperations > 0)
        {
            if (Time.realtimeSinceStartup - startedAt >= PendingOperationDrainTimeoutSeconds)
            {
                RecordFailure(RemoteContentOperationStage.RequestCleanup, RemoteContentErrorKind.Timeout,
                    RequiredResourceLabel, "超时的旧资源请求未结束；停止启动，避免重叠加载", false, lastOperationResult.Attempt);
                yield break;
            }
            yield return null;
        }
        currentStage = previousStage;
    }

    private void DrainTimedOutOperation(AsyncOperationHandle handle)
    {
        if (!handle.IsValid())
        {
            return;
        }

        pendingTimedOutOperations++;
        MonoManager.GetInstance().StartCoroutine(ReleaseOperationWhenComplete(handle));
    }

    private IEnumerator ReleaseOperationWhenComplete(AsyncOperationHandle handle)
    {
        while (handle.IsValid() && !handle.IsDone)
        {
            yield return null;
        }

        ReleaseIfValid(handle);
        pendingTimedOutOperations = Mathf.Max(0, pendingTimedOutOperations - 1);
    }

    private void ReleaseIfValid(AsyncOperationHandle handle)
    {
        if (handle.IsValid())
        {
            Addressables.Release(handle);
        }
    }

    private float AddRetryJitter(float delaySeconds)
    {
        return delaySeconds * UnityEngine.Random.Range(0.85f, 1.15f);
    }

    public static RemoteContentErrorKind ClassifyFailure(System.Exception exception, bool timedOut)
    {
        return ClassifyFailure(exception, timedOut, Application.internetReachability);
    }

    public static RemoteContentErrorKind ClassifyFailure(
        System.Exception exception,
        bool timedOut,
        NetworkReachability reachability)
    {
        if (timedOut)
        {
            return RemoteContentErrorKind.Timeout;
        }

        // Unity load conflicts and unsupported bundles are not network failures.
        string failureText = exception?.ToString()?.ToLowerInvariant() ?? string.Empty;
        if (failureText.Contains("tapbundleloadexception") || failureText.Contains("same files is already loaded")
            || failureText.Contains("already loaded by another provider"))
            return RemoteContentErrorKind.ResourceLoadFailure;
        if (reachability == NetworkReachability.NotReachable)
        {
            return RemoteContentErrorKind.Offline;
        }

        string message = exception?.ToString()?.ToLowerInvariant() ?? string.Empty;
        if (message.Contains("404") || message.Contains("not found"))
        {
            return RemoteContentErrorKind.ResourceMissing;
        }
        if (message.Contains("crc") || message.Contains("hash") || message.Contains("corrupt") || message.Contains("integrity"))
        {
            return RemoteContentErrorKind.IntegrityFailure;
        }
        if (message.Contains("disk") || message.Contains("storage") || message.Contains("quota") || message.Contains("space"))
        {
            return RemoteContentErrorKind.StorageFailure;
        }
        if (message.Contains("timeout") || message.Contains("timed out"))
        {
            return RemoteContentErrorKind.Timeout;
        }
        if (message.Contains("403") || message.Contains("401"))
        {
            return RemoteContentErrorKind.ResourceMissing;
        }
        if (message.Contains("429") || message.Contains("408") || message.Contains("500")
            || message.Contains("502") || message.Contains("503") || message.Contains("504")
            || message.Contains("network") || message.Contains("dns") || message.Contains("connection"))
        {
            return RemoteContentErrorKind.ServerUnavailable;
        }

        return RemoteContentErrorKind.Unknown;
    }

    public static bool IsRetryable(RemoteContentErrorKind errorKind)
    {
        return errorKind == RemoteContentErrorKind.Offline
            || errorKind == RemoteContentErrorKind.Timeout
            || errorKind == RemoteContentErrorKind.ServerUnavailable
            || errorKind == RemoteContentErrorKind.IntegrityFailure
            || errorKind == RemoteContentErrorKind.Unknown;
    }

    private void RecordSuccess(RemoteContentOperationStage stage, string label, int attempt)
    {
        lastOperationResult = new RemoteContentOperationResult
        {
            Stage = stage,
            ErrorKind = RemoteContentErrorKind.None,
            Label = label,
            CanRetry = false,
            Attempt = attempt
        };
        Debug.Log($"[RemoteContentTelemetry] stage={stage} result=success label={label ?? "-"} attempt={attempt} network={Application.internetReachability}");
    }

    private void RecordFailure(
        RemoteContentOperationStage stage,
        RemoteContentErrorKind errorKind,
        string label,
        string error,
        bool canRetry,
        int attempt)
    {
        lastOperationResult = new RemoteContentOperationResult
        {
            Stage = stage,
            ErrorKind = errorKind,
            Label = label,
            Error = error,
            CanRetry = canRetry,
            Attempt = attempt
        };
        lastError = BuildUserFacingError(lastOperationResult);
        Debug.LogWarning(
            $"[RemoteContentTelemetry] stage={stage} result=failure kind={errorKind} label={label ?? "-"} " +
            $"attempt={attempt} retryable={canRetry} network={Application.internetReachability} error={SanitizeError(error)}");
    }

    private string BuildUserFacingError(RemoteContentOperationResult result)
    {
        switch (result.ErrorKind)
        {
            case RemoteContentErrorKind.Offline:
                return "当前无网络连接，联网后请重试";
            case RemoteContentErrorKind.Timeout:
                return result.CanRetry ? $"网络响应超时，请重试（第 {Mathf.Max(1, result.Attempt)} 次）" : "资源请求超时且未结束，请重新启动游戏";
            case RemoteContentErrorKind.ResourceMissing:
                return "服务器资源不存在或版本不匹配，请稍后再试";
            case RemoteContentErrorKind.IntegrityFailure:
                return "资源校验失败，下载已停止，请重试";
            case RemoteContentErrorKind.ResourceLoadFailure:
                return "资源加载失败，下载已停止，请重新启动游戏";
            case RemoteContentErrorKind.StorageFailure:
                return "存储空间不足或缓存不可用，请清理空间后重试";
            case RemoteContentErrorKind.ServerUnavailable:
                return "资源服务器暂时不可用，请稍后重试";
            default:
                return "资源下载失败，请检查网络后重试";
        }
    }

    private string SanitizeError(string error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return "-";
        }

        int queryIndex = error.IndexOf('?');
        string sanitized = queryIndex >= 0 ? error.Substring(0, queryIndex) : error;
        return sanitized.Replace('\r', ' ').Replace('\n', ' ');
    }

    private void RegisterOrUpdateLoadingTask(float taskProgress, string taskName)
    {
        LoadingProgressManager progressManager = LoadingProgressManager.GetInstance();
        LoadingTask existingTask = progressManager.GetTask(LoadingTaskID);

        if (existingTask != null && existingTask.IsCompleted && taskProgress < 1f)
        {
            progressManager.UnregisterTask(LoadingTaskID);
            existingTask = null;
        }

        if (existingTask == null)
        {
            progressManager.RegisterTask(LoadingTaskID, taskName, LoadingTaskWeight, displayPriority: 1);
        }
        else
        {
            progressManager.UpdateTaskName(LoadingTaskID, taskName);
        }

        progressManager.UpdateTaskProgress(LoadingTaskID, taskProgress);
    }

    private void CompleteLoadingTask(string taskName)
    {
        LoadingProgressManager progressManager = LoadingProgressManager.GetInstance();

        if (progressManager.GetTaskProgress(LoadingTaskID) < 0f)
        {
            progressManager.RegisterTask(LoadingTaskID, taskName, LoadingTaskWeight, displayPriority: 1);
        }
        else
        {
            progressManager.UpdateTaskName(LoadingTaskID, taskName);
        }

        progressManager.CompleteTask(LoadingTaskID);
    }

    private string BuildProgressTaskName(string label)
    {
        return $"{LoadingTaskName}: {label} {progress * 100f:F0}% ({FormatBytes(downloadedBytes)} / {FormatBytes(totalDownloadBytes)})";
    }

    private string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
        return $"{bytes / (1024f * 1024f):F1} MB";
    }
}
