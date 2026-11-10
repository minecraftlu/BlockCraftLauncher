@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0.."
echo ==============================================
echo BlockCraftLauncher WPF v0.9.37 RC2 Preview Candidate - net48
echo Preview build - not a stable release
echo ==============================================

set "MSBUILD_EXE="
where msbuild.exe >nul 2>nul
if not errorlevel 1 (
  for /f "delims=" %%P in ('where msbuild.exe') do if not defined MSBUILD_EXE set "MSBUILD_EXE=%%P"
)
if not defined MSBUILD_EXE (
  set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
  if exist "!VSWHERE!" (
    for /f "usebackq delims=" %%P in (`"!VSWHERE!" -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe"`) do if not defined MSBUILD_EXE set "MSBUILD_EXE=%%P"
  )
)
if not defined MSBUILD_EXE (
  echo ERROR: Visual Studio 2022 MSBuild not found.
  echo Install the .NET desktop development workload.
  exit /b 2
)

set "TARGET_REF=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll"
if not exist "%TARGET_REF%" (
  echo ERROR: .NET Framework 4.8 targeting pack is missing.
  echo Install the .NET Framework 4.8 Developer Pack.
  exit /b 3
)

"%MSBUILD_EXE%" "BlockCraftLauncher-WPF.sln" -restore -t:Build -p:Configuration=Release -p:Platform="Any CPU" -p:PlatformTarget=x64 -p:Prefer32Bit=false -p:RestoreIgnoreFailedSources=false -m -v:minimal
if errorlevel 1 (
  echo BUILD FAILED. See MSBuild errors above.
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "build\package-windows.ps1"
if errorlevel 1 (
  echo PACKAGING FAILED. No valid release ZIP is available.
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "build\build-onefile.ps1"
if errorlevel 1 (
  echo ONE-FILE PACKAGING FAILED.
  exit /b 1
)
echo.
echo BUILD SUCCEEDED. Check dist\ for ZIP and SHA-256.
exit /b 0
