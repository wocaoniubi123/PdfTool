using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PdfTool
{
    // 阅读进度：记住每个文件上次看到第几页，下次打开自动跳回
    // 两级身份：路径（快、免费）→ 内容指纹（兜底：改名 / 换目录后仍认得）
    // 存储：exe 旁边的 PdfTool.pos.ini，每行 "P|H \t 键 \t 页码 \t 时间戳"
    //       制表符分隔（Windows 路径里不可能有制表符，不需要转义）
    internal static class Positions
    {
        private const int MaxEntries = 400;

        private sealed class Rec { public string Key; public int Page; public long Ticks; public bool IsHash; }

        private static readonly Dictionary<string, Rec> ByPath = new Dictionary<string, Rec>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Rec> ByHash = new Dictionary<string, Rec>();
        private static bool _loaded;

        public static bool Dirty;

        private static string StorePath
        {
            get
            {
                string dir = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
                return Path.Combine(dir, "PdfTool.pos.ini");
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(StorePath)) return;
                foreach (string line in File.ReadAllLines(StorePath, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] p = line.Split('\t');
                    if (p.Length < 3) continue;
                    int page; long ticks; long dummy;
                    if (!int.TryParse(p[2], out page) || page < 1) continue;
                    if (!long.TryParse(p.Length > 3 ? p[3] : "0", out ticks)) ticks = 0;
                    Rec r = new Rec();
                    r.Key = p[1]; r.Page = page; r.Ticks = ticks; r.IsHash = p[0] == "H";
                    if (r.Key.Length == 0) continue;
                    if (r.IsHash) { long.TryParse("x", out dummy); ByHash[r.Key] = r; }
                    else ByPath[r.Key] = r;
                }
            }
            catch { }
        }

        // 打开文件时调用：返回上次页码（1 基），没有记录返回 0
        public static int Resume(string path)
        {
            if (path == null) return 0;
            EnsureLoaded();
            try
            {
                Rec r;
                if (ByPath.TryGetValue(path, out r))
                {
                    r.Ticks = DateTime.Now.Ticks;
                    return r.Page;
                }
                string hash = Fingerprint(path);
                if (hash != null && ByHash.TryGetValue(hash, out r))   // 改过名/挪过地方：按内容认出来，顺手迁移到新路径
                {
                    Rec np = new Rec();
                    np.Key = path; np.Page = r.Page; np.Ticks = DateTime.Now.Ticks; np.IsHash = false;
                    ByPath[path] = np;
                    r.Ticks = np.Ticks;
                    Dirty = true;
                    return r.Page;
                }
            }
            catch { }
            return 0;
        }

        public static void Remember(string path, int page)
        {
            if (path == null || page < 1) return;
            EnsureLoaded();
            try
            {
                Rec r;
                if (ByPath.TryGetValue(path, out r))
                {
                    if (r.Page == page) return;      // 没变就别标脏
                    r.Page = page; r.Ticks = DateTime.Now.Ticks;
                }
                else
                {
                    r = new Rec();
                    r.Key = path; r.Page = page; r.Ticks = DateTime.Now.Ticks; r.IsHash = false;
                    ByPath[path] = r;
                    string h = Fingerprint(path);
                    if (h != null)
                    {
                        Rec hr = new Rec();
                        hr.Key = h; hr.Page = page; hr.Ticks = r.Ticks; hr.IsHash = true;
                        ByHash[h] = hr;
                    }
                }
                Dirty = true;
            }
            catch { }
        }

        public static void Flush()
        {
            if (!Dirty) return;
            Dirty = false;
            try
            {
                EnsureLoaded();
                Prune();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# PdfTool 阅读进度：类型 键 页码 时间戳（制表符分隔，改坏了删掉即可重建）");
                foreach (Rec r in ByPath.Values) sb.AppendLine("P\t" + r.Key + "\t" + r.Page + "\t" + r.Ticks);
                foreach (Rec r in ByHash.Values) sb.AppendLine("H\t" + r.Key + "\t" + r.Page + "\t" + r.Ticks);
                File.WriteAllText(StorePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // 只保留最近的 MaxEntries 条（指纹表按路径表重建）
        private static void Prune()
        {
            if (ByPath.Count <= MaxEntries) return;
            List<Rec> all = new List<Rec>(ByPath.Values);
            all.Sort(delegate(Rec a, Rec b) { return b.Ticks.CompareTo(a.Ticks); });
            ByPath.Clear();
            for (int i = 0; i < all.Count && i < MaxEntries; i++) ByPath[all[i].Key] = all[i];
            ByHash.Clear();
            foreach (Rec r in ByPath.Values)
            {
                string h = Fingerprint(r.Key);
                if (h != null)
                {
                    Rec hr = new Rec();
                    hr.Key = h; hr.Page = r.Page; hr.Ticks = r.Ticks; hr.IsHash = true;
                    ByHash[h] = hr;
                }
            }
        }

        private static void ReadFull(Stream s, byte[] buf, int offset, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, offset + got, count - got);
                if (n <= 0) break;
                got += n;
            }
        }

        // 指纹 = "大小-MD5前8字节"，MD5 输入是「头 64KB + 尾 64KB」（小文件就是全部）
        private static string Fingerprint(string path)
        {
            try
            {
                const int chunk = 64 * 1024;
                byte[] buf;
                long size;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    size = fs.Length;
                    if (size <= chunk * 2L)
                    {
                        buf = new byte[size];
                        ReadFull(fs, buf, 0, buf.Length);
                    }
                    else
                    {
                        buf = new byte[chunk * 2];
                        ReadFull(fs, buf, 0, chunk);
                        fs.Seek(-chunk, SeekOrigin.End);
                        ReadFull(fs, buf, chunk, chunk);
                    }
                }
                using (MD5 md5 = MD5.Create())
                {
                    byte[] h = md5.ComputeHash(buf);
                    StringBuilder sb = new StringBuilder(size.ToString());
                    sb.Append('-');
                    for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }
    }
}
