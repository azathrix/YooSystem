#if YOOASSET_INSTALLED
using System;
using Azathrix.Framework.Core.Startup;
using Azathrix.Framework.Core.Startup.Phases;
using Azathrix.Framework.Tools;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace Azathrix.YooAssetExtension
{
    /// <summary>
    /// 热更新阶段 - 在系统注册之前执行
    /// </summary>
    public class HotUpdatePhase : IHotUpdatePhase, IHotUpdateFlow
    {
        public string Id => "HotUpdate";
        public int Order => 50; // 在 ScanPhase(100) 之前

        // IHotUpdateFlow
        public HotUpdateState State { get; private set; }
        public string ErrorMessage { get; private set; }

        private ResourcePackage _defaultPackage;
        private YooAssetSettings _settings;
        private string _currentVersion;

        public async UniTask ExecuteAsync(PhaseContext context)
        {
            _settings = YooAssetSettings.Instance;
            if (!_settings.autoInitOnStartup)
            {
                Log.Info("[YooAsset] Auto init disabled, skip hot update phase");
                return;
            }

            Log.Info("[YooAsset] Starting hot update...");
            var success = await RunFullUpdateAsync(_settings.autoDownloadTags);
            if (!success)
            {
                Log.Error($"[YooAsset] Hot update failed: {ErrorMessage}");
            }
        }

        #region IHotUpdateFlow

        public async UniTask<bool> InitPackageAsync(string packageName = null)
        {
            State = HotUpdateState.InitPackage;
            packageName ??= _settings.defaultPackageName;

            YooAssets.Initialize();

            _defaultPackage = YooAssets.TryGetPackage(packageName)
                ?? YooAssets.CreatePackage(packageName);
            YooAssets.SetDefaultPackage(_defaultPackage);

            InitializationOperation initOp = _settings.playMode switch
            {
                EPlayMode.EditorSimulateMode => InitEditorSimulate(packageName),
                EPlayMode.OfflinePlayMode => InitOffline(packageName),
                EPlayMode.HostPlayMode => InitHost(packageName),
                EPlayMode.WebPlayMode => InitWebGL(packageName),
                _ => null
            };

            if (initOp == null)
            {
                ErrorMessage = "Invalid play mode";
                State = HotUpdateState.Failed;
                return false;
            }

            await initOp.ToUniTask();

            if (initOp.Status != EOperationStatus.Succeed)
            {
                ErrorMessage = initOp.Error;
                State = HotUpdateState.Failed;
                Log.Error($"[YooAsset] Package init failed: {initOp.Error}");
                return false;
            }

            Log.Info($"[YooAsset] Package {packageName} initialized");
            return true;
        }

        public async UniTask<bool> UpdateVersionAsync()
        {
            State = HotUpdateState.UpdateVersion;
            var op = _defaultPackage.UpdatePackageVersionAsync();
            await op.ToUniTask();

            if (op.Status != EOperationStatus.Succeed)
            {
                ErrorMessage = op.Error;
                State = HotUpdateState.Failed;
                Log.Error($"[YooAsset] Update version failed: {op.Error}");
                return false;
            }

            _currentVersion = op.PackageVersion;
            Log.Info($"[YooAsset] Version: {_currentVersion}");
            return true;
        }

        public async UniTask<bool> UpdateManifestAsync()
        {
            State = HotUpdateState.UpdateManifest;

            if (string.IsNullOrEmpty(_currentVersion))
            {
                var versionOp = _defaultPackage.UpdatePackageVersionAsync();
                await versionOp.ToUniTask();
                if (versionOp.Status != EOperationStatus.Succeed)
                {
                    ErrorMessage = versionOp.Error;
                    State = HotUpdateState.Failed;
                    return false;
                }
                _currentVersion = versionOp.PackageVersion;
            }

            var op = _defaultPackage.UpdatePackageManifestAsync(_currentVersion);
            await op.ToUniTask();

            if (op.Status != EOperationStatus.Succeed)
            {
                ErrorMessage = op.Error;
                State = HotUpdateState.Failed;
                Log.Error($"[YooAsset] Update manifest failed: {op.Error}");
                return false;
            }

            Log.Info("[YooAsset] Manifest updated");
            return true;
        }

        public async UniTask<ResourceDownloaderOperation> CreateDownloaderByTagsAsync(params string[] tags)
        {
            State = HotUpdateState.CreateDownloader;
            return _defaultPackage.CreateResourceDownloader(tags,
                _settings.downloadingMaxNum, _settings.failedTryAgain);
        }

        public async UniTask<ResourceDownloaderOperation> CreateDownloaderByPathsAsync(params string[] paths)
        {
            State = HotUpdateState.CreateDownloader;
            return _defaultPackage.CreateBundleDownloader(paths,
                _settings.downloadingMaxNum, _settings.failedTryAgain);
        }

        public async UniTask<bool> RunFullUpdateAsync(string[] downloadTags = null)
        {
            try
            {
                if (!await InitPackageAsync()) return false;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return false;
            }

            if (_settings.playMode == EPlayMode.HostPlayMode)
            {
                if (!await UpdateVersionAsync()) return false;
                if (!await UpdateManifestAsync()) return false;

                var tags = downloadTags ?? _settings.autoDownloadTags;
                if (tags != null && tags.Length > 0)
                {
                    var downloader = await CreateDownloaderByTagsAsync(tags);
                    if (downloader.TotalDownloadCount > 0)
                    {
                        Log.Info($"[YooAsset] Need download {downloader.TotalDownloadCount} files, {downloader.TotalDownloadBytes} bytes");

                        State = HotUpdateState.Downloading;
                        downloader.BeginDownload();
                        await downloader.ToUniTask();

                        if (downloader.Status != EOperationStatus.Succeed)
                        {
                            ErrorMessage = downloader.Error;
                            State = HotUpdateState.Failed;
                            Log.Error($"[YooAsset] Download failed: {downloader.Error}");
                            return false;
                        }
                    }
                }
            }

            State = HotUpdateState.Done;
            Log.Info("[YooAsset] Hot update completed");
            return true;
        }

        #endregion

        #region Private Helpers

        private InitializationOperation InitEditorSimulate(string packageName)
        {
            var param = new EditorSimulateModeParameters();
            param.SimulateManifestFilePath = EditorSimulateModeHelper.SimulateBuild(packageName);
            return _defaultPackage.InitializeAsync(param);
        }

        private InitializationOperation InitOffline(string packageName)
        {
            return _defaultPackage.InitializeAsync(new OfflinePlayModeParameters());
        }

        private InitializationOperation InitHost(string packageName)
        {
            var param = new HostPlayModeParameters();
            param.BuildinQueryServices = new GameQueryServices();
            param.RemoteServices = new RemoteServices(_settings.hostServerURL, _settings.fallbackHostServerURL);
            return _defaultPackage.InitializeAsync(param);
        }

        private InitializationOperation InitWebGL(string packageName)
        {
            var param = new WebPlayModeParameters();
            param.BuildinQueryServices = new GameQueryServices();
            param.RemoteServices = new RemoteServices(_settings.hostServerURL, _settings.fallbackHostServerURL);
            return _defaultPackage.InitializeAsync(param);
        }

        private class GameQueryServices : IBuildinQueryServices
        {
            public bool Query(string packageName, string fileName, string fileCRC) => false;
            public bool QueryStreamingAssets(string packageName, string fileName) => false;
        }

        private class RemoteServices : IRemoteServices
        {
            private readonly string _defaultUrl;
            private readonly string _fallbackUrl;

            public RemoteServices(string defaultUrl, string fallbackUrl)
            {
                _defaultUrl = defaultUrl;
                _fallbackUrl = fallbackUrl;
            }

            public string GetRemoteMainURL(string fileName) => $"{_defaultUrl}/{fileName}";

            public string GetRemoteFallbackURL(string fileName) =>
                string.IsNullOrEmpty(_fallbackUrl) ? GetRemoteMainURL(fileName) : $"{_fallbackUrl}/{fileName}";
        }

        #endregion
    }
}
#endif
