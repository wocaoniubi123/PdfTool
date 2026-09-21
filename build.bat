@echo off
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo 找不到系统自带的 C# 编译器：%CSC%
  pause
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:app.ico /out:PdfTool.exe ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  src\Pdfium.cs src\PdfJob.cs src\Cli.cs src\MainForm.cs src\Program.cs
if errorlevel 1 (
  echo.
  echo 编译失败，请把上面的错误发给我。
  pause
  exit /b 1
)
echo.
echo 编译完成：%CD%\PdfTool.exe
pause
