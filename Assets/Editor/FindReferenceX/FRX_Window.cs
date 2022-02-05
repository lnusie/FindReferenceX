using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FRX;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FRX
{
    public partial class FRX_Window : EditorWindow
    {
        [MenuItem("Tools/Find Reference X")]
        static void Show()
        {
            FRX_Window win = GetWindow<FRX_Window>();
            win.titleContent = new GUIContent("FRX");
            win.position = new Rect(400, 200, 780, 560);
        }

        private const int HISTORY_ITEM_COUNT = 10;
        private const int TREEVIEW_HEIGHT = 420;

        public int priority = 3;
        public string lastSelectPath;

        private InfoTreeView resultTreeView;
        private TreeViewState treeViewState;
        private SearchField m_SearchField;
        private Dictionary<string, List<string>> result;
        private List<FilterToggleInfo> filterToggles;
        private ToggleGroup<ToggleInfo> modeToggleGroup;


        private List<string> historySelects;
        private string lastSearch;
        private int tab;

        private InfoTreeView historyTreeView;
        private TreeViewState historyTreeViewState;
        private int historyTreeViewHeight;

        private void OnEnable()
        {
            if (treeViewState == null)
                treeViewState = new TreeViewState();
            historySelects = new List<string>();
            m_SearchField = new SearchField();
            resultTreeView = new InfoTreeView(treeViewState);
            if (historyTreeViewState == null)
            {
                historyTreeViewState = new TreeViewState();
            }
            historyTreeView = new InfoTreeView(historyTreeViewState);
            historyTreeView.RegistSelectCallback(OnSelectHistoryItem);
            m_SearchField.downOrUpArrowKeyPressed += resultTreeView.SetFocusAndEnsureSelectedItem;
            filterToggles = new List<FilterToggleInfo>()
            {
                new FilterToggleInfo() {toggleName = "忽略预制和场景", ignoreExtension = new[] {".prefab", ".unity"}},
                new FilterToggleInfo() {toggleName = "忽略材质球", ignoreExtension = new[] {".mat"}},
                new FilterToggleInfo()
                {
                    toggleName = "忽略动画",
                    ignoreExtension = new[] {".controller", ".overrideController", ".anim"},
                    value = false
                },
                new FilterToggleInfo() {toggleName = "忽略FBX", ignoreExtension = new[] {".fbx", ".FBX"}, value = false},
            };

        }

        private void OnGUI()
        {
            DrawRefInfoView();
        }

        bool DrawRefreshCacheContent()
        {
            bool refreshFinish = false;
            bool needRefresh = FRX_Main.NeedRefreshCache() && !FRX_Main.Running;
            var curProgress = FRX_Main.CurProgress;
            var totalProgress = FRX_Main.TotalProgress;

            if (FRX_Main.Running)
            {
                Rect rect = GUILayoutUtility.GetRect(1f, Screen.width, 18f, 18f);
                float value = (float) curProgress / (float) totalProgress;
                string str = curProgress == 0 ? "Starting ..." : "Refreshing ... " + curProgress + "/" + totalProgress;
                EditorGUI.ProgressBar(rect, value, str);
            }
            else if (needRefresh)
            {
                EditorGUILayout.HelpBox("刷新缓存后即可使用(约三四分钟)", MessageType.Info);
                var color = GUI.color;
                GUI.color = Color.green;
                if (GUILayout.Button(" 刷新缓存 "))
                {
                    FRX_Main.RefreshCache(() =>
                    {
                        ShowNotification(new GUIContent("刷新完成 ..."));
                    }, priority, GetFilterExtensions());
                }
                GUI.color = color;
                DrawFilterToggles();
            }
            else
            {
                refreshFinish = true;
            }
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Priority : ", GUILayout.MaxWidth(60));
            priority = EditorGUILayout.IntSlider(priority, 1, 3);
            EditorGUILayout.EndHorizontal();
            return refreshFinish;
        }

        void DrawCommonBottom()
        {
            bool needRefresh = FRX_Main.NeedRefreshCache() && !FRX_Main.Running;
            if (!needRefresh)
            {

                EditorGUILayout.BeginHorizontal();
                var timespan = FRX_Main.LastRefreshCacheTimeSpan;
                GUILayout.Label(string.Format("距离上次刷新缓存：{0}h {1}min", timespan.Hours, timespan.Minutes),
                    GUILayout.MinWidth(260));
                DrawFilterToggles();
                if (GUILayout.Button(FRX_Main.Running ? "中止刷新" : "刷新缓存"))
                {
                    if (FRX_Main.Running)
                    {
                        FRX_Main.Stop();
                    }
                    else
                    {
                        FRX_Main.RefreshCache(() =>
                        {
                            ShowNotification(new GUIContent("刷新完成"));
                        }, priority, GetFilterExtensions());
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        void DrawFilterToggles()
        {
            EditorGUILayout.BeginHorizontal();
            foreach (var info in filterToggles)
            {
                info.value = GUILayout.Toggle(info.value, info.toggleName);
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            string content = string.IsNullOrEmpty(lastSelectPath) ? "选中文件即可查看引用" : lastSelectPath;
            GUILayout.Label(content);
            resultTreeView.searchString = m_SearchField.OnToolbarGUI(resultTreeView.searchString);
            GUILayout.EndHorizontal();
        }
         
        void DrawTreeView()
        {
            var rect = EditorGUILayout.GetControlRect(false, TREEVIEW_HEIGHT - historyTreeViewHeight);
            resultTreeView.OnGUI(rect);
            if (!string.IsNullOrEmpty(resultTreeView.searchString))
            {
                lastSearch = resultTreeView.searchString;
            }
            if (string.IsNullOrEmpty(resultTreeView.searchString) && !string.IsNullOrEmpty(lastSearch))
            {
                var selection = resultTreeView.GetSelection();
                if (selection != null && selection.Count > 0)
                {
                    resultTreeView.FrameItem(selection[0]);
                }
                lastSearch = null;
            }
        }

        void DrawRefInfoView()
        {
            bool refreshFinish = DrawRefreshCacheContent();
            if (refreshFinish)
            {
                string target = GetSelectTarget();
                if (!string.IsNullOrEmpty(target))
                {
                    LoadAssetRefInfo(target);
                }
            }
            DrawToolbar();
            DrawHistoryTreeView();
            DrawTreeView();
            DrawCommonBottom();
        }

        string[] GetFilterExtensions()
        {
            List<string> exstensions = new List<string>();
            foreach (var info in filterToggles)
            {
                if (info.value)
                {
                    exstensions.AddRange(info.ignoreExtension);
                }
            }
            return exstensions.ToArray();
        }

        void DrawHistoryTreeView()
        {
            historyTreeViewHeight = HISTORY_ITEM_COUNT * 10;
            var rect = EditorGUILayout.GetControlRect(false, historyTreeViewHeight);
            historyTreeView.OnGUI(rect); 
        }

        string GetSelectTarget()
        {
            if (Selection.activeObject != null)
            {
                string path = AssetDatabase.GetAssetPath(Selection.activeObject);
               
                if (lastSelectPath != path && File.Exists(path))
                {
                    return path;
                }
            }
            return null;
        }

        void LoadAssetRefInfo(string path)
        {
            lastSelectPath = path;
            ShowNotification(new GUIContent("Wait ..."));
            EditorApplication.delayCall += () =>
            {
                FRX_Main.GetInfo(path, (result) =>
                {
                    RemoveNotification();
                    resultTreeView.SetData(result);
                    int count = 0;
                    foreach (var kv in result)
                    {
                        count += kv.Value.Count;
                    }
                    if (count < 30) 
                    {
                        resultTreeView.ExpandAll();
                    }
                    if (!historySelects.Contains(path))
                    {
                        historySelects.Insert(0, path);
                        if (historySelects.Count > HISTORY_ITEM_COUNT)
                        {
                            historySelects.RemoveRange(HISTORY_ITEM_COUNT, historySelects.Count - HISTORY_ITEM_COUNT);
                        }
                        historyTreeView.SetData(new Dictionary<string, List<string>> { { "History", historySelects } });
                        historyTreeView.ExpandAll();
                    }
                });
            };
        }

        private void OnSelectHistoryItem(string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj != null)
            {
                Selection.activeObject = obj;
            }
        }

        void Update()
        {
            Repaint();
        }

        class ToggleInfo
        {
            public string toggleName;
            public bool value;
        }

        class FilterToggleInfo : ToggleInfo
        {
            public string[] ignoreExtension;
        }

        class ToggleGroup<T> where T : ToggleInfo
        {
            public List<T> toggleInfos;
            public bool enableMultiSelect;
            public int selectIndex;
            public void OnSelectChange(int index)
            {
                selectIndex = index;
                for (int i = 0; i < toggleInfos.Count; i++)
                {
                    toggleInfos[i].value = i == selectIndex;
                }
            }
        }

    }

    class InfoTreeView : TreeView
    {
        private TreeViewItem rootItem;
        private Action<string> onSelectCallback;
        public InfoTreeView(TreeViewState treeViewState)
            : base(treeViewState)
        {
            rowHeight = 20;
            showAlternatingRowBackgrounds = true;
            showBorder = true;
            Reload();
        }

        private Dictionary<string, List<string>> data;

        public void SetData(Dictionary<string, List<string>> data)
        {
            this.data = data;
            Reload();
        }

        public void RegistSelectCallback(Action<string> callback)
        {
            if (onSelectCallback != null)
            {
                onSelectCallback = callback;
            }
            else
            {
                onSelectCallback += callback;
            }
        }

        protected override void ContextClickedItem(int selectId)
        {
            base.ContextClickedItem(selectId);
            if (onSelectCallback != null)
            {
                var selectItem = SearchItem(selectId);
                onSelectCallback.Invoke(selectItem != null ? selectItem.displayName : "");
            }
        }

        protected override TreeViewItem BuildRoot()
        {
            rootItem = new TreeViewItem { id = 0, depth = -1, displayName = "Root" };
            TraceTree(rootItem);
            if (!rootItem.hasChildren)
            {
                rootItem.AddChild(new TreeViewItem()
                {
                    id = GetGUID(),
                    depth = rootItem.depth + 1,
                    displayName = ""
                });
            }

            return rootItem;
        }

     
        private int id = 0;
        private int GetGUID()
        {
            return id++;
        }

        protected override void RowGUI(RowGUIArgs args)
        {
            base.RowGUI(args);
            Rect rect = args.rowRect;
            if (args.item.depth > 0)
            {
                float width = rect.width * 0.06f;
                float x = rect.x + rect.width - rect.width * 0.1f;
                rect.width = width;
                rect.x = x;
                rect.height *= 0.8f;
                if (GUI.Button(rect, "Ping"))
                {
                    var path = args.item.displayName;
                    var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                    if (obj != null)
                    {
                        EditorGUIUtility.PingObject(obj);
                    }
                }
            }
        }

        private void TraceTree(TreeViewItem root)
        {
            if (data == null) return;
            TreeViewItem curItem = null;
            foreach (var kv in data)
            {
                curItem = new TreeViewItem()
                {
                    id = GetGUID(),
                    depth = root.depth + 1,
                    displayName = kv.Key
                };
                root.AddChild(curItem);
                if (kv.Value == null) continue;
                foreach (var str in kv.Value)
                {
                    curItem.AddChild(new TreeViewItem()
                    {
                        id = GetGUID(),
                        depth = curItem.depth + 1,
                        displayName = str
                    });
                }
            }
        }

        private TreeViewItem SearchItem(int id)
        {
            Stack<TreeViewItem> tempStack = new Stack<TreeViewItem>();
            tempStack.Push(rootItem);
            while (tempStack.Peek() != null)
            {
                var item = tempStack.Pop();
                if (item.id == id)
                {
                    return item;
                    break;
                }
                foreach (var child in item.children)
                {
                    tempStack.Push(child);
                }
            }
            return null;
        }


        private static string AbsDirToSubDir(string absDir)
        {
            return absDir.Replace('\\', '/').Replace(Application.dataPath, "Assets");
        }
    }
}
