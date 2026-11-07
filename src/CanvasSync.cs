#nullable disable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Ink;

namespace InkCanvas.Plugin.MmapImport
{
    /// <summary>
    /// 画布跟随同步器 v2：让导图元素像宿主图片一样跟随画布漫游/双指手势。
    ///
    /// 原理：宿主漫游/双指只变换笔迹与白名单元素（Image/MediaElement/CanvasMediaControl），
    /// 插件元素不在名单。本同步器从 inkCanvas.Strokes 取锚点笔迹上的两个采样点，
    /// 逐帧反解画布增量矩阵（平移+旋转+缩放），累计后按宿主 ApplyMatrixTransformToImage
    /// 的同款方式（裸矩阵追加到 RenderTransform 组末尾）写入元素。
    ///
    /// v2 关键修复：
    /// 1) 宿主初始化 TransformGroup 是异步的（Loaded 优先级 BeginInvoke），Attach 时的挂载
    ///    会被宿主随后整组替换 → 跟随矩阵被丢弃。现改为逐帧自愈：每帧校验 _follow 是否在
    ///    当前 RenderTransform 组内，不在则重新挂载。
    /// 2) 去掉原点补偿，与宿主图片行为逐位对齐（平移精确；缩放轴与图片一致）。
    ///
    /// 生命周期：Attach 后逐帧工作；锚点笔迹被擦/缺失自动重选并重置基线；
    /// 元素脱离画布树自动暂停。
    /// </summary>
    public class CanvasSync
    {
        static readonly List<CanvasSync> _live = new List<CanvasSync>();

        /// <summary>翻页/清场时调用：重置所有同步器的累计矩阵与锚点基线。</summary>
        public static void ResetAll()
        {
            foreach (var s in _live)
            {
                s._canvasMatrix = Matrix.Identity;
                s._anchorA = s._anchorB = null;
                s._hasPrev = false;
                if (s._follow != null) s._follow.Matrix = Matrix.Identity;
            }
        }

        readonly FrameworkElement _element;
        System.Windows.Controls.InkCanvas _inkCanvas;
        MatrixTransform _follow;                 // 专用跟随矩阵（挂在 RenderTransform 组末尾）
        Stroke _anchorA, _anchorB;               // 锚点笔迹（Tier1 单笔迹 / Tier2 跨笔迹）
        string _anchorDesc = "无";
        int _idx0, _idx1;                        // Tier1 采样点索引（O(1) 采样）
        bool _crossStroke;                       // Tier2 跨笔迹模式
        int _validateCountdown;                  // Contains 线性扫描节流
        string _lastBadge;                       // 徽章文本去重
        (Point a, Point b) _prev;                // 上一帧锚点采样
        Matrix _canvasMatrix = Matrix.Identity;  // 累计画布矩阵
        bool _hasPrev;
        bool _hooked;
        int _orphanFrames;

        CanvasSync(FrameworkElement element) { _element = element; }

        /// <summary>插入成功后调用：启动逐帧同步（InkCanvas 祖先与变换组逐帧自愈挂载）。</summary>
        public static void Attach(FrameworkElement element, Action<string> log = null)
        {
            if (element == null) return;
            var sync = new CanvasSync(element);
            sync._log = log;
            sync.Log("同步器 Attach");
            _live.Add(sync);
            CompositionTarget.Rendering += sync.Tick;
        }

        // ---------------- 诊断徽章（v0.3.1 调试构建） ----------------
        Action<string> _log;
        Border _badge;
        TextBlock _badgeText;
        int _frame;
        DateTime _lastLog = DateTime.MinValue;
        string _lastApplied;   // 最近一次生效的矩阵摘要

