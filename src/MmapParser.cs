#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace InkCanvas.Plugin.MmapImport
{
    /// <summary>从 .mmap 提取出的导图节点（绝对坐标已按父子偏移累加）。</summary>
    public class MmapNode
    {
        public string OId;
        public string Text;
        public double X, Y;                 // 累加后的绝对坐标（根为原点）
        public bool IsFloating;
        public bool Collapsed;
        public string FillColor;            // "ffaabbcc" 或 null
        public string LineColor;
        public string FontColor;
        public bool Bold;
        public double FontSize = double.NaN;
        public List<MmapImage> Images = new List<MmapImage>();
        public List<MmapNode> Children = new List<MmapNode>();
    }

    /// <summary>主题挂载的图片（备注大图或行内小图标）。EMF 无法解码，已在解析期丢弃。</summary>
    public class MmapImage
    {
        public byte[] Data;                 // PNG/JPEG 原始字节
        public double Width, Height;        // 文件记录的显示尺寸（单位≈px）
        public bool IsNoteImage;            // true=备注大图（显示在文字下方），false=行内小图标
    }

    /// <summary>关系线（两个主题 OId 之间的虚线连接）。</summary>
    public class MmapRelationship
    {
        public string FromOId, ToOId;
    }

    /// <summary>解析结果：根节点、关系线列表与包围盒。</summary>
    public class MmapDocument
    {
        public MmapNode Root;
        public List<MmapRelationship> Relationships = new List<MmapRelationship>();
        public double MinX, MinY, MaxX, MaxY;
        public int TopicCount;
        public string Error;
    }

    /// <summary>
    /// MindManager .mmap 解析器（v3）：ZIP → Document.xml → 层级树。
    /// 提取：文本/颜色/字号/加粗/折叠态/浮动批注/坐标/主题图片/备注图片/关系线。
    /// 图片 URI 形如 "mmarch://bin/xxx.bin"，直接从 ZIP 内 bin/ 目录取字节。
    /// </summary>
    public static class MmapParser
    {
        const string Ap = "{http://schemas.mindjet.com/MindManager/Application/2003}";
        const string Cor = "{http://schemas.mindjet.com/MindManager/Core/2003}";

        public static MmapDocument ParseFile(string path)
        {
            var doc = new MmapDocument { MinX = 0, MinY = 0, MaxX = 0, MaxY = 0 };
            try
            {
                using var zip = ZipFile.OpenRead(path);
                var entry = zip.GetEntry("Document.xml");
                if (entry == null) { doc.Error = "缺少 Document.xml（不是 MindManager 导图？）"; return doc; }
                XDocument x;
                using (var s = entry.Open())
                    x = XDocument.Load(s);
                _zip = zip;                                          // 供 ReadTopic 解析期即时取图

                var one = x.Root == null ? null : x.Root.Element(Ap + "OneTopic");
                var rootTopic = one == null ? null : one.Element(Ap + "Topic");
                if (rootTopic == null) { doc.Error = "Document.xml 中没有根主题"; return doc; }

                doc.Root = ReadTopic(rootTopic, 0, 0, false, doc);

                // 关系线：两个 ConnectionGroup 各含一个 ObjectReference 指向主题 OId
                foreach (var rel in x.Root.Descendants(Ap + "Relationship"))
                {
                    var refs = rel.Descendants(Ap + "ObjectReference")
                                  .Select(o => (string)o.Attribute("OIdRef")).Take(2).ToList();
                    if (refs.Count == 2)
                        doc.Relationships.Add(new MmapRelationship { FromOId = refs[0], ToOId = refs[1] });
                }

                doc.TopicCount = MmapParser.Walk(doc.Root).Count();
                _zip = null;                                         // 解析结束，释放 zip 上下文
            }
            catch (Exception ex)
            {
                doc.Error = ex.Message;
            }
            return doc;
        }

        static MmapNode ReadTopic(XElement t, double px, double py, bool floating, MmapDocument doc)
        {
            doc.TopicCount++;

            var off = t.Element(Ap + "Offset");
            double cx = px, cy = py;
            if (off != null)
            {
                cx = px + ParseD(off.Attribute("CX")?.Value);
                cy = py + ParseD(off.Attribute("CY")?.Value);
            }

            var col = t.Element(Ap + "Color");
            var txt = t.Element(Ap + "Text");
            var font = txt == null ? null : txt.Element(Ap + "Font");

            var n = new MmapNode
            {
                OId = (string)t.Attribute("OId"),
                Text = GetText(t),
                X = cx,
                Y = cy,
                IsFloating = floating,
                Collapsed = ((string)t.Element(Ap + "TopicViewGroup")?.Element(Ap + "Collapsed")
                                ?.Attribute("Collapsed")) == "true",   // 缺省=未折叠
                FillColor = col?.Attribute("FillColor")?.Value,
                LineColor = col?.Attribute("LineColor")?.Value,
                FontColor = font?.Attribute("Color")?.Value,
                Bold = ((string)font?.Attribute("Bold") ?? "") == "true",
                FontSize = ParseD(font?.Attribute("Size")?.Value),
            };

            doc.MinX = Math.Min(doc.MinX, n.X); doc.MaxX = Math.Max(doc.MaxX, n.X);
            doc.MinY = Math.Min(doc.MinY, n.Y); doc.MaxY = Math.Max(doc.MaxY, n.Y);

            // ---- 主题行内图片（ap:OneImage/ap:Image）----
            foreach (var img in t.Elements(Ap + "OneImage").Descendants(Ap + "Image"))
                AddImage(n, zip_of(doc), img, false);

            // ---- 备注图片（ap:NotesGroup/ap:NotesData）----
            var notes = t.Element(Ap + "NotesGroup");
            if (notes != null)
                foreach (var nd in notes.Elements(Ap + "NotesData"))
                {
                    var uriEl = nd.Element(Cor + "Uri");
                    if (uriEl == null) continue;
                    var bin = NormalizeUri(uriEl.Value);
                    if (bin == null) continue;
                    var img = LoadImage(zip_of(doc), bin, 0, 0, true);
                    if (img != null) n.Images.Add(img);
                }

            // ---- 子主题 / 浮动主题 ----
            var subs = t.Element(Ap + "SubTopics");
            if (subs != null)
                foreach (var c in subs.Elements(Ap + "Topic"))
                    n.Children.Add(ReadTopic(c, n.X, n.Y, false, doc));

            var floats = t.Element(Ap + "FloatingTopics");
            if (floats != null)
                foreach (var c in floats.Elements(Ap + "Topic"))
                    n.Children.Add(ReadTopic(c, n.X, n.Y, true, doc));

            return n;
        }

        // MmapDocument 不持有 zip 引用；图片在解析期即时加载，这里用线程本地传递 zip 上下文
        [ThreadStatic] static ZipArchive _zip;
        static ZipArchive zip_of(MmapDocument doc) => _zip;

        static void AddImage(MmapNode n, ZipArchive zip, XElement img, bool isNote)
        {
            if (zip == null) return;
            var uriEl = img.Element(Ap + "ImageData")?.Element(Cor + "Uri");
            if (uriEl == null) return;
            var bin = NormalizeUri(uriEl.Value);
            if (bin == null) return;
            var size = img.Element(Ap + "ImageSize");
            double w = ParseD(size?.Attribute("Width")?.Value);
            double h = ParseD(size?.Attribute("Height")?.Value);
            var mi = LoadImage(zip, bin, w, h, isNote);
            if (mi != null) n.Images.Add(mi);
        }

        static string GetText(XElement topic)
        {
            var t = topic.Element(Ap + "Text");
            if (t == null)
                t = topic.Element(Ap + "TitleGroup")?.Element(Cor + "Text");
            if (t == null)
                t = topic.Element(Ap + "Title")?.Element(Cor + "Text");
            if (t == null) return null;
            return ((string)t.Attribute("PlainText") ?? string.Concat(t.DescendantNodes().OfType<XText>().Select(x => x.Value))).Trim();
        }

        static double ParseD(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return double.NaN;
            s = s.TrimEnd('.');                                  // MindManager 数字带尾点，如 "12."
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        }

        /// <summary>把 "mmarch://bin/xxx.bin" 归一化为 ZIP 内路径 "bin/xxx.bin"。</summary>
        static string NormalizeUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            uri = uri.Replace("mmarch://", "");
            return uri.StartsWith("bin/") ? uri : null;
        }

        /// <summary>读取图片：PNG/JPEG 返回字节，EMF 等无法解码的类型返回 null。</summary>
        static MmapImage LoadImage(ZipArchive zip, string binPath, double w, double h, bool isNote)
        {
            try
            {
                var e = zip.GetEntry(binPath);
                if (e == null) return null;
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var data = ms.ToArray();
                if (data.Length < 4) return null;
                bool png = data[0] == 0x89 && data[1] == 0x50;                    // \x89PNG
                bool jpg = data[0] == 0xFF && data[1] == 0xD8;                    // FFD8
                if (!png && !jpg) return null;                                     // EMF/其他：跳过
                return new MmapImage { Data = data, Width = w, Height = h, IsNoteImage = isNote };
            }
            catch { return null; }
        }

        /// <summary>遍历全部节点（含浮动）。</summary>
        public static IEnumerable<MmapNode> Walk(MmapNode n)
        {
            if (n == null) yield break;
            yield return n;
            foreach (var c in n.Children)
                foreach (var d in Walk(c)) yield return d;
        }
    }
}
