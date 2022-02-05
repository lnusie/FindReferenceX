using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FRX
{
    public class FRX_Asset : ICloneable
    {
        public string relativePath;
        public List<string> useby = new List<string>();
        public List<string> use = new List<string>();

        public List<string> indirectUse;
        public List<string> indirectUseby;

        public object Clone()
        {
            var asset = new FRX_Asset();
            asset.relativePath = relativePath;
            string[] arr = new string[use.Count];
            use.CopyTo(arr);
            asset.use = new List<string>(arr);
            arr = new string[useby.Count];
            useby.CopyTo(arr);
            asset.useby = new List<string>(arr);
            return asset;
        }

        public Dictionary<string, List<string>> ToDict()
        {
            return new Dictionary<string, List<string>>()
            {
                { string.Format("Use By ({0})", useby.Count), useby},
                { string.Format("Use ({0})", use.Count), use},
                { string.Format("Indirect Useby ({0})", indirectUseby.Count), indirectUseby},
                { string.Format("Indirect Use ({0})", indirectUse.Count), indirectUse},
            };
        }
    }

    public class FRX_Main
    {
        public const float UPDATE_CACHE_INTERVAL = 60 * 60 * 8; //s

        //能引用其他资源的资源
        public static string[] AssetExtensions = new[] {".prefab",".mat",".asset",".unity",".controller",".overrideController",".anim", ".fbx", ".FBX", ".spriteatlas" };

        private static string cacheFileDir;

        public static string CacheFileDir
        {
            get
            {
                if (string.IsNullOrEmpty(cacheFileDir))
                {
                    cacheFileDir = Application.persistentDataPath + "\\FR_X";
                }
                return cacheFileDir;
            }

        }

        private static string cachePath;

        public static string CachePath
        {
            get
            {
                if (string.IsNullOrEmpty(cachePath))
                {
                    cachePath = CacheFileDir + "\\frx_cache.txt";
                }
                return cachePath;
            }
        }

        private static int curProgress;

        public static int CurProgress
        {
            get { return curProgress; }
        }
        private static int totalProgress;

        public static int TotalProgress
        {
            get { return totalProgress; }
        }

        private static FRX_Scanner scanner;

        public static FRX_Scanner Scanner
        {
            get
            {  
                if (scanner == null)
                {
                    //scanner = new FRX_Scanner(Application.dataPath, CachePath);
                    scanner = new FRX_Scanner(Application.dataPath, CachePath);
                }
                return scanner; 
            }
        }

        private static bool running;

        public static bool Running
        {
            get { return running; }
        }

        public static TimeSpan LastRefreshCacheTimeSpan
        {
            get
            {
                if ((File.Exists(CachePath)))
                {
                    FileInfo file = new FileInfo(CachePath);
                    return (DateTime.Now - file.LastWriteTime);
                }
                return TimeSpan.Zero;
            }
        }

        public static double GetCacheLastUpdateTime()
        {
            if (File.Exists(CachePath))
            {
                FileInfo file = new FileInfo(CachePath);
                double lastWriteTime = (file.LastWriteTime - new DateTime(1970, 1, 1)).TotalSeconds;
                return lastWriteTime;
            }
            return 0;
        }

        public static bool NeedRefreshCache()
        {
            if (!File.Exists(CachePath))
            {
                return true;
            }
            else
            {
                var cacheLastUpdateTime = GetCacheLastUpdateTime();
                double nowTime = (DateTime.Now - new DateTime(1970, 1, 1)).TotalSeconds;
                if (nowTime - cacheLastUpdateTime >= UPDATE_CACHE_INTERVAL)
                {
                    return true;
                }
            }
            return false;
        }

        private static Dictionary<string, FRX_Asset> assetDicts = new Dictionary<string, FRX_Asset>();
        private static Dictionary<string, AssetCacheInfo> path2CacheInfoDict;
        public static Dictionary<string, AssetCacheInfo> Path2CacheInfoDict
        {
            get { return path2CacheInfoDict; }
        }

        private static Dictionary<string, AssetCacheInfo> guid2CacheInfoDict;
        public static Dictionary<string, AssetCacheInfo> Guid2CacheInfoDict
        {
            get { return guid2CacheInfoDict; }
        }

        private static Dictionary<string, string> guid2PathDict;
        public static Dictionary<string, string> Guid2PathDict
        {
            get { return guid2PathDict; }
        }

        private static Dictionary<string, string> path2GUIDDict;
        public static Dictionary<string, string> Path2GUIDDict
        {
            get { return path2GUIDDict; }
        }

        #region 外部接口

        /// <summary>
        /// 刷新引用缓存
        /// </summary>
        /// <param name="onFinish">结束回调</param>
        /// <param name="priority">优先级，默认为3</param>
        /// <param name="ignoreExtensions">不扫描的资源，比如想忽略材质球的引用，可传{".mat"}</param>
        public static void RefreshCache(Action onFinish = null, int priority = 3, string[] ignoreExtensions = null)
        {
            if (running) return;
            running = true;
            if (!Directory.Exists(CacheFileDir))
            {
                Directory.CreateDirectory(CacheFileDir);
            }
            totalProgress = curProgress = 0;
            assetDicts.Clear();
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            var extensions = AssetExtensions.ToList();
            if (ignoreExtensions != null)
            {
                for (int i = extensions.Count - 1; i >= 0; i--)
                {
                    if (ignoreExtensions.Contains(extensions[i]))
                    {
                        extensions.RemoveAt(i);
                    }
                }
            }
            if (NeedRefreshCache() && File.Exists(CachePath))
            {
                File.Delete(CachePath);
            }
            Scanner.SetPriority(priority);
            Scanner.SetExtension(extensions.ToArray());
            Scanner.Scan(() =>
            {
                stopwatch.Stop();
                Debug.Log("Scan finish, cost : " + stopwatch.ElapsedMilliseconds * 0.001 + "(s)");
                Stop();
                path2CacheInfoDict = null;
                if (onFinish != null) 
                {
                    onFinish.Invoke();
                }
            });
            EditorApplication.update += Poll;
        }

        private static void SeekIndirectUse(ref FRX_Asset asset)
        {
            asset.indirectUse = new List<string>();
            SeekIndirectUseRecursive(ref asset, 0, true);
        }

        private static void SeekIndirectUseRecursive(ref FRX_Asset asset, int start, bool first)
        {
            var list = first ? asset.use : asset.indirectUse;
            var end = list.Count;
            if (start >= end) return;
            for (int i = start; i < end; i++)
            {
                var path = list[i];
                if (path2CacheInfoDict.ContainsKey(path))
                {
                    var tempAsset = CacheInfo2Asset(path2CacheInfoDict[path]);
                    foreach (var use in tempAsset.use)
                    {
                        if (!asset.indirectUse.Contains(use))
                        {
                            asset.indirectUse.Add(use);
                        }
                    }
                }
            }
            if (first)
            {
                SeekIndirectUseRecursive(ref asset, 0, false);
            }
            else
            {
                SeekIndirectUseRecursive(ref asset, end, false);
            }

        }

        private static void SeekIndirectUseby(ref FRX_Asset asset)
        {
            asset.indirectUseby = new List<string>();
            SeekIndirectUsebyRecursive(ref asset, 0, true);
        }

        private static void SeekIndirectUsebyRecursive(ref FRX_Asset asset, int start, bool first)
        {
            var list = first ? asset.useby : asset.indirectUseby;
            var end = list.Count;
            if (start >= end) return;

            for (int i = start; i < end; i++)
            {
                var path = list[i];
                if (path2CacheInfoDict.ContainsKey(path))
                {
                    var tempAsset = CacheInfo2Asset(path2CacheInfoDict[path]);
                    foreach (var useby in tempAsset.useby)
                    {
                        if (!asset.indirectUseby.Contains(useby))
                        {
                            asset.indirectUseby.Add(useby);
                        }
                    }
                }
            }
            if (first)
            {
                SeekIndirectUseRecursive(ref asset, 0, false);
            }
            else
            {
                SeekIndirectUseRecursive(ref asset, end, false);
            }
        }

        public static void GetInfo(List<string> assetPaths, int prioriy, Action<Dictionary<string, FRX_Asset>> callback)
        {
            if (!Directory.Exists(CacheFileDir))
            {
                Directory.CreateDirectory(CacheFileDir);
            }
            Dictionary<string, FRX_Asset> result = new Dictionary<string, FRX_Asset>();
            for (int i = assetPaths.Count - 1; i >= 0; i--)
            {
                string path = assetPaths[i];
                if (assetDicts.ContainsKey(path))
                {
                    result.Add(path, assetDicts[path]);
                    assetPaths.RemoveAt(i);
                }
            }
            if (path2CacheInfoDict == null)
            {
                DeserializeCache();    
            }
            foreach (var assetPath in assetPaths)
            {
                if (path2CacheInfoDict.ContainsKey(assetPath))
                {
                    var asset = CacheInfo2Asset(path2CacheInfoDict[assetPath]);
                    if (!assetDicts.ContainsKey(assetPath))
                    { 
                        assetDicts.Add(assetPath, asset);
                    }
                    SeekIndirectUse(ref asset);
                    SeekIndirectUseby(ref asset);
                    result.Add(assetPath, asset);
                }
            }
            if (callback != null)
            {
                callback.Invoke(result);
            }
           
        }

        public static void GetInfo(string assetPath, Action<Dictionary<string, List<string>>> callback)
        {
            if (assetDicts.ContainsKey(assetPath) && callback != null)
            {
                callback.Invoke(assetDicts[assetPath].ToDict());
                return;
            }
            GetInfo(new List<string>() { assetPath }, 1, (dict) =>
            {
                if (callback != null)
                {
                    if (dict.ContainsKey(assetPath))
                    {
                        callback.Invoke(dict[assetPath].ToDict());
                    }
                    else
                    {
                        callback.Invoke(new Dictionary<string, List<string>>()
                        {
                            { string.Format("Use By ({0})", 0), new List<string>()},
                            { string.Format("Use ({0})", 0), new List<string>()},
                        });
                    }
                }
            });
        }

        public static void Stop()
        {
            Scanner.BreakOff();
            curProgress = totalProgress = 0;
            running = false;
            callbackAfterExcute = null;
            outputInfo = null;
            EditorApplication.update -= Poll;

        }
        #endregion

        private static void DeserializeCache()
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            var lines = File.ReadAllLines(CachePath);
            path2CacheInfoDict = new Dictionary<string, AssetCacheInfo>();
            guid2CacheInfoDict = new Dictionary<string, AssetCacheInfo>();
            guid2PathDict = new Dictionary<string, string>();
            path2GUIDDict = new Dictionary<string, string>();
            totalProgress = lines.Length;
            for (int i = 0; i < lines.Length; i++)
            {
                curProgress = i; 
                AssetCacheInfo assetCacheInfo = AssetCacheInfo.Deserialize(lines[i]);
                assetCacheInfo.IsIgnore = false;
                path2CacheInfoDict.Add(assetCacheInfo.Path, assetCacheInfo);
                guid2CacheInfoDict.Add(assetCacheInfo.GUID, assetCacheInfo);
                guid2PathDict.Add(assetCacheInfo.GUID, assetCacheInfo.Path);
                path2GUIDDict.Add(assetCacheInfo.Path, assetCacheInfo.GUID);
            }
            GC.Collect();
            stopwatch.Stop();
            Debug.Log("DeserializeCache finish, cost : " + stopwatch.ElapsedMilliseconds * 0.001 + "(s)");
        }

        private static Action<string> callbackAfterExcute;
        private static Process process;
        private static string outputInfo;

        private static void Poll()
        {
            curProgress = scanner.ScannedCount;
            totalProgress = scanner.TotalScanCount;
        }

        private static FRX_Asset CacheInfo2Asset(AssetCacheInfo cacheInfo)
        {
            FRX_Asset asset = new FRX_Asset();
            asset.relativePath = cacheInfo.Path;
            var useGUIDs = cacheInfo.GetUseGUIDS();
            foreach (var guid in useGUIDs)
            {
                if (guid2CacheInfoDict.ContainsKey(guid))
                {
                    var useCacheInfo = guid2CacheInfoDict[guid];
                    asset.use.Add(useCacheInfo.Path);
                }
            }
            var usebyGUIDs = cacheInfo.GetUsebyGUIDS();
            foreach (var guid in usebyGUIDs)
            {
                if (guid2CacheInfoDict.ContainsKey(guid))
                {
                    var useCacheInfo = guid2CacheInfoDict[guid];
                    asset.useby.Add(useCacheInfo.Path);
                }
            }
            return asset;
        }
    }
}
