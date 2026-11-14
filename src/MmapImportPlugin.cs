/* -*- coding: utf-8 -*-
 * ICC-CE 插件：将 MindManager .mmap 导图导入白板画布
 * v0.1.1 —— 按官方 SamplePlugin 范式重写：
 *   - 继承 PluginBase（元数据从 manifest.json 读取）
 *   - 引用 NuGet InkCanvas.PluginSdk/Controls 1.7.19.9（与正式版宿主 API 对齐）
 *   - 工具栏组件用 Ink_Canvas.Controls.ToolbarImageButton
 *   - 不打包 SDK DLL（pack.ps1 同款三文件清单），避免与宿主类型冲突
 */
using Ink_Canvas.Controls;
using Ink_Canvas.Plugins;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InkCanvas.Plugin.MmapImport
{
    [PluginEntrance]
    public class MmapImportPlugin : PluginBase
    {
        MmapCanvas _currentMap;                       // 当前可交互导图（元素模式）
        readonly List<string> _hotkeyIds = new List<string>();

        public override void Initialize(IPluginHost host, IServiceCollection services)
        {
            base.Initialize(host, services);

            KeepToolbarButtonsAlive();

            // 翻页/换页时重置画布跟随矩阵（新页面 = 全新画布空间）
            var events = GetService<IEventService>();
            if (events != null) events.WhiteboardPageChanged += (_, __) => CanvasSync.ResetAll();

            // 按钮 1：导图作为可交互元素上画布（可点 +/− 展开收起、可拖动）
            host.RegisterBoardToolbarItem(new PluginToolbarItemInfo
            {
                Id = "top.brokoon.mmapimport.button",
                DisplayName = "导入导图",
                Description = "选择 MindManager .mmap 导图文件导入白板：还原层级、配色、折叠态；点 +/− 展开（触摸屏点不动时用 Alt+Shift+E / Alt+Shift+C 全部展开/收起）",
                IconGeometry = "M4 4h7v5H4V4zm9 6h7v5h-7v-5zM4 15h7v5H4v-5zm7-7.5h2v5h5v2h-7v-7z",
                ViewFactory = CreateButton,
                ApplyOrientation = (view, orientation) =>
                {
                    if (view is ToolbarImageButton btn)
                        btn.ApplyOrientation(orientation == Orientation.Vertical);
                },
            });

            // 按钮 2：导图全展开铺到白板背景层（当"纸"用，笔迹直接写图上，漫游/双指由同步器跟随）
            host.RegisterBoardToolbarItem(new PluginToolbarItemInfo
            {
                Id = "top.brokoon.mmapimport.bgbutton",
                DisplayName = "导图上背景",
                Description = "将导图全展开铺在白板背景层：可直接在图上书写批注，随画布漫游缩放（无交互节点）",
                IconGeometry = "M4 4h7v5H4V4zm9 6h7v5h-7v-5zM4 15h7v5H4v-5zm7-7.5h2v5h5v2h-7v-7z",
                ViewFactory = CreateBackgroundButton,
                ApplyOrientation = (view, orientation) =>
                {
                    if (view is ToolbarImageButton btn)
                        btn.ApplyOrientation(orientation == Orientation.Vertical);
                },
            });

            RegisterHotkeys(host);

            Log(string.Format("{0} (mmap 导图导入) 已初始化", Name));
        }

        /// <summary>
        /// v0.5.1 工具栏按钮保活。
        ///
        /// 宿主机制：插件注册白板工具栏按钮时，只有「首次注册」（插件目录无 .toolbar_registered
        /// 标记）才会把按钮写进当前工具栏配置；之后启动只进组件库——本意是防止用户手动删掉的
        /// 组件重启后被自动加回。但工具栏配置一旦因宿主更新/迁移/重置被重建，所有插件的按钮
        /// 就会集体消失，且宿主不再自动恢复（需到设置里重新启用触发一次重注册）。
        ///
        /// 处理：Initialize 时删除本插件及同目录下其他插件的 .toolbar_registered 标记，
        /// 宿主随之按「首次注册」处理 → EnsurePluginItemInActiveConfig 幂等回填
        /// （Registry 按组件 Id 去重，不会堆重复按钮）。副作用：从工具栏手动移除的按钮
        /// 重启后会回来；要彻底移除某插件请卸载它。
        /// </summary>
        void KeepToolbarButtonsAlive()
        {
            try
            {
                var myDir = Path.GetDirectoryName(typeof(MmapImportPlugin).Assembly.Location);
                if (string.IsNullOrEmpty(myDir) || !Directory.Exists(myDir)) return;

                var pluginsRoot = Directory.GetParent(myDir);           // .../Plugins
                var dirs = pluginsRoot != null
                    ? Directory.GetDirectories(pluginsRoot.FullName)
                    : new[] { myDir };

                int cleared = 0;
                foreach (var dir in dirs)
                {
                    var marker = Path.Combine(dir, ".toolbar_registered");
                    if (!File.Exists(marker)) continue;
                    try { File.Delete(marker); cleared++; }
                    catch (Exception ex) { Log(string.Format("清除标记失败 {0}: {1}", dir, ex.Message)); }
                }
                if (cleared > 0)
                    Log(string.Format("已清除 {0} 个 .toolbar_registered 标记，本次启动按钮将强制回填工具栏", cleared));
            }
            catch (Exception ex) { Log("工具栏保活异常: " + ex.Message); }
        }

        /// <summary>
        /// 注册全局热键（走宿主 IHotkeyService）：Alt+Shift+E 全部展开 / Alt+Shift+C 全部收起。
        /// 鸿合触摸屏点不动 +/− 徽标时的兜底操作。注册前查询宿主已占用热键自动避让：
        /// 主选键冲突则回退 O / P，再冲突则放弃该热键（避免抢占宿主/其他软件）。
        /// </summary>
        void RegisterHotkeys(IPluginHost host)
        {
            try
            {
                var hk = GetService<IHotkeyService>();
                if (hk == null) { Log("宿主未提供 IHotkeyService，快捷键不可用"); return; }

                var used = hk.GetRegisteredHotkeys();
                TryRegister(hk, "top.brokoon.mmapimport.expandall", "全部展开",
                    new[] { (Key.E, 0x45u), (Key.O, 0x4Fu) }, () => ToggleAll(true));
                TryRegister(hk, "top.brokoon.mmapimport.collapseall", "全部收起",
                    new[] { (Key.C, 0x43u), (Key.P, 0x50u) }, () => ToggleAll(false));
            }
            catch (Exception ex) { Log("热键注册异常: " + ex.Message); }
        }

        /// <summary>Alt|Shift=5（SDK 位定义 Alt=1 Shift=4）；候选键依次尝试。</summary>
        void TryRegister(IHotkeyService hk, string id, string label,
            (Key key, uint vk)[] candidates, Action action)
        {
            const uint MOD_ALT_SHIFT = 1u | 4u;
            var used = hk.GetRegisteredHotkeys();
            foreach (var (key, vk) in candidates)
            {
                bool conflict = false;
                if (used != null)
                    foreach (var h in used)
                        if (h.Key == key && h.Modifiers == (ModifierKeys.Alt | ModifierKeys.Shift))
                        { conflict = true; break; }
                if (conflict) continue;

                if (hk.Register(id, MOD_ALT_SHIFT, vk, action))
                {
                    _hotkeyIds.Add(id);
                    Log(string.Format("热键注册成功: {0} = Alt+Shift+{1}", label, key));
                    return;
                }
            }
            Log(string.Format("热键 {0} 全部候选键被占用，未注册", label));
        }

        void ToggleAll(bool expand)
        {
            try
            {
                var map = _currentMap;
                if (map == null || (map.Parent == null && System.Windows.Media.VisualTreeHelper.GetParent(map) == null))
                {
                    Notify("导图快捷键", "当前页没有可交互导图（背景模式导图恒为全展开）", NotificationLevel.Info);
                    return;
                }
                map.Dispatcher.Invoke(() => map.SetAllExpanded(expand));
                Notify("导图快捷键", expand ? "已展开全部分支" : "已收起全部分支", NotificationLevel.Success);
            }
            catch (Exception ex) { Log("热键处理异常: " + ex.Message); }
        }

        FrameworkElement CreateButton()
        {
            var btn = new ToolbarImageButton
            {
                Label = "导入导图",
                Tag = "mmapimport",
            };
            btn.Icon.Geometry = Geometry.Parse(
                "M4 4h7v5H4V4zm9 6h7v5h-7v-5zM4 15h7v5H4v-5zm7-7.5h2v5h5v2h-7v-7z");
            btn.ButtonMouseDown += (s, e) =>
            {
                e.Handled = true;
                Import();
            };
            return btn;
        }

        FrameworkElement CreateBackgroundButton()
        {
            var btn = new ToolbarImageButton
            {
                Label = "导图上背景",
                Tag = "mmapimportbg",
            };
            btn.Icon.Geometry = Geometry.Parse(
                "M4 4h7v5H4V4zm9 6h7v5h-7v-5zM4 15h7v5H4v-5zm7-7.5h2v5h5v2h-7v-7z");
            btn.ButtonMouseDown += (s, e) =>
            {
                e.Handled = true;
                ImportToBackground();
            };
            return btn;
        }

        void Import()
        {
            try
            {
                var dlg = GetService<IFileDialogService>();
                var path = dlg != null
                    ? dlg.OpenFile("选择 MindManager 导图文件", "MindManager 导图 (*.mmap)|*.mmap|所有文件 (*.*)|*.*")
                    : null;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                var doc = MmapParser.ParseFile(path);
                if (doc.Root == null)
                {
                    Notify("导图导入失败", doc.Error ?? "无法解析该文件", NotificationLevel.Error);
                    return;
                }

                var element = new MmapCanvas(doc);   // 交互式导图：保存态还原 + 点击徽标展开/收起
                int visible = element.VisibleTopicCount();

                var canvas = GetService<ICanvasElementService>();
                bool ok = canvas != null && canvas.InsertElement(element);
                if (!ok)
                {
                    Notify("导图导入失败", "当前页冻结或非白板状态，请切到白板后重试", NotificationLevel.Warning);
                    return;
                }

                // 画布跟随同步：漫游/双指时让导图像墨迹一样跟着画布走
                // （宿主漫游只变换笔迹与白名单元素，插件元素需自行从笔迹锚点反解矩阵跟随）
                CanvasSync.Attach(element, Log);
                _currentMap = element;

                Notify("导图导入完成",
                    string.Format("《{0}》：共 {1} 个主题，画布显示 {2} 个（其余收起，点 +/− 展开）",
                        doc.Root.Text, doc.TopicCount, visible),
                    NotificationLevel.Success);
                Log(string.Format("导入 {0} 成功：{1} topics", Path.GetFileName(path), doc.TopicCount));
            }
            catch (Exception ex)
            {
                LogError("导入异常", ex);
                Notify("导图导入异常", ex.Message, NotificationLevel.Error);
            }
        }

        /// <summary>
        /// 背景模式：导图全展开注入白板背景层（InjectBackgroundLayer）。
        /// 背景层不参与命中测试 → 导图纯展示（＋号不可用），笔迹直接写在图上方；
        /// 漫游/双指跟随由 CanvasSync 复用实现（背景层同样不在宿主跟随白名单内）。
        /// </summary>
        void ImportToBackground()
        {
            try
            {
                var dlg = GetService<IFileDialogService>();
                var path = dlg != null
                    ? dlg.OpenFile("选择 MindManager 导图文件（上背景）", "MindManager 导图 (*.mmap)|*.mmap|所有文件 (*.*)|*.*")
                    : null;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                var doc = MmapParser.ParseFile(path);
                if (doc.Root == null)
                {
                    Notify("导图上背景失败", doc.Error ?? "无法解析该文件", NotificationLevel.Error);
                    return;
                }

                var comp = GetService<ICanvasCompositionService>();
                if (comp == null)
                {
                    Notify("导图上背景失败", "宿主未提供画布合成服务", NotificationLevel.Error);
                    return;
                }

                comp.InjectBackgroundLayer(() =>
                {
                    // 全展开导图，Viewbox 等比适配宿主铺满的容器
                    var map = new MmapCanvas(doc, expandAll: true);
                    map.VisibleTopicCount();
                    var box = new Viewbox
                    {
                        Child = map,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(30),
                    };
                    var host = new Grid { Background = Brushes.Transparent };
                    host.Children.Add(box);
                    CanvasSync.Attach(host, Log);
                    Log(string.Format("背景导入 {0}：{1} topics 全展开", Path.GetFileName(path), doc.TopicCount));
                    return host;
                });

                Notify("导图已上背景",
                    string.Format("《{0}》全展开 {1} 个主题，可直接书写批注；漫游/双指随画布同步",
                        doc.Root.Text, doc.TopicCount),
                    NotificationLevel.Success);
            }
            catch (Exception ex)
            {
                LogError("背景导入异常", ex);
                Notify("导图上背景异常", ex.Message, NotificationLevel.Error);
            }
        }

        void Notify(string title, string msg, NotificationLevel level)
        {
            var svc = GetService<INotificationService>();
            if (svc != null) svc.Show(title, msg, level);
        }

        public override void Shutdown()
        {
            try
            {
                var hk = GetService<IHotkeyService>();
                if (hk != null)
                    foreach (var id in _hotkeyIds) hk.Unregister(id);
            }
            catch { }
            Log("mmap 导图导入已卸载");
            base.Shutdown();
        }
    }
}
