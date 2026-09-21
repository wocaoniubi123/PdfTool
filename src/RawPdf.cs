using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PdfTool
{
    // 一张图片在 PDF 里的原始信息
    internal sealed class RawImage
    {
        public int ObjNum;
        public string Filter;      // 主滤镜：DCTDecode / JPXDecode / FlateDecode / ...
        public bool MultiFilter;   // 多重滤镜（不支持原样导出）
        public byte[] Raw;         // 原始流字节（压缩状态，未解码）
        public int Width;
        public int Height;
        public int Bits;
        public string ColorSpace;  // DeviceGray / DeviceRGB / DeviceCMYK ...
        public int Comps;          // 分量数：1=Gray 3=RGB 4=CMYK，0=认不出
        public byte[] Palette;     // Indexed 调色板（原始字节）
        public int BaseComps;      // 调色板每项的分量数
        public bool HasPredictor;
        public int Predictor;
        public int Colors;
        public int Columns;
        public bool Invert;        // /Decode [1 0]
        public List<string> PreFilters = new List<string>();  // 前置解码器（按顺序）
        public List<string> PreParms = new List<string>();     // 各前置解码器对应的 DecodeParms
        public RawImage Mask;                                  // /SMask 指向的蒙版图（已解析）
        public int CcittK = 0;     // CCITT: K<0=G4, K=0=G3 1D
        public bool CcittBlackIs1 = false;
        public bool CcittByteAlign = false;
    }

    // 极简 PDF 解析：只为"按原始字节提取图片"。
    // 只认顶层对象（老式 xref 表 / 无对象流的文件，扫描 "N G obj" 即可）；
    // 看不懂的结构一律返回空，由调用方退回 pdfium 解码路径。
    internal sealed class RawPdf
    {
        private byte[] _b;
        private readonly Dictionary<int, List<int>> _objPos = new Dictionary<int, List<int>>();

        private RawPdf(byte[] b) { _b = b; }

        public static RawPdf Open(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            RawPdf p = new RawPdf(b);
            p.Scan();
            if (p._objPos.Count < 3) return null;
            p.ExpandObjectStreams();
            if (p._objPos.Count < 3) return null;
            return p;
        }

        // 把 /Type /ObjStm 里压缩存放的对象展开成普通对象文本，附加到缓冲末尾再重新扫描
        private void ExpandObjectStreams()
        {
            List<int> stms = new List<int>();
            foreach (KeyValuePair<int, List<int>> kv in _objPos)
            {
                int after;
                Dictionary<string, string> d = ObjectDict(kv.Key, out after);
                if (d == null) continue;
                string t;
                if (d.TryGetValue("Type", out t) && ResolveName(t) == "ObjStm") stms.Add(kv.Key);
            }
            if (stms.Count == 0) return;
            MemoryStream ms = new MemoryStream();
            ms.Write(_b, 0, _b.Length);
            ms.WriteByte(10);
            int added = 0;
            foreach (int s in stms)
            {
                int after;
                Dictionary<string, string> d = ObjectDict(s, out after);
                if (d == null) continue;
                byte[] raw = ReadStreamBytes(s);
                if (raw == null) continue;
                byte[] data = raw;
                string ft;
                if (d.TryGetValue("Filter", out ft) && ResolveName(ft) == "FlateDecode")
                {
                    data = RawExport.Inflate(raw);
                    if (data != null && d.ContainsKey("DecodeParms")) data = ApplyPredictor(d, data);
                }
                if (data == null) continue;
                string nv, fv;
                int N = d.TryGetValue("N", out nv) ? ResolveInt(nv, 0) : 0;
                int first = d.TryGetValue("First", out fv) ? ResolveInt(fv, 0) : 0;
                if (N <= 0 || first <= 0 || first >= data.Length) continue;
                List<int> nums = new List<int>();
                List<int> offs = new List<int>();
                int p = 0;
                for (int i = 0; i < N; i++)
                {
                    int a = ReadIntToken(data, ref p);
                    int b2 = ReadIntToken(data, ref p);
                    if (a < 0 || b2 < 0) break;
                    nums.Add(a);
                    offs.Add(first + b2);
                }
                for (int i = 0; i < nums.Count; i++)
                {
                    int from = offs[i];
                    int to = (i + 1 < offs.Count) ? offs[i + 1] : data.Length;
                    if (from < 0 || to > data.Length || to <= from) continue;
                    byte[] head = Encoding.ASCII.GetBytes(nums[i] + " 0 obj\n");
                    ms.Write(head, 0, head.Length);
                    ms.Write(data, from, to - from);
                    byte[] tail = Encoding.ASCII.GetBytes("\nendobj\n");
                    ms.Write(tail, 0, tail.Length);
                    added++;
                }
            }
            if (added == 0) return;
            _b = ms.ToArray();
            _objPos.Clear();
            Scan();
        }

        private int ReadIntToken(byte[] data, ref int p)
        {
            while (p < data.Length && (data[p] < 48 || data[p] > 57) && data[p] != 45) p++;
            if (p >= data.Length) return -1;
            int s = p;
            if (data[p] == 45) p++;
            while (p < data.Length && data[p] >= 48 && data[p] <= 57) p++;
            int v;
            return int.TryParse(Encoding.ASCII.GetString(data, s, p - s), out v) ? v : -1;
        }

        // 对象流可能带 PNG/TIFF 预测器
        private byte[] ApplyPredictor(Dictionary<string, string> d, byte[] data)
        {
            string dp;
            if (!d.TryGetValue("DecodeParms", out dp)) return data;
            Dictionary<string, string> pd = ParseDictFromText(dp.Trim());
            if (pd == null) return data;
            string v;
            RawImage tmp = new RawImage();
            tmp.Width = 1;
            tmp.Height = 0;
            if (pd.TryGetValue("Predictor", out v)) tmp.Predictor = ResolveInt(v, 1);
            else return data;
            if (tmp.Predictor < 2) return data;
            tmp.HasPredictor = true;
            if (pd.TryGetValue("Colors", out v)) tmp.Colors = ResolveInt(v, 1);
            if (pd.TryGetValue("Columns", out v)) tmp.Columns = ResolveInt(v, 0);
            if (pd.TryGetValue("BitsPerComponent", out v)) tmp.Bits = ResolveInt(v, 8);
            if (tmp.Columns <= 0) return data;
            int bits = tmp.Bits > 0 ? tmp.Bits : 8;
            int colors = tmp.Colors > 0 ? tmp.Colors : 1;
            tmp.Colors = colors;
            tmp.Bits = bits;
            tmp.Width = tmp.Columns;
            tmp.Height = 0;
            byte[] outData = RawExport.UndoPredictorAuto(data, tmp);
            return outData != null ? outData : data;
        }
        private void Scan()
        {
            int n = _b.Length;
            for (int i = 0; i < n - 4; i++)
            {
                if (_b[i] != 'o' || _b[i + 1] != 'b' || _b[i + 2] != 'j') continue;
                if (i > 0 && IsNameChar(_b[i - 1])) continue; // 排除 endobj 等
                int j = i - 1;
                while (j >= 0 && IsWs(_b[j])) j--;
                int genEnd = j;
                while (j >= 0 && _b[j] >= '0' && _b[j] <= '9') j--;
                if (j == genEnd) continue;
                while (j >= 0 && IsWs(_b[j])) j--;
                int numEnd = j;
                while (j >= 0 && _b[j] >= '0' && _b[j] <= '9') j--;
                if (j == numEnd) continue;
                int num = 0;
                bool ok = true;
                for (int k = j + 1; k <= numEnd; k++)
                {
                    if (num > 100000000) { ok = false; break; }
                    num = num * 10 + (_b[k] - '0');
                }
                if (!ok) continue;
                // 记录字典起点：跳过 " 0 obj"
                int p = numEnd + 1;
                while (p < n && IsWs(_b[p])) p++;
                while (p < n && _b[p] >= '0' && _b[p] <= '9') p++;
                while (p < n && IsWs(_b[p])) p++;
                if (p + 3 <= n && _b[p] == 'o' && _b[p + 1] == 'b' && _b[p + 2] == 'j') p += 3;
                List<int> list;
                if (!_objPos.TryGetValue(num, out list)) { list = new List<int>(); _objPos[num] = list; }
                list.Add(p);
            }
        }

        private static bool IsWs(byte c)
        {
            return c == 0 || c == 9 || c == 10 || c == 12 || c == 13 || c == 32;
        }

        private static bool IsNameChar(byte c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
        }

        // ---------- 基础解析 ----------

        private int SkipWsAndComments(int pos)
        {
            int n = _b.Length;
            while (pos < n)
            {
                byte c = _b[pos];
                if (IsWs(c)) { pos++; continue; }
                if (c == '%')
                {
                    while (pos < n && _b[pos] != '\n' && _b[pos] != '\r') pos++;
                    continue;
                }
                break;
            }
            return pos;
        }

        // 读一个值，返回它的原始文本（数组/字典会带上括号，引用是 "12 0 R"）
        private string ReadValueText(ref int pos)
        {
            pos = SkipWsAndComments(pos);
            if (pos >= _b.Length) return null;
            byte c = _b[pos];
            int start = pos;
            if (c == '<' && pos + 1 < _b.Length && _b[pos + 1] == '<')
            {
                int depth = 0;
                while (pos < _b.Length)
                {
                    if (_b[pos] == '<' && pos + 1 < _b.Length && _b[pos + 1] == '<') { depth++; pos += 2; continue; }
                    if (_b[pos] == '>' && pos + 1 < _b.Length && _b[pos + 1] == '>') { depth--; pos += 2; if (depth == 0) break; continue; }
                    pos++;
                }
            }
            else if (c == '[')
            {
                int depth = 0;
                while (pos < _b.Length)
                {
                    if (_b[pos] == '[') depth++;
                    else if (_b[pos] == ']') { depth--; if (depth == 0) { pos++; break; } }
                    else if (_b[pos] == '(') { pos = SkipLiteralString(pos); continue; }
                    pos++;
                }
            }
            else if (c == '(')
            {
                pos = SkipLiteralString(pos);
            }
            else if (c == '/')
            {
                pos++;
                while (pos < _b.Length && !IsWs(_b[pos]) && !IsDelim(_b[pos])) pos++;
            }
            else
            {
                while (pos < _b.Length && !IsWs(_b[pos]) && !IsDelim(_b[pos])) pos++;
                // 可能是 "12 0 R"
                int save = pos;
                int p2 = SkipWsAndComments(pos);
                if (p2 > pos && p2 < _b.Length && _b[p2] >= '0' && _b[p2] <= '9')
                {
                    int q = p2;
                    while (q < _b.Length && _b[q] >= '0' && _b[q] <= '9') q++;
                    int q2 = SkipWsAndComments(q);
                    if (q2 < _b.Length && _b[q2] == 'R')
                    {
                        pos = q2 + 1;
                    }
                    else pos = save;
                }
                else pos = save;
            }
            if (pos <= start) pos = start + 1;
            return Str(start, pos);
        }

        private int SkipLiteralString(int pos)
        {
            int depth = 1;
            pos++;
            while (pos < _b.Length && depth > 0)
            {
                if (_b[pos] == '\\') { pos += 2; continue; }
                if (_b[pos] == '(') depth++;
                else if (_b[pos] == ')') depth--;
                pos++;
            }
            return pos;
        }

        private static bool IsDelim(byte c)
        {
            return c == '/' || c == '[' || c == ']' || c == '<' || c == '>' || c == '(' || c == ')' || c == '{' || c == '}';
        }

        private string Str(int s, int e)
        {
            return Encoding.ASCII.GetString(_b, s, e - s);
        }

        private Dictionary<string, string> ParseDict(int pos, out int endPos)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            pos = SkipWsAndComments(pos);
            if (pos + 1 >= _b.Length || _b[pos] != '<' || _b[pos + 1] != '<') { endPos = pos; return null; }
            pos += 2;
            while (pos < _b.Length)
            {
                pos = SkipWsAndComments(pos);
                if (pos + 1 < _b.Length && _b[pos] == '>' && _b[pos + 1] == '>') { pos += 2; break; }
                if (pos >= _b.Length) break;
                if (_b[pos] != '/') { pos++; continue; }
                pos++;
                int ks = pos;
                while (pos < _b.Length && !IsWs(_b[pos]) && !IsDelim(_b[pos])) pos++;
                string key = Str(ks, pos);
                string val = ReadValueText(ref pos);
                d[key] = val;
            }
            endPos = pos;
            return d;
        }

        private Dictionary<string, string> ObjectDict(int objNum, out int afterDict)
        {
            afterDict = -1;
            List<int> list;
            if (!_objPos.TryGetValue(objNum, out list)) return null;
            Dictionary<string, string> first = null;
            int firstAfter = -1;
            foreach (int p in list)
            {
                int after;
                Dictionary<string, string> d = ParseDict(p, out after);
                if (d == null || d.Count == 0) continue;
                if (d.ContainsKey("Type"))   // 真对象一般都有 /Type，假匹配多半没有
                {
                    afterDict = after;
                    return d;
                }
                if (first == null) { first = d; firstAfter = after; }
            }
            afterDict = firstAfter;
            return first;
        }

        // 不要求是字典对象的位置查找（颜色空间可能是数组/名字对象）
        private bool TryRawPos(int objNum, out int pos)
        {
            pos = -1;
            List<int> list;
            if (!_objPos.TryGetValue(objNum, out list)) return false;
            foreach (int p in list)
            {
                int q = SkipWsAndComments(p);
                if (q >= _b.Length) continue;
                byte c = _b[q];
                if (c == (byte)47 || c == (byte)91 || c == (byte)60 || (c >= 48 && c <= 57))
                {
                    pos = q;
                    return true;
                }
            }
            return false;
        }

        // 取"能解析出字典"的那个位置（二进制流里可能有假匹配）
        private bool TryFirstValidPos(int objNum, out int pos)
        {
            pos = -1;
            List<int> list;
            if (!_objPos.TryGetValue(objNum, out list)) return false;
            foreach (int p in list)
            {
                int after;
                Dictionary<string, string> d = ParseDict(p, out after);
                if (d != null && d.Count > 0) { pos = p; return true; }
            }
            return false;
        }

        // 对象是数字时取值（引用也能跟）
        private int ResolveInt(string v, int fallback)
        {
            if (string.IsNullOrEmpty(v)) return fallback;
            v = v.Trim();
            int num, gen;
            if (TryParseRef(v, out num, out gen))
            {
                int after;
                Dictionary<string, string> o = null;
                int pos;
                if (TryFirstValidPos(num, out pos))
                {
                    pos = SkipWsAndComments(pos);
                    int s = pos;
                    while (pos < _b.Length && _b[pos] >= '0' && _b[pos] <= '9') pos++;
                    if (pos > s) return int.Parse(Str(s, pos));
                }
                return fallback;
            }
            int r;
            if (int.TryParse(v, out r)) return r;
            return fallback;
        }

        private static bool TryParseRef(string v, out int num, out int gen)
        {
            num = 0; gen = 0;
            string[] parts = v.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[2] != "R") return false;
            return int.TryParse(parts[0], out num) && int.TryParse(parts[1], out gen);
        }

        private string ResolveName(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            v = v.Trim();
            if (v.StartsWith("/")) return v.Substring(1);
            int num, gen;
            if (TryParseRef(v, out num, out gen))
            {
                int pos;
                if (TryFirstValidPos(num, out pos))
                {
                    pos = SkipWsAndComments(pos);
                    if (pos < _b.Length && _b[pos] == '/')
                    {
                        int s = ++pos;
                        while (pos < _b.Length && !IsWs(_b[pos]) && !IsDelim(_b[pos])) pos++;
                        return Str(s, pos);
                    }
                }
            }
            return v;
        }

        private List<string> SplitArray(string v)
        {
            List<string> items = new List<string>();
            if (string.IsNullOrEmpty(v)) return items;
            v = v.Trim();
            if (v.StartsWith("[")) v = v.Substring(1);
            if (v.EndsWith("]")) v = v.Substring(0, v.Length - 1);
            int depth = 0, start = 0;
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '[' || c == '<') depth++;
                else if (c == ']' || c == '>') depth--;
                else if (IsWs((byte)c) && depth == 0)
                {
                    if (i > start) items.Add(v.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (v.Length > start) items.Add(v.Substring(start));
            return items;
        }

        // ---------- 页面 -> 图片 ----------

        private List<int> PageObjects()
        {
            // 找根 Pages 节点（有 /Kids 且没有 /Parent），按顺序收集页面对象
            List<int> pages = new List<int>();
            int rootPages = -1;
            foreach (KeyValuePair<int, List<int>> kv in _objPos)
            {
                int after;
                Dictionary<string, string> d = ObjectDict(kv.Key, out after);
                if (d == null) continue;
                string t;
                if (d.TryGetValue("Type", out t) && ResolveName(t) == "Pages" && !d.ContainsKey("Parent"))
                {
                    rootPages = kv.Key;
                    break;
                }
            }
            if (rootPages >= 0) CollectPages(rootPages, pages, 0);
            if (pages.Count == 0)
            {
                // 退回：按对象号顺序列出所有 /Type /Page
                foreach (KeyValuePair<int, List<int>> kv in _objPos)
                {
                    int after;
                    Dictionary<string, string> d = ObjectDict(kv.Key, out after);
                    if (d == null) continue;
                    string t;
                    if (d.TryGetValue("Type", out t) && ResolveName(t) == "Page") pages.Add(kv.Key);
                }
                pages.Sort();
            }
            return pages;
        }

        // 把 "[ 671 0 R 13 0 R ]" 切成 "671 0 R"、"13 0 R" 这样的引用列表
        private List<string> SplitRefs(string v)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(v)) return result;
            v = v.Trim();
            if (v.StartsWith("[")) v = v.Substring(1);
            if (v.EndsWith("]")) v = v.Substring(0, v.Length - 1);
            string[] tok = v.Split(new char[] { (char)32, (char)9, (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < tok.Length; i++)
            {
                if (tok[i + 2] == "R") { result.Add(tok[i] + " " + tok[i + 1] + " R"); i += 2; }
            }
            return result;
        }

        private void CollectPages(int objNum, List<int> pages, int depth)
        {
            if (depth > 32) return;
            int after;
            Dictionary<string, string> d = ObjectDict(objNum, out after);
            if (d == null) return;
            string t;
            if (d.TryGetValue("Type", out t) && ResolveName(t) == "Page") { pages.Add(objNum); return; }
            string kids;
            if (!d.TryGetValue("Kids", out kids)) return;
            foreach (string k in SplitRefs(kids))
            {
                int num, gen;
                if (TryParseRef(k.Trim(), out num, out gen)) CollectPages(num, pages, depth + 1);
            }
        }

        private Dictionary<string, string> ResolveDict(string v, out int after)
        {
            after = -1;
            if (v == null) return null;
            v = v.Trim();
            if (v.StartsWith("<<"))
            {
                // 内联字典：重新定位到文件里解析（简化：用文本再解析一次）
                return ParseDictFromText(v);
            }
            int num, gen;
            if (TryParseRef(v, out num, out gen)) return ObjectDict(num, out after);
            return null;
        }

        private Dictionary<string, string> ParseDictFromText(string text)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            int pos = 0;
            byte[] tmp = Encoding.ASCII.GetBytes(text);
            byte[] save = _b;
            try
            {
                // 用一个临时缓冲解析（借用同一套逻辑）
                RawPdf tmpPdf = new RawPdf(tmp);
                return tmpPdf.ParseDict(0, out pos);
            }
            finally
            {
                save = null;
            }
        }

        public int ObjectCount { get { return _objPos.Count; } }

        // 诊断：统计对象类型分布，并给出几个样例
        public string DebugScan()
        {
            int pages = 0, pagestree = 0, images = 0, forms = 0, none = 0;
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<int, List<int>> kv in _objPos)
            {
                int after;
                Dictionary<string, string> d = ObjectDict(kv.Key, out after);
                if (d == null) { none++; continue; }
                string t = null, st = null;
                string v;
                if (d.TryGetValue("Type", out v)) t = ResolveName(v);
                if (d.TryGetValue("Subtype", out v)) st = ResolveName(v);
                if (t == "Page") pages++;
                else if (t == "Pages") pagestree++;
                else if (st == "Image") images++;
                else if (st == "Form") forms++;
                if (sb.Length < 400 && (t != null || st != null))
                    sb.Append(" | 对象").Append(kv.Key).Append(" Type=").Append(t).Append(" Subtype=").Append(st).Append(" keys=").Append(d.Count);
            }
            return "对象数=" + _objPos.Count + " 页面=" + pages + " 页树=" + pagestree
                + " 图片=" + images + " 表单=" + forms + " 解析失败=" + none + sb.ToString();
        }

        // 诊断用：页面数与某一页的资源概况
        public int PageCount { get { return PageObjects().Count; } }

        public string DebugPage(int pageIndex)
        {
            List<int> pages = PageObjects();
            if (pageIndex < 0 || pageIndex >= pages.Count) return "页码越界（共 " + pages.Count + " 页）";
            int after;
            Dictionary<string, string> page = ObjectDict(pages[pageIndex], out after);
            StringBuilder sb = new StringBuilder();
            sb.Append("页面对象号=").Append(pages[pageIndex]);
            foreach (KeyValuePair<string, string> kv in page)
                sb.Append(" | ").Append(kv.Key).Append("=").Append(kv.Value.Length > 60 ? kv.Value.Substring(0, 60) + "..." : kv.Value);
            return sb.ToString();
        }

        // 页里的图片对象（含 Form XObject 里的，最多钻 4 层）
        public List<RawImage> PageImages(int pageIndex)
        {
            List<RawImage> result = new List<RawImage>();
            List<int> pages = PageObjects();
            if (pageIndex < 0 || pageIndex >= pages.Count) return result;
            int after;
            Dictionary<string, string> page = ObjectDict(pages[pageIndex], out after);
            if (page == null) return result;
            string res;
            if (page.TryGetValue("Resources", out res))
            {
                Dictionary<string, string> rd = ResolveDict(res, out after);
                CollectFromResources(rd, result, 0);
            }
            return result;
        }

        private void CollectFromResources(Dictionary<string, string> res, List<RawImage> result, int depth)
        {
            if (res == null || depth > 4) return;
            string xo;
            if (!res.TryGetValue("XObject", out xo)) return;
            int after;
            Dictionary<string, string> xd = ResolveDict(xo, out after);
            if (xd == null) return;
            foreach (KeyValuePair<string, string> kv in xd)
            {
                int num, gen;
                if (!TryParseRef(kv.Value.Trim(), out num, out gen)) continue;
                Dictionary<string, string> od = ObjectDict(num, out after);
                if (od == null) continue;
                string st;
                if (!od.TryGetValue("Subtype", out st)) continue;
                string sub = ResolveName(st);
                if (sub == "Image")
                {
                    RawImage img = ReadImage(num, od);
                    if (img != null) result.Add(img);
                }
                else if (sub == "Form")
                {
                    string r2;
                    if (od.TryGetValue("Resources", out r2))
                    {
                        Dictionary<string, string> rd2 = ResolveDict(r2, out after);
                        CollectFromResources(rd2, result, depth + 1);
                    }
                }
            }
        }

        // 用 /Length 之前先验证：后面必须紧跟 endstream，否则说明 /Length 不可信
        private int StreamDataEnd(int dataStart, int declaredLen)
        {
            if (declaredLen >= 0 && dataStart + declaredLen <= _b.Length)
            {
                int p = dataStart + declaredLen;
                while (p < _b.Length && (_b[p] == 13 || _b[p] == 10 || _b[p] == 32)) p++;
                if (p + 9 <= _b.Length && Str(p, p + 9) == "endstream") return dataStart + declaredLen;
            }
            return FindEndstream(dataStart);
        }

        // 读某个对象的流字节（未解码）
        private byte[] ReadStreamBytes(int objNum)
        {
            int pos;
            if (!TryFirstValidPos(objNum, out pos)) return null;
            int after;
            Dictionary<string, string> d = ParseDict(pos, out after);
            if (d == null) return null;
            int len = -1;
            string lt;
            if (d.TryGetValue("Length", out lt)) len = ResolveInt(lt, -1);
            int p = SkipWsAndComments(after);
            if (p + 6 > _b.Length || Str(p, p + 6) != "stream") return null;
            p += 6;
            if (p < _b.Length && _b[p] == 13) p++;
            if (p < _b.Length && _b[p] == 10) p++;
            int start = p;
            int end = StreamDataEnd(start, len);
            if (end < 0) return null;
            if (end <= start) return null;
            byte[] data = new byte[end - start];
            Array.Copy(_b, start, data, 0, data.Length);
            return data;
        }

        // 从 token 列表里找出第一个 "N G R" 形式的引用
        private static string FindRefInTokens(List<string> tok, int from)
        {
            for (int i = from; i + 2 < tok.Count; i++)
            {
                if (tok[i + 2] == "R" && IsAllDigits(tok[i]) && IsAllDigits(tok[i + 1]))
                    return tok[i] + " " + tok[i + 1] + " R";
            }
            return null;
        }

        private static bool IsArrayDelim(char c)
        {
            return c == (char)32 || c == (char)9 || c == (char)13 || c == (char)10 || c == (char)47 || c == (char)91 || c == (char)93;
        }

        // 在数组文本里找 "N G R" 引用
        private static string FindRefInText(string txt, int from)
        {
            string[] tok = txt.Substring(from).Split(new char[] { (char)32, (char)9, (char)13, (char)10, (char)47, (char)91, (char)93 }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < tok.Length; i++)
            {
                if (tok[i + 2] == "R" && IsAllDigits(tok[i]) && IsAllDigits(tok[i + 1]))
                    return tok[i] + " " + tok[i + 1] + " R";
            }
            return null;
        }

        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++) if (s[i] < (char)48 || s[i] > (char)57) return false;
            return true;
        }

        // 解析颜色空间（支持名字 / 引用 / ICCBased / Indexed）
        private void ResolveColorInfo(string v, RawImage img, int depth)
        {
            img.Comps = 1;
            img.Palette = null;
            if (depth > 3 || v == null) return;
            v = v.Trim();
            if (v.StartsWith("/"))
            {
                string n = v.Substring(1);
                if (n == "DeviceCMYK" || n == "CMYK") img.Comps = 4;
                else if (n == "DeviceRGB" || n == "CalRGB" || n == "RGB") img.Comps = 3;
                else img.Comps = 1;
                return;
            }
            if (v.StartsWith("["))
            {
                int iIdx = v.IndexOf("Indexed");
                if (iIdx >= 0)
                {
                    // [/Indexed <基色空间> <最高索引> <调色板流引用>]，名字可能连写，用专用解析
                    int p = iIdx + 7;
                    while (p < v.Length && v[p] != '/') p++;
                    int s0 = p;
                    p++; // 跳过开头的斜杠
                    while (p < v.Length && !IsArrayDelim(v[p])) p++;
                    string baseCs = p > s0 ? v.Substring(s0, p - s0) : null;
                    RawImage baseImg = new RawImage();
                    if (baseCs != null) ResolveColorInfo(baseCs, baseImg, depth + 1);
                    img.BaseComps = baseImg.Comps > 0 ? baseImg.Comps : 1;
                    img.Comps = 1;
                    string refStr = FindRefInText(v, p);
                    int num0, gen0;
                    if (refStr != null && TryParseRef(refStr, out num0, out gen0))
                    {
                        byte[] raw = ReadStreamBytes(num0);
                        if (raw != null)
                        {
                            byte[] pal = raw;
                            int after;
                            Dictionary<string, string> sd = ObjectDict(num0, out after);
                            string ft;
                            if (sd != null && sd.TryGetValue("Filter", out ft) && ResolveName(ft) == "FlateDecode")
                                pal = RawExport.Inflate(raw);
                            img.Palette = pal;
                        }
                    }
                    return;
                }
                List<string> items = SplitArray(v);
                if (items.Count == 0) return;
                string head = ResolveName(items[0]);
                if (head == "Indexed" && items.Count >= 4)
                {
                    RawImage baseImg = new RawImage();
                    ResolveColorInfo(items[1], baseImg, depth + 1);
                    img.BaseComps = baseImg.Comps > 0 ? baseImg.Comps : 1;
                    img.Comps = 1; // 索引
                    int num, gen;
                    string refStr = FindRefInTokens(items, 3);
                    if (refStr != null && TryParseRef(refStr, out num, out gen))
                    {
                        byte[] raw = ReadStreamBytes(num);
                        if (raw != null)
                        {
                            byte[] pal = raw;
                            // 调色板流多半也是 Flate
                            int after;
                            Dictionary<string, string> sd = ObjectDict(num, out after);
                            string ft;
                            if (sd != null && sd.TryGetValue("Filter", out ft) && ResolveName(ft) == "FlateDecode")
                                pal = RawExport.Inflate(raw);
                            img.Palette = pal;
                        }
                    }
                    return;
                }
                if (head == "ICCBased" && items.Count > 1)
                {
                    int num, gen;
                    if (TryParseRef(items[1].Trim(), out num, out gen))
                    {
                        int after;
                        Dictionary<string, string> icc = ObjectDict(num, out after);
                        string nv;
                        if (icc != null && icc.TryGetValue("N", out nv)) img.Comps = ResolveInt(nv, 3);
                    }
                    return;
                }
                if (head == "CalRGB") { img.Comps = 3; return; }
                if (head == "CalGray") { img.Comps = 1; return; }
                ResolveColorInfo(items[0], img, depth + 1);
                return;
            }
            int num2, gen2;
            if (TryParseRef(v, out num2, out gen2))
            {
                int pos;
                if (!TryRawPos(num2, out pos)) return;
                if (pos < _b.Length && _b[pos] == '/')
                {
                    ResolveColorInfo(Str(pos, NextNameEnd(pos)), img, depth + 1);
                    return;
                }
                if (pos < _b.Length && _b[pos] == '[')
                {
                    string txt = ReadValueText(ref pos);
                    ResolveColorInfo(txt, img, depth + 1);
                    return;
                }
                if (pos + 1 < _b.Length && _b[pos] == '<' && _b[pos + 1] == '<')
                {
                    int after;
                    Dictionary<string, string> sd = ParseDict(pos, out after);
                    string nv;
                    if (sd != null && sd.TryGetValue("N", out nv)) img.Comps = ResolveInt(nv, 3);
                }
            }
        }

        // 颜色空间 -> 分量数（1/3/4），认不出来返回 0
        private int ResolveComps(string v, int depth)
        {
            if (depth > 3 || v == null) return 0;
            v = v.Trim();
            if (v.StartsWith("/"))
            {
                string n = v.Substring(1);
                if (n == "DeviceGray" || n == "CalGray" || n == "G") return 1;
                if (n == "DeviceRGB" || n == "CalRGB" || n == "RGB") return 3;
                if (n == "DeviceCMYK" || n == "CMYK") return 4;
                return 0;
            }
            if (v.StartsWith("["))
            {
                List<string> items = SplitArray(v);
                if (items.Count == 0) return 0;
                string head = ResolveName(items[0]);
                if (head == "ICCBased" && items.Count > 1)
                {
                    int num, gen;
                    if (TryParseRef(items[1].Trim(), out num, out gen))
                    {
                        int after;
                        Dictionary<string, string> icc = ObjectDict(num, out after);
                        string nv;
                        if (icc != null && icc.TryGetValue("N", out nv)) return ResolveInt(nv, 0);
                    }
                    return 0;
                }
                if (head == "CalRGB") return 3;
                if (head == "CalGray") return 1;
                if (head == "ICCBased") return 0;
                return ResolveComps(items[0], depth + 1);
            }
            int num2, gen2;
            if (TryParseRef(v, out num2, out gen2))
            {
                // 引用：可能是名字对象，也可能是数组/流（ICCBased）
                int pos;
                if (!TryFirstValidPos(num2, out pos)) return 0;
                pos = SkipWsAndComments(pos);
                if (pos < _b.Length && _b[pos] == '/')
                {
                    return ResolveComps(Str(pos, NextNameEnd(pos)), depth + 1);
                }
                if (pos < _b.Length && _b[pos] == '[')
                {
                    int after;
                    Dictionary<string, string> dummy = null;
                    // 数组对象：直接读取其文本
                    string txt = ReadValueTextFromPos(ref pos);
                    return ResolveComps(txt, depth + 1);
                }
                if (pos + 1 < _b.Length && _b[pos] == '<' && _b[pos + 1] == '<')
                {
                    int after;
                    Dictionary<string, string> sd = ParseDict(pos, out after);
                    string nv;
                    if (sd != null && sd.TryGetValue("N", out nv)) return ResolveInt(nv, 0);
                }
            }
            return 0;
        }

        private int NextNameEnd(int pos)
        {
            pos++;
            while (pos < _b.Length && !IsWs(_b[pos]) && !IsDelim(_b[pos])) pos++;
            return pos;
        }

        private string ReadValueTextFromPos(ref int pos)
        {
            string v = ReadValueText(ref pos);
            return v;
        }

        private RawImage ReadImage(int objNum, Dictionary<string, string> d)
        {
            int after;
            int pos;
            if (!TryFirstValidPos(objNum, out pos)) return null;
            Dictionary<string, string> dd = ParseDict(pos, out after);
            if (dd == null) return null;

            // /Length（可能是引用）
            string lenTxt;
            int len = -1;
            if (dd.TryGetValue("Length", out lenTxt)) len = ResolveInt(lenTxt, -1);

            // stream 起点
            int p = SkipWsAndComments(after);
            if (p + 6 > _b.Length) return null;
            if (!(Str(p, p + 6) == "stream")) return null;
            p += 6;
            if (p < _b.Length && _b[p] == '\r') p++;
            if (p < _b.Length && _b[p] == '\n') p++;
            int dataStart = p;
            int dataEnd;
            if (len >= 0 && dataStart + len <= _b.Length)
            {
                dataEnd = dataStart + len;
            }
            else
            {
                dataEnd = FindEndstream(dataStart);
                if (dataEnd < 0) return null;
            }
            if (dataEnd <= dataStart) return null;

            RawImage img = new RawImage();
            img.ObjNum = objNum;
            img.Raw = new byte[dataEnd - dataStart];
            Array.Copy(_b, dataStart, img.Raw, 0, img.Raw.Length);

            string v;
            if (d.TryGetValue("Width", out v)) img.Width = ResolveInt(v, 0);
            if (d.TryGetValue("Height", out v)) img.Height = ResolveInt(v, 0);
            if (d.TryGetValue("BitsPerComponent", out v)) img.Bits = ResolveInt(v, 8);
            else img.Bits = 8;
            if (d.TryGetValue("ColorSpace", out v))
            {
                img.ColorSpace = ResolveName(v);
                ResolveColorInfo(v, img, 0);
            }
            else
            {
                img.ColorSpace = "DeviceGray"; // PDF 缺省
                img.Comps = 1;
            }

            if (d.TryGetValue("Filter", out v))
            {
                List<string> fs = SplitArray(v);
                // 滤镜按从左到右依次解码，最后一个当作图片编码
                for (int i = 0; i < fs.Count; i++)
                {
                    string nm = ResolveName(fs[i]);
                    if (nm == null) continue;
                    if (i == fs.Count - 1) img.Filter = nm;
                    else img.PreFilters.Add(nm);
                }
                // 认识的前置解码器：ASCII85/ASCIIHex/Flate/LZW/RunLength
                for (int i = 0; i < img.PreFilters.Count; i++)
                {
                    string pf = img.PreFilters[i];
                    // LZW 解码器已用独立实现（Pillow 生成的 TIFF-LZW）交叉验证通过
                    if (pf != "ASCII85Decode" && pf != "ASCIIHexDecode" && pf != "FlateDecode"
                        && pf != "RunLengthDecode" && pf != "LZWDecode")
                        img.MultiFilter = true;
                }
            }
            string dp;
            if (d.TryGetValue("DecodeParms", out dp))
            {
                List<string> items = SplitArray(dp);
                string first = items.Count > 0 ? items[0].Trim() : dp.Trim();
                Dictionary<string, string> pd = first.StartsWith("<<") ? ParseDictFromText(first) : ResolveDict(first, out after);
                if (pd != null)
                {
                    img.HasPredictor = true;
                    string t;
                    if (pd.TryGetValue("Predictor", out t)) img.Predictor = ResolveInt(t, 1);
                    if (pd.TryGetValue("Colors", out t)) img.Colors = ResolveInt(t, 1);
                    if (pd.TryGetValue("Columns", out t)) img.Columns = ResolveInt(t, 0);
                    if (pd.TryGetValue("K", out t)) img.CcittK = ResolveInt(t, 0);
                    if (pd.TryGetValue("BlackIs1", out t)) img.CcittBlackIs1 = (t.Trim() == "true");
                    if (pd.TryGetValue("EncodedByteAlign", out t)) img.CcittByteAlign = (t.Trim() == "true");
                    if (pd.TryGetValue("Rows", out t) && img.Height <= 0) img.Height = ResolveInt(t, 0);
                }
            }
            if (d.TryGetValue("Decode", out v))
            {
                List<string> items = SplitArray(v);
                if (items.Count == 2 && items[0].Trim() == "1" && items[1].Trim() == "0") img.Invert = true;
            }
            // 透明蒙版：递归解析 /SMask 指向的图（蒙版不再有蒙版）
            string sm;
            if (d.TryGetValue("SMask", out sm))
            {
                int mn, mg;
                if (TryParseRef(sm.Trim(), out mn, out mg))
                {
                    int after2;
                    Dictionary<string, string> md = ObjectDict(mn, out after2);
                    if (md != null) img.Mask = ReadImage(mn, md);
                }
            }
            return img;
        }

        private int FindEndstream(int from)
        {
            for (int i = from; i < _b.Length - 9; i++)
            {
                if (_b[i] == 'e' && _b[i + 1] == 'n' && _b[i + 2] == 'd' && _b[i + 3] == 's'
                    && _b[i + 4] == 't' && _b[i + 5] == 'r' && _b[i + 6] == 'e' && _b[i + 7] == 'a' && _b[i + 8] == 'm')
                {
                    int e = i;
                    while (e > from && (_b[e - 1] == '\r' || _b[e - 1] == '\n')) e--;
                    return e;
                }
            }
            return -1;
        }
    }
}
