using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using FRX;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FRX
{
    public class FRX_Scanner
    {
        public static string ApplicationDataPath;
        private List<string> metaFilePaths;
        private List<string> assetFilePaths;

        private FRX_ScanWorker[] scanWorkers;
        private string rootPath;
        private string cacheSavePath;
        private string[] extensions;

        private int totalScanCount;

        public int TotalScanCount
        {
            get { return totalScanCount; }
        }

        public int ScannedCount
        {
            get { return curScanIndex; }
        }

        private int curScanIndex;

        private string error;

        public string Error
        {
            get { return Error; }
        }

        private int priority = 3;

        private ScannerState state = ScannerState.Initial;

        public ScannerState State
        {
            get { return state; }
        }

        private double cacheLastUpdateTime = 0;

        private Action onPrepareCompleteCallback;
        private Action onScanGUIDCompelteCallback;
        private Action onCompleteCallback;

        public FRX_Scanner(string rootPath, string cacheSavePath)
        { 
            this.cacheSavePath = cacheSavePath;
            this.rootPath = rootPath;
            ApplicationDataPath = FRX_ScanWorker.ApplicationDataPath = Application.dataPath;
        }

        public void BreakOff()
        {
            if (scanWorkers != null)
            {
                foreach (var worker in scanWorkers)
                {
                    worker.BreakOff();
                }
            }
            this.onCompleteCallback = null;
            this.onPrepareCompleteCallback = null;
            this.onScanGUIDCompelteCallback = null;
        }

        public void SetPriority(int priority)
        {
            if (state == ScannerState.Initial)
            {
                this.priority = priority;
            }
        }

        public void SetExtension(string[] exts)
        {
            if (state == ScannerState.Initial)
            {
                this.extensions = exts;
            }
        }

        public void Scan(Action onCompleteCallback)
        {
            this.onCompleteCallback = onCompleteCallback;
            state = ScannerState.Preparing;
            Action excuteScan = () =>
            {
                if (assetFilePaths.Count == 0)
                {
                    state = ScannerState.AllFinish;
                    return;
                }
                int threadCount = priority * 5;
                scanWorkers = new FRX_ScanWorker[threadCount];
                for (int i = 0; i < scanWorkers.Length; i++)
                {
                    scanWorkers[i] = new FRX_ScanWorker(rootPath, OnError);
                }
                ScanGUID(() =>
                {
                    ScanRef();
                });
            };
            EditorApplication.update += Poll;
            PrepareToScan(excuteScan);
        }

        public void PrepareToScan(Action onPrepareCompleteCallback)
        {
            this.onPrepareCompleteCallback = onPrepareCompleteCallback;
            Dictionary<string, AssetCacheInfo> guid2CacheInfoDict = null;
            Dictionary<string, string > path2GUIDDict = null;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            int sign = 2;
            Action callback = () =>
            {
                stopwatch.Stop();
              
                if (guid2CacheInfoDict != null && path2GUIDDict != null)
                {
                    stopwatch.Reset();
                    stopwatch.Start();
                    FilterAssetNeedScan(guid2CacheInfoDict, path2GUIDDict);
                    stopwatch.Stop();
                }
                
                FRX_ScanWorker.Init(guid2CacheInfoDict, path2GUIDDict);
                if (this.onPrepareCompleteCallback != null)
                {
                    this.onPrepareCompleteCallback.Invoke();
                }
            };
            //curScanIndex = 0;
            //totalScanCount = extensions.Length + 1;
            ThreadPool.SetMaxThreads(Environment.ProcessorCount, Environment.ProcessorCount);
            //GetFiles 比较耗时，放在另一线程执行
            ThreadPool.QueueUserWorkItem((o) =>
            {
                GetFilesToScan();
                Interlocked.Decrement(ref sign);
                if (0 >= sign)
                {
                    callback.Invoke();
                }
            }, null);
            ThreadPool.QueueUserWorkItem((o) =>
            {
                cacheLastUpdateTime = FRX_Main.GetCacheLastUpdateTime();
                if (cacheLastUpdateTime > 0) //有缓存,读缓存
                {
                    GetExistCache(out guid2CacheInfoDict, out path2GUIDDict);
                }
                Interlocked.Decrement(ref sign);
                if (0 >= sign)
                {
                    callback.Invoke();
                }
            }, null);
        }

        private void GetFilesToScan()
        {
            metaFilePaths = new List<string>(20000);
            assetFilePaths = new List<string>(5000);
            curScanIndex = 0;
            var allFilePaths = Directory.GetFiles(rootPath, "*.*", SearchOption.AllDirectories);//获取全部再筛选比调多次GetFiles还快
            totalScanCount = allFilePaths.Length;
            for (int i = 0; i < allFilePaths.Length; i++)
            {
                curScanIndex = i;
                var path = allFilePaths[i];
                if (path.EndsWith(".meta"))
                {
                    metaFilePaths.Add(path.Replace("\\", "/").Replace(ApplicationDataPath, "Assets"));
                }
                else
                {
                    foreach (var ex in extensions)
                    {
                        if (path.EndsWith(ex))
                        {
                            assetFilePaths.Add(path.Replace("\\", "/").Replace(ApplicationDataPath, "Assets"));
                        }
                    }
                }

            }
        }

        private void GetExistCache(out Dictionary<string, AssetCacheInfo> guid2CacheInfoDict, out Dictionary<string, string> path2GUIDDict)
        {
            if (FRX_Main.Path2CacheInfoDict != null && FRX_Main.Guid2PathDict != null)
            {
                guid2CacheInfoDict = new Dictionary<string, AssetCacheInfo>(FRX_Main.Guid2CacheInfoDict);
                path2GUIDDict = new Dictionary<string, string>(FRX_Main.Path2GUIDDict);
                return;
            }
           
            var lines = File.ReadAllLines(cacheSavePath);
            guid2CacheInfoDict = new Dictionary<string, AssetCacheInfo>();
            path2GUIDDict = new Dictionary<string, string>();
            for (int i = 0; i < lines.Length; i++)
            {
                AssetCacheInfo assetCacheInfo = AssetCacheInfo.Deserialize(lines[i]);
                assetCacheInfo.IsIgnore = false;
                guid2CacheInfoDict.Add(assetCacheInfo.GUID, assetCacheInfo);
                path2GUIDDict.Add(assetCacheInfo.Path, assetCacheInfo.GUID);
            }
        }
          
        private void FilterAssetNeedScan(Dictionary<string, AssetCacheInfo> guid2CacheInfoDict, Dictionary<string, string> path2GUIDDict)
        {
            List<string> fileteredAssetFilePaths = new List<string>();
            for (int i = assetFilePaths.Count - 1; i >= 0; i--)
            {
                var filePath = assetFilePaths[i];
                if (!CanIgnoreAsset(filePath))
                {
                    fileteredAssetFilePaths.Add(filePath);
                    if (path2GUIDDict.ContainsKey(filePath))
                    {
                        var guid = path2GUIDDict[filePath];
                        guid2CacheInfoDict.Remove(guid);
                        path2GUIDDict.Remove(filePath);
                    }
                }
            }
            assetFilePaths = fileteredAssetFilePaths;
        }

        private bool CanIgnoreAsset(string filePath)
        {
            if (cacheLastUpdateTime > 0)
            {
                FileInfo fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists) //已经被删除，需要从旧缓存中移除
                {
                    return false;
                }
                var fileUpdateTime = (fileInfo.LastWriteTime - new DateTime(1970, 1, 1)).TotalSeconds;
                return cacheLastUpdateTime - fileUpdateTime > 0; //资源最后的修改在上次扫描之前就不用再扫了;
            }
            return false;
        }

        private void Poll()
        {
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError(error);
                error = null;
            }
            if (state == ScannerState.ScanningGUID || state == ScannerState.ScanningRef)
            {
                bool isFinish = true;
                foreach (var worker in scanWorkers)
                {
                    if (worker.IsWorking)
                    {
                        isFinish = false;
                        break;
                    }
                } 
                if (isFinish)
                {
                    if (state == ScannerState.ScanningGUID) 
                    {
                        if (onScanGUIDCompelteCallback != null)
                        {
                            onScanGUIDCompelteCallback.Invoke();
                            onScanGUIDCompelteCallback = null;
                        }
                    }
                    else
                    {
                        state = ScannerState.AllFinish;
                    }
                }
            }
            if (state == ScannerState.AllFinish)
            {
                OnScanFinish();
            }
        }

        void ScanGUID(Action onFinishCallback)
        {
            this.onScanGUIDCompelteCallback = onFinishCallback;
            totalScanCount = metaFilePaths.Count;
            curScanIndex = 0;
            for (int i = 0; i < scanWorkers.Length; i++)
            {
                scanWorkers[i].StartScanGUID(GetMetaFileToScan, OnSingleWorkFinish);
            }
            state = ScannerState.ScanningGUID;
        }

        void ScanRef()
        {
            totalScanCount = assetFilePaths.Count;
            curScanIndex = 0;
            for (int i = 0; i < scanWorkers.Length; i++)
            {
                scanWorkers[i].StartScanRef(GetAssetFiletToScan, OnSingleWorkFinish);
            }
            state = ScannerState.ScanningRef;
        }

        void OnScanFinish()
        {
            metaFilePaths = null;
            assetFilePaths.Clear();
            System.GC.Collect();
            EditorApplication.update -= Poll;
            EditorUtility.ClearProgressBar();
            SeralizeScanData();
            if (onCompleteCallback != null)
            {
                onCompleteCallback.Invoke();
            }
        }

        void SeralizeScanData()
        {
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError(error);
                error = null; 
            }
            var assetInfoDict = FRX_ScanWorker.AssetInfoDict;
            StringBuilder sb = new StringBuilder();
            foreach (var kv in assetInfoDict)
            { 
                var info = kv.Value;
                if (!info.IsIgnore)
                {
                    var content = info.Serialize();
                    sb.AppendLine(content);
                }
            }
            File.WriteAllText(cacheSavePath, sb.ToString());
        }

        string GetMetaFileToScan()
        {
            lock (this)
            {
                if (curScanIndex < totalScanCount)
                {
                    return metaFilePaths[curScanIndex++];
                }
                return null;
            }
        }

        string GetAssetFiletToScan()
        {
            lock (this)
            {
                if (curScanIndex < totalScanCount)
                {
                    return assetFilePaths[curScanIndex++];
                }
                return null;
            }
        }

        void OnSingleWorkFinish()
        {
            //
        }

        void OnError(string err)
        {
            lock (this)
            {
                if (error == null)
                {
                    error = err;
                }
                else
                {
                    error += err + "\n";
                }
            }
        }

        public enum ScannerState
        {
            Initial = 0,
            Preparing,
            ScanningGUID,
            ScanningRef,
            AllFinish,
        }


    }

    public class AssetCacheInfo
    {
        private string guid;
        public string GUID {
            get { return guid; }
        }
        private string path;

        public string Path
        {
            get { return path; }
        }

        private HashSet<string> useGUIDs;
        private HashSet<string> useByGUIDs;

        public AssetCacheInfo(string guid, string path)
        {
            this.guid = guid;
            this.path = path;
        }

        private AssetCacheInfo()
        {
        }

        public void AddUse(string _guid)
        {
            lock (this)
            {
                if (useGUIDs == null)
                {
                    useGUIDs = new HashSet<string>();
                }
                if (!useGUIDs.Contains(_guid))
                {
                    useGUIDs.Add(_guid);
                }
            }

        }

        public void AddUseBy(string _guid)
        {
            lock (this)
            {
                if (useByGUIDs == null)
                {
                    useByGUIDs = new HashSet<string>();
                }
                if (!useByGUIDs.Contains(_guid))
                {
                    useByGUIDs.Add(_guid);
                }
            }
        }

        public string[] GetUseGUIDS()
        {
            if (useGUIDs != null)
            {
                return useGUIDs.ToArray();
            }
            return null;
        }

        public string[] GetUsebyGUIDS()
        {
            if (useByGUIDs != null)
            {
                return useByGUIDs.ToArray();
            }
            return null;
        }

        private bool isIgnore = true;

        public bool IsIgnore
        {
            get { return isIgnore; }
            set
            {
                lock (this)
                {
                    isIgnore = value;
                }
            }

        }

        public string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format("{0}${1}$", guid, path));
            if (useByGUIDs != null)
            {
                sb.Append(string.Join("#", useByGUIDs.ToArray()));
            }
            sb.Append("$");
            if (useGUIDs != null)
            {
                sb.Append(string.Join("#", useGUIDs.ToArray()));
            }
            return sb.ToString();
        }

        public static AssetCacheInfo Deserialize(string str)
        {
            AssetCacheInfo info = new AssetCacheInfo();
            str = str.Trim('\n');
            string[] strs = str.Split('$');
            info.guid = strs[0];
            info.path = strs[1];
            string[] usebyGuids = strs[2].Split('#');
            if (usebyGuids.Length > 0)
            {
                info.useByGUIDs = new HashSet<string>();
                foreach (var usebyGuid in usebyGuids)
                {
                    info.useByGUIDs.Add(usebyGuid);
                }
            }
            string[] useGuids = strs[3].Split('#');
            if (useGuids.Length > 0)
            {
                info.useGUIDs = new HashSet<string>();
                foreach (var useGuid in useGuids)
                {
                    info.useGUIDs.Add(useGuid);
                }
            }
            return info;
        }
    }
}
