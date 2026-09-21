using System;
using System.IO;
using System.Windows.Forms;

namespace PdfTool
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool cli = args.Length >= 2 && (args[0] == "--split" || args[0] == "--selftest" || args[0] == "--rendump" || args[0] == "--extract" || args[0] == "--rawinfo" || args[0] == "--lzwtest");
            Pdfium.FPDF_InitLibrary();
            int exit = 0;
            try
            {
                if (cli)
                {
                    if (args[0] == "--split") exit = Cli.Split(args[1]);
                    else if (args[0] == "--extract")
                    {
                        if (args.Length < 4) throw new Exception("用法：--extract 文件.pdf 页码 输出目录");
                        exit = Cli.Extract(args[1], args[2], args[3]);
                    }
                    else if (args[0] == "--lzwtest")
                    {
                        exit = Cli.LzwTest(args[1], args[2]);
                    }
                    else if (args[0] == "--rawinfo")
                    {
                        exit = Cli.RawInfo(args[1], args[2]);
                    }
                    else if (args[0] == "--rendump")
                    {
                        if (args.Length < 5) throw new Exception("用法：--rendump 文件.pdf 页码 宽度 输出.png");
                        exit = Cli.RenderDump(args[1], args[2], args[3], args[4]);
                    }
                    else exit = Cli.SelfTest(args[1]);
                }
                else
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += OnThreadException;
                    string initial = null;
                    if (args.Length >= 1 && File.Exists(args[0])) initial = args[0];
                    Application.Run(new MainForm(initial));
                }
            }
            catch (Exception ex)
            {
                if (cli)
                {
                    Console.WriteLine("出错：" + ex.Message);
                    exit = 1;
                }
                else
                {
                    MessageBox.Show(ex.Message, "出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    exit = 1;
                }
            }
            finally
            {
                Pdfium.FPDF_DestroyLibrary();
            }
            Environment.ExitCode = exit;
        }

        // 兜底：任何没想到的界面异常都弹提示，不再出 .NET 崩溃对话框
        private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            MessageBox.Show("出错了：" + e.Exception.Message + "\n\n窗口会继续开着，可以重试或换个文件。",
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
