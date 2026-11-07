#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using InkCanvas.Plugin.MmapImport;
using WPath = System.Windows.Shapes.Path;

namespace InkCanvas.Plugin.MmapImport
{
    /// <summary>带可见性/展开状态的运行时节点。</summary>
    public class MapVM
    {
        public MmapNode Src;
        public MapVM Parent;
        public List<MapVM> Children = new List<MapVM>();
        public bool Open = true;            // 自身的子树是否展开（初始 = !Src.Collapsed）
        public bool SavedVisible = true;    // 文件保存态是否可见（祖先全部未折叠）
        public double X, Y;                 // 当前布局坐标（卡片中心）
        public double CardW, CardH;         // 卡片尺寸（绘制后回填）
        public bool SideRight = true;       // 相对父链的展开侧
        public FrameworkElement View;       // 主题卡片视图
        public FrameworkElement Badge;      // +/− 徽标视图
        public bool HasKids => Children.Count > 0;

        /// <summary>自身是否应当显示：所有祖先均 Open（祖先的 Src.Collapsed 或运行时收起都会隐藏后代）。</summary>
        public bool Visible
        {
            get
            {
                for (var p = Parent; p != null; p = p.Parent)
                    if (!p.Open) return false;
                return true;
            }
        }

        public IEnumerable<MapVM> Walk() { yield return this; foreach (var c in Children) foreach (var d in c.Walk()) yield return d; }
    }

    /// <summary>
    /// 交互式思维导图画布 v0.2：
    ///   · MindManager 风格连线（父卡边缘 → 子卡边缘的 S 曲线，按分支 LineColor 着色）
    ///   · 主题卡片内嵌图片（备注大图 / 行内小图标，EMF 除外）
    ///   · 还原 ap:Relationship 关系线（虚线弧）
    ///   · 折叠态还原 + 点击 +/− 徽标展开收起
    /// </summary>
    public class MmapCanvas : Canvas
    {
        const double HGap = 64;       // 自动布局父子水平间距
        const double VGap = 10;       // 自动布局兄弟垂直间距
        const double CardH0 = 34;     // 卡片高度估算值
        static readonly Brush DefaultLineBrush = MakeFrozen(0x6B, 0x77, 0x8C);
        static readonly Brush RelationBrush = MakeFrozenBrush(220, 0x8A, 0x7B, 0xD0);
        static readonly Brush FloatBgBrush = MakeFrozenBrush(235, 255, 246, 179);
        static readonly Brush CardBgBrush = MakeFrozenBrush(242, 255, 255, 255);
        static readonly Brush CardBorderBrush = MakeFrozen(0x5B, 0x64, 0x78);
        static readonly Brush BadgeBrush = MakeFrozen(0x2F, 0x6F, 0xED);

        static Brush MakeFrozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br;
        }
        static Brush MakeFrozenBrush(byte a, byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br;
        }
        const double NoteImgMaxW = 220;   // 备注大图最大显示宽
        const double InlineIconH = 26;    // 行内小图标显示高

        readonly MapVM _root;
        readonly MmapDocument _doc;
        readonly Canvas _lineLayer = new Canvas { IsHitTestVisible = false };
        readonly Canvas _nodeLayer = new Canvas();

        public MmapCanvas(MmapDocument doc, bool expandAll = false)
        {
            _doc = doc;
            _expandAll = expandAll;
            _root = BuildVM(doc.Root, null);
            foreach (var c in _root.Children)
                c.SideRight = c.Src.X > 0 || (c.Src.X == 0 && c.Src.Y >= 0);
            Children.Add(_lineLayer);
            Children.Add(_nodeLayer);
            Relayout();
        }

        readonly bool _expandAll;

        /// <summary>当前可见主题数（初始 = 保存态可见数）。</summary>
        public int VisibleTopicCount() => _root.Walk().Count(n => n.Visible);

        MapVM BuildVM(MmapNode src, MapVM parent)
        {
            var vm = new MapVM
            {
                Src = src,
                Parent = parent,
                X = src.X,
                Y = src.Y,
                // 忽略根自身的折叠标记；背景模式(expandAll)全展开
                Open = parent == null ? true : (_expandAll || !src.Collapsed),
                SavedVisible = parent == null || (parent.SavedVisible && !parent.Src.Collapsed),
            };
            foreach (var c in src.Children) vm.Children.Add(BuildVM(c, vm));
            return vm;
        }

        // ---------------- 布局 ----------------

        void Relayout()
        {
            foreach (var n in _root.Walk())
                if (n.View == null) BuildCard(n);      // 阶段1：建卡测量（只做一次）

            _root.X = 0; _root.Y = 0;
            foreach (var n in _root.Walk().Where(v => v.Open && v.HasKids).ToList())
                LayoutChildren(n);                     // 阶段2：先父后子

            DrawAll();
        }

