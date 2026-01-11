#if YOOASSET_INSTALLED
using System;
using Azathrix.Framework.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using Azathrix.Framework.Core.Attributes;
using Azathrix.Framework.Interfaces;
using Azathrix.Framework.Interfaces.DefaultSystems;
using Azathrix.Framework.Interfaces.SystemEvents;
using Azathrix.Framework.Tools;
using Cysharp.Threading.Tasks;
using YooAsset;

namespace Azathrix.YooAssetExtension
{
    /// <summary>
    /// YooAsset资源系统实现
    /// </summary>
    [SystemPriority(-1000)]
    public class YooAssetResourceSystem : IResourcesSystem, IDownloadMonitor, ICacheManager, ISystemInitialize
    {
        private ResourcePackage _defaultPackage;
        private YooAssetSettings _settings;
        private ResourceDownloaderOperation _currentDownloader;
        private DownloadManager _downloadManager;

        // IDownloadMonitor
        public event Action<DownloadProgress> OnProgressChanged;
        public event Action<string> OnDownloadError;
        public event Action OnDownloadComplete;
        public bool IsDownloading => _currentDownloader != null && !_currentDownloader.IsDone;
        public DownloadProgress CurrentProgress { get; private set; }

        /// <summary>
        /// 获取下载管理器
        /// </summary>
        public DownloadManager DownloadManager => _downloadManager;

        /// <summary>
        /// 获取默认资源包
        /// </summary>
        public ResourcePackage DefaultPackage => _defaultPackage;

        #region IDownloadMonitor

        public void StartMonitor()
        {
            if (_currentDownloader == null) return;
            _currentDownloader.OnDownloadProgressCallback = OnDownloadProgressInternal;
            _currentDownloader.OnDownloadErrorCallback = OnDownloadErrorInternal;
        }

        public void StopMonitor()
        {
            if (_currentDownloader != null)
            {
                _currentDownloader.OnDownloadProgressCallback = null;
                _currentDownloader.OnDownloadErrorCallback = null;
            }
        }

        private void OnDownloadProgressInternal(int totalCount, int currentCount, long totalBytes, long currentBytes)
        {
            CurrentProgress = new DownloadProgress
            {
                TotalCount = totalCount,
                CurrentCount = currentCount,
                TotalBytes = totalBytes,
                CurrentBytes = currentBytes
            };
            OnProgressChanged?.Invoke(CurrentProgress);
        }

        private void OnDownloadErrorInternal(string fileName, string error)
        {
            OnDownloadError?.Invoke($"{fileName}: {error}");
        }

        #endregion

        #region ICacheManager

        public CacheInfo GetCacheInfo(string packageName = null)
        {
            // YooAsset 2.x 暂无直接获取缓存大小的API
            return new CacheInfo { TotalSize = 0, FileCount = 0 };
        }

        public async UniTask ClearUnusedCacheAsync(string packageName = null)
        {
            var pkg = string.IsNullOrEmpty(packageName) ? _defaultPackage : YooAssets.GetPackage(packageName);
            if (pkg == null) return;

            var op = pkg.ClearUnusedCacheFilesAsync();
            await op.ToUniTask();
            Log.Info("[YooAsset] Unused cache cleared");
        }

        public async UniTask ClearAllCacheAsync(string packageName = null)
        {
            var pkg = string.IsNullOrEmpty(packageName) ? _defaultPackage : YooAssets.GetPackage(packageName);
            if (pkg == null) return;

            var op = pkg.ClearAllCacheFilesAsync();
            await op.ToUniTask();
            Log.Info("[YooAsset] All cache cleared");
        }

        #endregion

        #region IResourcesLoader

        public async UniTask<T> LoadAsync<T>(string key) where T : UnityEngine.Object
        {
            if (_defaultPackage == null)
            {
                Log.Error("[YooAsset] Package not initialized");
                return null;
            }

            var handle = _defaultPackage.LoadAssetAsync<T>(key);
            await handle.ToUniTask();

            if (handle.Status == EOperationStatus.Succeed)
            {
                return handle.AssetObject as T;
            }

            Log.Error($"[YooAsset] Failed to load asset: {key}");
            return null;
        }

        public T Load<T>(string key) where T : UnityEngine.Object
        {
            if (_defaultPackage == null)
            {
                Log.Error("[YooAsset] Package not initialized");
                return null;
            }

            var handle = _defaultPackage.LoadAssetSync<T>(key);

            if (handle.Status == EOperationStatus.Succeed)
            {
                return handle.AssetObject as T;
            }

            Log.Error($"[YooAsset] Failed to load asset: {key}");
            return null;
        }

        #endregion

        #region IResourcesSystem

        public async UniTask<GameObject> InstantiateAsync(string key, Transform parent = null)
        {
            var prefab = await LoadAsync<GameObject>(key);
            return prefab != null ? UnityEngine.Object.Instantiate(prefab, parent) : null;
        }

        public async UniTask LoadSceneAsync(string key, LoadSceneMode mode)
        {
            if (_defaultPackage == null)
            {
                Log.Error("[YooAsset] Package not initialized");
                return;
            }

            var op = _defaultPackage.LoadSceneAsync(key, mode);
            await op.ToUniTask();

            if (op.Status != EOperationStatus.Succeed)
            {
                Log.Error($"[YooAsset] Failed to load scene: {key}");
            }
        }

        #endregion

        public UniTask OnInitializeAsync()
        {
            var system = AzathrixFramework.GetSystem<IResourcesSystem>();
            Log.Info(system);
            // 获取已初始化的默认包（由 HotUpdatePhase 初始化）
            _settings = YooAssetSettings.Instance;
            _defaultPackage = YooAssets.GetPackage(_settings.defaultPackageName);
            if (_defaultPackage != null)
                _downloadManager = new DownloadManager(_defaultPackage, _settings);

            //将框架的资源加载切到YooAsset系统
            AzathrixFramework.ResourcesLoader = this;
            return UniTask.CompletedTask;
        }
    }
}
#endif
