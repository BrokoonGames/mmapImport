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
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InkCanvas.Plugin.MmapImport
{
    [PluginEntrance]
    public class MmapImportPlugin : PluginBase
    {
        public override void Initialize(IPluginHost host, IServiceCollection services)
        {
            base.Initialize(host, services);

            // 翻页/换页时重置画布跟随矩阵（新页面 = 全新画布空间）
            var events = GetService<IEventService>();
            if (events != null) events.WhiteboardPageChanged += (_, __) => CanvasSync.ResetAll();

            // 按钮 1：导图作为可交互元素上画布（可点 +/− 展开收起、可拖动）
            host.RegisterBoardToolbarItem(new PluginToolbarItemInfo
            {
                Id = "top.brokoon.mmapimport.button",
                DisplayName = "导入导图",
                Description = "选择 MindManager .mmap 导图文件导入白板：还原层级、配色、折叠态，支持点击展开分支",
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

            Log(string.Format("{0} (mmap 导图导入) 已初始化", Name));
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
            Log("mmap 导图导入已卸载");
            base.Shutdown();
        }
    }
}