        /// <summary>布局 n 的直接子题：纵向按子树高度均分堆叠，横向排在父卡外侧。</summary>
        void LayoutChildren(MapVM n)
        {
            if (!n.Open || !n.HasKids) return;
            int side = n.SideRight ? 1 : -1;
            double total = n.Children.Sum(k => EstSubH(k)) + VGap * (n.Children.Count - 1);
            double cursor = n.Y - total / 2;

            foreach (var k in n.Children)
            {
                double h = EstSubH(k);
                k.Y = cursor + h / 2;
                cursor += h + VGap;
                k.X = n.X + side * (n.CardW / 2 + HGap + k.CardW / 2);
                k.SideRight = n.SideRight;
                LayoutChildren(k);
            }
        }

        /// <summary>节点子树布局高度（收起或叶子 = 单卡高）。</summary>
        double EstSubH(MapVM n)
        {
            if (!n.Open || !n.HasKids) return Math.Max(n.CardH, CardH0);
            double sum = n.Children.Sum(c => EstSubH(c)) + VGap * (n.Children.Count - 1);
            return Math.Max(sum, Math.Max(n.CardH, CardH0));
        }

        // ---------------- 绘制 ----------------

        void DrawAll()
        {
            _lineLayer.Children.Clear();
            _nodeLayer.Children.Clear();

            var visible = _root.Walk().Where(v => v.Visible).ToList();

            // 先算包围盒与平移量，所有元素统一用平移后坐标绘制（v0.1 连线错位就是这个顺序问题）
            double minX = visible.Min(v => v.X - v.CardW / 2), maxX = visible.Max(v => v.X + v.CardW / 2);
            double minY = visible.Min(v => v.Y - v.CardH / 2), maxY = visible.Max(v => v.Y + v.CardH / 2);
            double pad = 90;
            double dx = pad - minX, dy = pad - minY;
            Width = Math.Ceiling((maxX - minX) + pad * 2);
            Height = Math.Ceiling((maxY - minY) + pad * 2);

            // 1) MindManager 风格连线（父卡边缘 → 子卡边缘 S 曲线）
            foreach (var n in visible.Where(v => v.Parent != null))
                AddBranchLine(n.Parent, n, dx, dy, n.Src.IsFloating);

            // 2) 关系线（ap:Relationship，虚线弧）
            foreach (var rel in _doc.Relationships)
            {
                var a = visible.FirstOrDefault(v => v.Src.OId == rel.FromOId);
                var b = visible.FirstOrDefault(v => v.Src.OId == rel.ToOId);
                if (a != null && b != null)
                    AddRelationship(a, b, dx, dy);
            }

            // 3) 卡片
            foreach (var n in visible)
            {
                Canvas.SetLeft(n.View, n.X + dx - n.CardW / 2);
                Canvas.SetTop(n.View, n.Y + dy - n.CardH / 2);
                Panel.SetZIndex(n.View, 1);
                _nodeLayer.Children.Add(n.View);
            }

            // 4) 折叠徽标（Clear 后必须重新挂载，否则首次开合后全部徽标从可视树消失）
            foreach (var n in visible.Where(v => v.HasKids))
            {
                if (n.Badge == null) AddBadge(n);
                if (!_nodeLayer.Children.Contains(n.Badge)) _nodeLayer.Children.Add(n.Badge);
                double side = n.SideRight ? 1 : -1;
                Canvas.SetLeft(n.Badge, n.X + dx + side * (n.CardW / 2 + 15) - 13);
                Canvas.SetTop(n.Badge, n.Y + dy - 13);
            }
        }

