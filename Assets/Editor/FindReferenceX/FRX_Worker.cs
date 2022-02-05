using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace FRX
{
    class FRX_ScanWorker
    {
        private static Dictionary<string, AssetCacheInfo> assetInfoDict;

        public static Dictionary<string, AssetCacheInfo> AssetInfoDict
        {
            get { return assetInfoDict; }
        }

        private static Dictionary<string, string> path2GUIDDict;

        public static Dictionary<string, string> Path2GUIDDict
        {
            get { return path2GUIDDict; }
        }

        public static string ApplicationDataPath;

        public static void Init(Dictionary<string, AssetCacheInfo> _assetInfoDict = null, Dictionary<string, string> _path2GUIDDict = null)
        {
            assetInfoDict = _assetInfoDict != null ? _assetInfoDict : new Dictionary<string, AssetCacheInfo>();
            path2GUIDDict = _path2GUIDDict != null ? _path2GUIDDict : new Dictionary<string, string>();
        }
         
        static void SaveAssetInfo(string guid, string path)
        {
            lock (assetInfoDict)
            {
                if (!assetInfoDict.ContainsKey(guid))
                {
                    AssetCacheInfo info = new AssetCacheInfo(guid, path);
                    assetInfoDict.Add(guid, info);
                    path2GUIDDict.Add(path, guid);
                }
            }
        }

        static AssetCacheInfo GetAssetInfo(string guid)
        {
            lock (assetInfoDict)
            {
                AssetCacheInfo result;
                assetInfoDict.TryGetValue(guid, out result);
                return result;
            }
        }

        static string GetAssetGUID(string path)
        {
            lock (path2GUIDDict)
            {
                string result;
                path2GUIDDict.TryGetValue(path, out result);
                return result;
            }
        }

        private string assetRootPath;
        private Func<string> getNextFilePathFunc;
        private Action onSingleWorkFinish;
        private Action<string> onError;
        private bool working = false;
        public bool IsWorking
        {
            get { return working; }
        }
        private static Regex GuidRegex = new Regex(@"guid:\s[a-zA-Z0-9]+");

        public Thread workingThread;

        public FRX_ScanWorker(string assetRootPath, Action<string> onError)
        {
            this.assetRootPath = assetRootPath;
            this.onError = onError;
        }

        public void StartScanGUID(Func<string> getNextFilePath, Action onSingleWorkFinish)
        {
            if (working) return;
            this.getNextFilePathFunc = getNextFilePath;
            this.onSingleWorkFinish = onSingleWorkFinish;
            working = true;
            workingThread = new Thread(ScanGUID);
            workingThread.Start();
        }

        public void StartScanRef(Func<string> getNextFilePath, Action onSingleWorkFinish)
        {
            if (working) return;
            this.getNextFilePathFunc = getNextFilePath;
            this.onSingleWorkFinish = onSingleWorkFinish;
            working = true;
            workingThread = new Thread(ScanRef);
            workingThread.Start();
        }

        public void BreakOff()
        {
            if (working)
            {
                try
                {
                    working = false;
                    workingThread.Abort();
                }
                catch (System.Threading.ThreadAbortException)
                {
                    //忽略
                }
            }
        }

        private void ScanGUID()
        {
            while (working)
            {
                var filePath = getNextFilePathFunc.Invoke();
                if (filePath == null)
                {
                    working = false; //这里不用调 workingThread.Abort(); 因为退出循环进程自然结束了
                    return;
                }
                string guid = null;
                if (!File.Exists(filePath))//文件夹不扫描
                {
                    continue;
                }
                try
                {
                    using (StreamReader sr = new StreamReader(filePath))
                    {
                        string line = sr.ReadLine();
                        while (line != null)
                        {
                            if (line.Contains("guid:"))
                            {
                                guid = line.Replace("guid:", "").Trim('\n').Trim();
                                break;
                            }
                            line = sr.ReadLine();
                        }
                    }
                    if (guid != null)
                    {
                        filePath = filePath.Replace(".meta", "");
                        SaveAssetInfo(guid, filePath);
                    }
                }
                catch (Exception ex)
                {
                    onError.Invoke(ex.ToString() + " -- " + filePath);

                }
                finally
                {
                    onSingleWorkFinish.Invoke();
                }
            }
        }

        private void ScanRef()
        {
            while (working)
            {
                try
                {
                    var filePath = getNextFilePathFunc.Invoke();
                    if (filePath == null)
                    {
                        working = false; //这里不用调 workingThread.Abort(); 因为退出循环进程自然结束了
                        return;
                    }
                    filePath = filePath.Replace("\\", "/");
                    var relativePath = filePath.Replace("\\", "/").Replace(ApplicationDataPath, "Assets");
                    string guid = GetAssetGUID(relativePath);
                    if (guid == null) 
                    {
                        onSingleWorkFinish.Invoke();
                        continue; 
                    }
                    var assetInfo = GetAssetInfo(guid);
                    if (assetInfo == null)
                    {
                        onSingleWorkFinish.Invoke();
                        continue;
                    }
                    assetInfo.IsIgnore = false;
                    if (filePath.ToLower().EndsWith(".fbx"))
                    {
                        filePath = filePath + ".meta";
                    }

                    var text = File.ReadAllText(filePath);
                    var matchs = GuidRegex.Matches(text);
                    foreach (Match m in matchs)
                    {
                        var refGuid = m.Value.Replace("guid: ", "");
                        refGuid = refGuid.Trim('\n').Trim();
                        assetInfo.AddUse(refGuid);
                        var refAssetInfo = GetAssetInfo(refGuid);
                        if (refAssetInfo != null)
                        {
                            refAssetInfo.AddUseBy(guid);
                            refAssetInfo.IsIgnore = false;
                        }
                    }
                    //for (int t = 2; t < lines.Length; t++)
                    //{
                    //    var line = lines[t];

                    //    if (line.Contains("guid:"))
                    //    {
                    //        var match = GuidRegex.Match(line);
                    //        if (match.Success)
                    //        {
                    //            var refGuid = match.Value.Replace("guid: ", "");
                    //            refGuid = refGuid.Trim('\n').Trim();
                    //            assetInfo.AddUse(refGuid);
                    //            var refAssetInfo = GetAssetInfo(refGuid);
                    //            if (refAssetInfo != null)
                    //            {
                    //                refAssetInfo.AddUseBy(guid);
                    //                refAssetInfo.IsIgnore = false;
                    //            }
                    //        }
                    //    }
                    //}
                }
                catch (Exception ex)
                {
                    onError.Invoke(ex.ToString());
                }
                finally
                {
                    onSingleWorkFinish.Invoke();
                }
            }
        }
    }
}