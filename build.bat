@echo off
rem Compile with the Windows-bundled .NET Framework csc.exe (no install needed)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found, please install .NET Framework 4.x
  exit /b 1
)
"%CSC%" -nologo -codepage:65001 -target:winexe -optimize+ -out:QuickSnap.exe QuickSnap.cs -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll
if %errorlevel%==0 (
  echo Build OK: QuickSnap.exe
) else (
  echo Build FAILED
  exit /b 1
)
