@echo off
rem Makes the standalone CAD Convert folder: the app, invre, the scripts and the FreeCAD it drives, nothing else to install.
rem   tools\package.cmd [destination]     default %USERPROFILE%\CAD Convert (outside OneDrive)
rem FreeCAD comes from the portable copy under %LOCALAPPDATA%\CADConvert (or CADCONVERT_FREECAD_DIR), cut down to what the
rem converter uses. Copy the folder anywhere and run CadConvert.exe; SolidCraft output still needs SolidCraft installed.
setlocal
set DEST=%~1
if "%DEST%"=="" set DEST=%USERPROFILE%\CAD Convert
set SRC=%CADCONVERT_FREECAD_DIR%
if "%SRC%"=="" for /d %%d in ("%LOCALAPPDATA%\CADConvert\FreeCAD_*") do set SRC=%%d
if not exist "%SRC%\bin\FreeCADCmd.exe" (echo FreeCAD not found under %LOCALAPPDATA%\CADConvert & exit /b 1)

call "%~dp0build.cmd" || exit /b 1
if not exist "%DEST%" mkdir "%DEST%"
copy /y "%~dp0bin\CadConvert.exe" "%DEST%\" >nul || exit /b 1
copy /y "%~dp0bin\invre.exe" "%DEST%\" >nul || exit /b 1
robocopy "%~dp0freecad" "%DEST%\freecad" *.py /xf quick_test.py case_test.py /njh /njs /nfl /ndl >nul
if errorlevel 8 exit /b 1
robocopy "%~dp0solidcraft" "%DEST%\solidcraft" *.py /xf quick_test.py /njh /njs /nfl /ndl >nul
if errorlevel 8 exit /b 1

rem FreeCAD goes in "engine": a folder called FreeCAD would be the same folder as the scripts' "freecad" on Windows. Only
rem what the converter was seen to use (engine_keep.txt; see engine_copy.py), into a fresh folder.
if exist "%DEST%\engine" rmdir /s /q "%DEST%\engine"
"%SRC%\bin\python.exe" -I "%~dp0engine_copy.py" "%SRC%" "%DEST%\engine" "%~dp0engine_keep.txt" || exit /b 1

> "%DEST%\README.txt" (
  echo CAD Convert: Inventor parts and assemblies to FreeCAD ^(and SolidCraft, if installed^).
  echo Run CadConvert.exe. Nothing to install; the folder can live anywhere. Settings and previews go in
  echo %%LOCALAPPDATA%%\CADConvert.
  echo.
  echo engine folder: FreeCAD 1.1.4, LGPL-2.1-or-later, source at https://www.freecad.org; see engine\doc\LICENSE.html and
  echo ThirdPartyLibraries.html. It holds only the parts of FreeCAD the converter uses.
)
echo Packaged in %DEST%
