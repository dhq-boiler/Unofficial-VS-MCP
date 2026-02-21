@echo off
setlocal
set MSBUILD="C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
echo Using MSBuild: %MSBUILD%
%MSBUILD% "%~dp0src\VsMcp.Extension\VsMcp.Extension.csproj" -p:Configuration=Release -restore -v:normal
echo Exit code: %ERRORLEVEL%
endlocal
