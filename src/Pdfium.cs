using System;
using System.Runtime.InteropServices;

namespace PdfTool
{
    // pdfium.dll 的最小 P/Invoke 绑定（签名照 include/*.h 逐条核对）
    internal static class Pdfium
    {
        private const string Dll = "pdfium.dll";

        [StructLayout(LayoutKind.Sequential)]
        internal struct FPDF_FILEWRITE
        {
            public int version;
            public IntPtr WriteBlock; // int (*)(FPDF_FILEWRITE*, const void*, unsigned long)
        }

        internal delegate int WriteBlockDelegate(IntPtr self, IntPtr data, uint size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDF_InitLibrary();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDF_DestroyLibrary();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern IntPtr FPDF_LoadMemDocument(IntPtr dataBuf, int size, string password);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint FPDF_GetLastError();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDF_CreateNewDocument();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDF_ClosePage(IntPtr page);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern float FPDF_GetPageWidthF(IntPtr page);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern float FPDF_GetPageHeightF(IntPtr page);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDFPage_New(IntPtr document, int pageIndex, double width, double height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int FPDF_ImportPages(IntPtr destDoc, IntPtr srcDoc, string pageRange, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDF_SaveAsCopy(IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY,
            int sizeX, int sizeY, int rotate, int flags);

        // 直接渲染到设备 DC（打印机 DC + EMF 打印模式 → 文字/矢量以 GDI 指令输出，spool 小）
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDF_RenderPage(IntPtr hdc, IntPtr page, int startX, int startY,
            int sizeX, int sizeY, int rotate, int flags);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDF_SetPrintMode(int mode);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFBitmap_GetWidth(IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFBitmap_GetHeight(IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFBitmap_GetFormat(IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFPage_CountObjects(IntPtr page);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFPageObj_GetType(IntPtr pageObject);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDFImageObj_GetBitmap(IntPtr imageObject);

        [StructLayout(LayoutKind.Sequential)]
        internal struct FPDF_IMAGEOBJ_METADATA
        {
            public uint width;
            public uint height;
            public float horizontal_dpi;
            public float vertical_dpi;
            public uint bits_per_pixel;
            public int colorspace;
            public int marked_content_id;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFImageObj_GetImageMetadata(IntPtr imageObject, IntPtr page,
            ref FPDF_IMAGEOBJ_METADATA metadata);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint FPDFImageObj_GetImageDataDecoded(IntPtr imageObject, IntPtr buffer, uint buflen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint FPDFImageObj_GetImageDataRaw(IntPtr imageObject, IntPtr buffer, uint buflen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFImageObj_GetImageFilterCount(IntPtr imageObject);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint FPDFImageObj_GetImageFilter(IntPtr imageObject, int index, IntPtr buffer, uint buflen);

        internal const int CsGray = 1;
        internal const int CsRgb = 2;
        internal const int CsCmyk = 3;

        [StructLayout(LayoutKind.Sequential)]
        internal struct FPDF_FILEACCESS
        {
            public uint FileLen;
            public IntPtr GetBlock;
            public IntPtr Param;
        }

        internal delegate int GetBlockDelegate(IntPtr param, uint position, IntPtr buffer, uint size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDFPage_Delete(IntPtr document, int pageIndex);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDF_MovePages(IntPtr document, int[] pageIndices, uint pageIndicesLen, int destPageIndex);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr FPDFPageObj_NewImageObj(IntPtr document);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFImageObj_SetBitmap(IntPtr[] pages, int count, IntPtr imageObject, IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFImageObj_LoadJpegFileInline(IntPtr[] pages, int count, IntPtr imageObject,
            ref FPDF_FILEACCESS fileAccess);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FPDFPageObj_Transform(IntPtr pageObject, double a, double b, double c,
            double d, double e, double f);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FPDFPage_GenerateContent(IntPtr page);

        internal const int PageObjImage = 3;
        internal const int BitmapBgrx = 3;
        internal const int BitmapBgra = 4;
        internal const int FlagAnnot = 0x01;
        internal const int FlagLcdText = 0x02;
        internal const int FlagPrinting = 0x800;   // FPDF_RenderPage 打印用
        internal const int FlagGrayScale = 0x08;   // 灰度渲染（黑白打印用）
        internal const int PrintModeEmf = 0;       // FPDF_SetPrintMode：输出 EMF/GDI 指令（默认）
    }
}