        /// <summary>MindManager 风格分支连线：父卡近端边缘 → 子卡近端边缘的横向 S 曲线。</summary>
        void AddBranchLine(MapVM p, MapVM c, double dx, double dy, bool dashed)
        {
            int side = c.SideRight ? 1 : -1;
            double sx = p.X + dx + side * p.CardW / 2;
            double ex = c.X + dx - side * c.CardW / 2;
            double sy = p.Y + dy, ey = c.Y + dy;

            var fig = new PathFigure { StartPoint = new Point(sx, sy) };
            fig.Segments.Add(new BezierSegment(
                new Point(sx + (ex - sx) * 0.45, sy),
                new Point(ex - (ex - sx) * 0.45, ey),
                new Point(ex, ey), true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);

            var brush = BrushFromArgb(c.Src.LineColor)
                        ?? BrushFromArgb(p.Src.LineColor)
                        ?? DefaultLineBrush;
            var path = new WPath
            {
                Data = geo,
                Stroke = brush,
                StrokeThickness = 2.4,
                StrokeDashArray = dashed ? new DoubleCollection { 4, 3 } : null,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            Panel.SetZIndex(path, 0);
            _lineLayer.Children.Add(path);
        }

        /// <summary>关系线：中心到中心的虚线弧（向侧上方拱起，避免压住卡片）。</summary>
        void AddRelationship(MapVM a, MapVM b, double dx, double dy)
        {
            double ax = a.X + dx, ay = a.Y + dy, bx = b.X + dx, by = b.Y + dy;
            double mx = (ax + bx) / 2, my = (ay + by) / 2;
            // 垂直方向偏移形成拱形
            double nx = -(by - ay), ny = (bx - ax);
            double len = Math.Sqrt(nx * nx + ny * ny);
            if (len < 1) return;
            double bow = Math.Min(46, len * 0.18);
            double cx = mx + nx / len * bow, cy = my + ny / len * bow;

            var fig = new PathFigure { StartPoint = new Point(ax, ay) };
            fig.Segments.Add(new QuadraticBezierSegment(new Point(cx, cy), new Point(bx, by), true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            var path = new WPath
            {
                Data = geo,
                Stroke = RelationBrush,
                StrokeThickness = 1.8,
                StrokeDashArray = new DoubleCollection { 5, 3 },
            };
            Panel.SetZIndex(path, 0);
            _lineLayer.Children.Add(path);
        }

        Border BuildCard(MapVM n)
        {
            var src = n.Src;
            double size = double.IsNaN(src.FontSize) ? 13 : src.FontSize;
            if (n.Parent == null) size = Math.Max(size, 20);

            var panel = new StackPanel();

            // 行内小图标（OneImage 的 PNG， MindManager 里是主题旁的小图标）
            foreach (var img in src.Images.Where(i => !i.IsNoteImage))
            {
                try
                {
                    var bmp = LoadBitmap(img.Data);
                    if (bmp == null) continue;
                    panel.Children.Add(new Image
                    {
                        Source = bmp,
                        Height = InlineIconH,
                        Width = InlineIconH * bmp.PixelWidth / Math.Max(1, bmp.PixelHeight),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(2, 2, 0, 0),
                    });
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(src.Text))
                panel.Children.Add(new TextBlock
                {
                    Text = src.Text,
                    FontFamily = new FontFamily("微软雅黑"),
                    FontSize = size,
                    FontWeight = (src.Bold || n.Parent == null) ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = BrushFromArgb(src.FontColor) ?? Brushes.Black,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 240,
                    TextAlignment = TextAlignment.Center,
                });

            // 备注大图（NotesGroup 里的内容图，MindManager 备注面板里的图）
            foreach (var img in src.Images.Where(i => i.IsNoteImage))
            {
                try
                {
                    var bmp = LoadBitmap(img.Data);
                    if (bmp == null) continue;
                    double w = img.Width > 0 ? Math.Min(img.Width, NoteImgMaxW) : NoteImgMaxW;
                    w = Math.Max(w, 120);
                    panel.Children.Add(new Image
                    {
                        Source = bmp,
                        Width = w,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 4, 0, 2),
                    });
                }
                catch { }
            }

            var border = new Border
            {
                Child = panel,
                Background = BrushFromArgb(src.FillColor)
                             ?? (src.IsFloating ? FloatBgBrush : CardBgBrush),
                BorderBrush = BrushFromArgb(src.LineColor) ?? CardBorderBrush,
                BorderThickness = new Thickness(src.IsFloating ? 1 : 1.6),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 4, 9, 4),
            };
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            border.Width = Math.Ceiling(border.DesiredSize.Width);
            border.Height = Math.Ceiling(border.DesiredSize.Height);
            n.View = border;
            n.CardW = border.Width;
            n.CardH = border.Height;
            return border;
        }

        static BitmapImage LoadBitmap(byte[] data)
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(data);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        void AddBadge(MapVM n)
        {
            var b = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = BadgeBrush,
                Child = new System.Windows.Controls.TextBlock
                {
                    Text = n.Open ? "−" : "+",
                    Foreground = Brushes.White,
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center,
                },
                Cursor = Cursors.Hand,
                ToolTip = n.Open ? "收起分支" : $"展开 {n.Children.Count} 个子主题",
            };
            var target = n;
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; target.Open = !target.Open; Relayout(); };
            n.Badge = b;
            Panel.SetZIndex(b, 2);
            _nodeLayer.Children.Add(b);
        }

        static Brush BrushFromArgb(string argb)
        {
            if (string.IsNullOrWhiteSpace(argb)) return null;
            var s = argb.Trim();
            if (s.Length != 8) return null;
            try
            {
                var brush = new SolidColorBrush(Color.FromArgb(
                    Convert.ToByte(s.Substring(0, 2), 16),
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16),
                    Convert.ToByte(s.Substring(6, 2), 16)));
                brush.Freeze();   // 跨线程可用 + 渲染优化
                return brush;
            }
            catch { return null; }
        }
    }
}
