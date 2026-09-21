using System;
using System.IO;
using System.Windows.Forms;

namespace PdfTool
{
    // 极简设置：只记"保存到哪里"（桌面 / 文件所在文件夹），存在 exe 旁边的 PdfTool.ini
    internal static class Settings
    {
        public static bool SaveToDesktop;

        private static string IniPath()
        {
            try
            {
                return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "PdfTool.ini");
            }
            catch
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PdfTool.ini");
            }
        }

        public static void Load()
        {
            try
            {
                string p = IniPath();
                if (!File.Exists(p)) return;
                foreach (string line in File.ReadAllLines(p))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    if (k == "SaveTo") SaveToDesktop = (v == "desktop");
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                File.WriteAllText(IniPath(), "SaveTo=" + (SaveToDesktop ? "desktop" : "local") + "\r\n");
            }
            catch { }
        }

        public static string DesktopDir
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        }
    }
}