        void EnsureBadge()
        {
            if (_badge != null) return;
            if (!(_element is Panel p)) return;
            _badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 20, 24, 34)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 6, 0, 0),
                IsHitTestVisible = false,
            };
            _badgeText = new TextBlock
            {
                Foreground = Brushes.LightGreen,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                FontSize = 12,
            };
            _badge.Child = _badgeText;
            Panel.SetZIndex(_badge, 999);
            Canvas.SetLeft(_badge, 6);
            Canvas.SetTop(_badge, 6);
            p.Children.Add(_badge);
        }

        void Log(string msg)
        {
            try { _log?.Invoke("[CanvasSync] " + msg); } catch { }
        }

        /// <summary>每帧：自愈挂载 → 采样锚点笔迹两点 → 反解增量矩阵 → 累计写入跟随矩阵。</summary>
        void Tick(object sender, EventArgs e)
        {
            try
            {
                // 元素脱离可视树（被替换/卸载）持续约 10 秒 → 自行注销
                if (VisualTreeHelper.GetParent(_element) == null && _element.Parent == null)
                {
                    if (++_orphanFrames > 600)
                    {
                        CompositionTarget.Rendering -= Tick;
                        _live.Remove(this);
                    }
                    return;
                }
                _orphanFrames = 0;
                _frame++;
                EnsureBadge();
                if ((_frame & 1) != 0) return;       // 性能：30fps 足够，减半 Tick 开销

                if (!_hooked) EnsureHook();
                if (!_hooked) { UpdateBadge("未找到画布"); return; }

                EnsureFollowInGroup();               // 逐帧校验：宿主替换 RenderTransform 后自动重挂

                var sc = _inkCanvas.Strokes;
                if (sc == null || sc.Count == 0)
                {
                    _anchorA = _anchorB = null; _hasPrev = false;
                    UpdateBadgeThrottled("待笔迹(画布空)");
                    return;
                }

                // ---- 锚点采样（v0.4.0 性能版：O(1) 索引采样 + 校验节流）----
                Point a0, a1;
                string anchorState;

                if (--_validateCountdown <= 0)
                {
                    // Contains 是线性扫描，30 帧校验一次即可；
                    // 锚点被删时点坐标停止更新 → inc=Identity 无副作用，兜底由校验兜住
                    _validateCountdown = 30;
                    if (_anchorA == null || !sc.Contains(_anchorA)
                        || (_anchorB != null && !sc.Contains(_anchorB)))
                    {
                        RePickAnchors(sc);
                        _hasPrev = false;
                        if (_anchorA == null)
                        {
                            Log(string.Format("无可用锚点 笔迹{0}条", sc.Count));
                            UpdateBadgeThrottled(string.Format("笔迹{0}条 无可用锚点", sc.Count));
                            return;
                        }
                        Log(string.Format("锚点重建: {0}", _anchorDesc));
                    }
                }

                if (!_crossStroke)
                {
                    var pts = _anchorA.StylusPoints;
                    if (pts.Count < 2 || _idx1 >= pts.Count)
                    { RePickAnchors(sc); _hasPrev = false; return; }
                    a0 = pts[_idx0].ToPoint();
                    a1 = pts[_idx1].ToPoint();       // O(1)：最远点索引在选锚时已缓存
                    if (Dist(a0, a1) < 6) { RePickAnchors(sc); _hasPrev = false; return; }
                    anchorState = "单笔迹";
                }
                else
                {
                    var pa = _anchorA.StylusPoints;
                    var pb = _anchorB.StylusPoints;
                    if (pa.Count < 1 || pb.Count < 1)
                    { RePickAnchors(sc); _hasPrev = false; return; }
                    a0 = pa[0].ToPoint();
                    a1 = pb[0].ToPoint();
                    if (Dist(a0, a1) < 6) { RePickAnchors(sc); _hasPrev = false; return; }
                    anchorState = "跨笔迹";
                }

                if (_hasPrev)
                {
                    Point b0 = _prev.a, b1 = _prev.b;   // 上一帧
                    var inc = SolveMatrix(b0, b1, a0, a1);
                    if (inc != Matrix.Identity)
                    {
                        // WPF 行向量约定：p·(A·B)=(p·A)·B → 累计 = 旧 × 新增量
                        _canvasMatrix = _canvasMatrix * inc;
                        _follow.Matrix = _canvasMatrix;   // 与宿主图片同款：裸矩阵追加
                        if (_frame % 10 == 0 || _lastApplied == null)
                            _lastApplied = string.Format("({0:F0},{1:F0})×{2:F2}",
                                _canvasMatrix.OffsetX, _canvasMatrix.OffsetY,
                                Math.Sqrt(Math.Abs(_canvasMatrix.M11 * _canvasMatrix.M22 - _canvasMatrix.M12 * _canvasMatrix.M21)));
                        if ((DateTime.Now - _lastLog).TotalMilliseconds > 800)
                        {
                            Log("矩阵生效 " + _lastApplied);
                            _lastLog = DateTime.Now;
                        }
                    }
                    UpdateBadgeThrottled("工作中 " + _lastApplied);
                }
                else
                {
                    UpdateBadgeThrottled(string.Format("基线采样 [{0}] 笔迹{1}条", anchorState, sc.Count));
                }
                _prev = (a0, a1);
                _hasPrev = true;
            }
            catch (Exception ex)
            {
                Log("Tick 异常: " + ex.Message);
            }
        }

        void UpdateBadge(string state)
        {
            if (_badgeText == null || state == _lastBadge) return;   // 文本去重，避免无谓重排
            _lastBadge = state;
            _badgeText.Text = "跟随 " + state;
        }

        /// <summary>徽章节流：最多每 30 帧（约 0.5s）刷新一次文本。</summary>
        void UpdateBadgeThrottled(string state)
        {
            if (_frame % 30 == 0) UpdateBadge(state);
        }

        void EnsureHook()
        {
            // 元素模式：InkCanvas 是祖先；背景模式：走窗口树搜索（背景层不在 InkCanvas 子树内）
            DependencyObject root = null;
            for (DependencyObject d = _element; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is System.Windows.Controls.InkCanvas ic) { _inkCanvas = ic; break; }
                root = d;                                       // 记录祖先链顶端（通常是 Window）
            }
            if (_inkCanvas == null && root != null)
                _inkCanvas = FindLargestInkCanvas(root);        // 背景/兜底：全树搜索取最大者
            if (_inkCanvas == null) return;

            Log(string.Format("已挂钩 InkCanvas {0}x{1} 笔迹{2}条",
                _inkCanvas.ActualWidth, _inkCanvas.ActualHeight, _inkCanvas.Strokes.Count));
            _follow = new MatrixTransform(Matrix.Identity);
            EnsureFollowInGroup();
            _hooked = true;
        }

        /// <summary>确保 _follow 位于元素当前 RenderTransform 组末尾；被宿主替换时自动重挂。</summary>
        void EnsureFollowInGroup()
        {
            var rt = _element.RenderTransform;
            if (rt is TransformGroup tg)
            {
                if (!tg.Children.Contains(_follow)) tg.Children.Add(_follow);
            }
            else
            {
                var tg2 = new TransformGroup();
                if (rt != null && !ReferenceEquals(rt, _follow)) tg2.Children.Add(rt);
                tg2.Children.Add(_follow);
                _element.RenderTransform = tg2;
            }
        }

        /// <summary>重选锚点。Tier1：单笔迹内"首点→最远点"跨度最大者（缓存索引）；Tier2：跨笔迹首点配对。</summary>
        void RePickAnchors(StrokeCollection sc)
        {
            _anchorA = _anchorB = null;
            _anchorDesc = "无";
            _idx0 = 0; _idx1 = 0; _crossStroke = false;

            Stroke best = null; double bestSpan = 6;
            int n = 0, bestFarIdx = 1;
            foreach (Stroke st in sc)
            {
                if (++n > 64) break;
                var pts = st.StylusPoints;
                if (pts.Count < 2) continue;
                var p0 = pts[0].ToPoint();
                double span = 0; int far = 1;
                for (int j = 1; j < pts.Count; j++)
                {
                    double d = Dist(p0, pts[j].ToPoint());
                    if (d > span) { span = d; far = j; }
                }
                if (span > bestSpan) { bestSpan = span; best = st; bestFarIdx = far; }
            }
            if (best != null)
            {
                _anchorA = best;
                _idx0 = 0; _idx1 = bestFarIdx;       // O(1) 采样索引
                _anchorDesc = string.Format("单笔迹 span={0:F0}", bestSpan);
                return;
            }

            // Tier2：全部短笔迹 → 取两条首点相距最大的笔迹
            Stroke first = null; Point firstPt = default;
            Stroke fa = null, fb = null; double bd = 6;
            foreach (Stroke st in sc)
            {
                var pts = st.StylusPoints;
                if (pts.Count < 1) continue;
                var p = pts[0].ToPoint();
                if (first == null) { first = st; firstPt = p; continue; }
                double d = Dist(firstPt, p);
                if (d > bd) { bd = d; fa = first; fb = st; }
            }
            if (fa != null && fb != null)
            {
                _anchorA = fa; _anchorB = fb;
                _crossStroke = true;
                _anchorDesc = string.Format("跨笔迹 d={0:F0}", bd);
            }
        }

        /// <summary>由两组点对反解相似变换（平移+旋转+等比缩放）：满足 M(bx)=ax, M(by)=ay。</summary>
        static Matrix SolveMatrix(Point b0, Point b1, Point a0, Point a1)
        {
            double dx = b1.X - b0.X, dy = b1.Y - b0.Y;      // 上一帧方向
            double dx2 = a1.X - a0.X, dy2 = a1.Y - a0.Y;    // 当前方向
            double lenPrev = Math.Sqrt(dx * dx + dy * dy);
            double lenCur = Math.Sqrt(dx2 * dx2 + dy2 * dy2);
            if (lenPrev < 1e-6 || lenCur < 1e-6) return Matrix.Identity;

            double cos = (dx * dx2 + dy * dy2) / (lenPrev * lenCur);
            double sin = (dx * dy2 - dy * dx2) / (lenPrev * lenCur);
            double scale = lenCur / lenPrev;

            // v0.3.3 关键修复：先合成完整矩阵（含平移），再判 Identity。
            // 旧版在此处提前 return Identity —— 纯平移时方向/长度不变，
            // 被误判为"没动"，漫游（纯平移）因此永不生效！
            var m = new Matrix(cos * scale, sin, -sin * scale, cos * scale, 0, 0);
            var v = new Vector(b0.X, b0.Y);
            var mapped = v * m;                              // 旋转缩放后的 b0
            m.OffsetX = a0.X - mapped.X;
            m.OffsetY = a0.Y - mapped.Y;

            if (Math.Abs(m.OffsetX) < 1e-6 && Math.Abs(m.OffsetY) < 1e-6 &&
                Math.Abs(m.M11 - 1) < 1e-9 && Math.Abs(m.M12) < 1e-9 &&
                Math.Abs(m.M21) < 1e-9 && Math.Abs(m.M22 - 1) < 1e-9)
                return Matrix.Identity;                      // 真正无任何变化
            return m;
        }

        /// <summary>背景模式兜底：在窗口可视树中找面积最大的 InkCanvas（主画布）。</summary>
        static System.Windows.Controls.InkCanvas FindLargestInkCanvas(DependencyObject root)
        {
            System.Windows.Controls.InkCanvas best = null;
            double bestArea = 0;
            void Walk(DependencyObject d)
            {
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is System.Windows.Controls.InkCanvas ic)
                    {
                        double a = ic.ActualWidth * ic.ActualHeight;
                        if (a > bestArea) { bestArea = a; best = ic; }
                    }
                    Walk(c);
                }
            }
            Walk(root);
            return best;
        }

        static double Dist(Point p, Point q)
        {
            double dx = p.X - q.X, dy = p.Y - q.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
