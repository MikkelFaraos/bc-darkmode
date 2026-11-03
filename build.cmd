@echo off
rem Builds BCDarkOverlay.exe with the C# compiler that ships with Windows.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:BCDarkOverlay.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll BCDarkOverlay.cs
