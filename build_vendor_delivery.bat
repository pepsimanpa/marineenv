@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

rem ============================================================
rem MarineEnvironment vendor delivery builder
rem
rem Result:
rem   deliver-vendor\
rem     Viewer\   - Viewer source only
rem     lib\      - MarineEnvironment binaries only (no core source)
rem     app\      - prebuilt Viewer executable/runtime
rem     config\   - runtime configuration
rem     db\       - runtime DB (optional; enabled by default)
rem     native\   - NetCDF/HDF5 native runtime when available
rem     README.md
rem
rem Set COPY_DB=0 if DB files will be delivered separately.
rem ============================================================

set "ROOT=%~dp0"
set "OUT=%ROOT%deliver-vendor"
set "CORE_PROJECT=%ROOT%src\MarineEnvironment\MarineEnvironment.csproj"
set "CORE_OUTPUT=%ROOT%src\MarineEnvironment\bin\Release\net5.0"
set "VIEWER_SOURCE=%ROOT%src\MarineEnvironment.Viewer"
set "CONFIG_SOURCE=%ROOT%config"
set "DB_SOURCE=%ROOT%db"
set "NATIVE_SOURCE=%ROOT%native\win-x64"
set "README_SOURCE=%ROOT%docs\VENDOR_DELIVERY_README.md"
set "COPY_DB=1"

echo.
echo ============================================================
echo  MarineEnvironment - Vendor Delivery Builder
echo ============================================================
echo.

echo [1/9] Building MarineEnvironment.dll (Release)...
dotnet build "%CORE_PROJECT%" -c Release
if errorlevel 1 goto :fail

if not exist "%CORE_OUTPUT%\MarineEnvironment.dll" (
    echo ERROR: MarineEnvironment.dll was not created.
    goto :fail
)

echo [2/9] Recreating delivery directory...
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"
mkdir "%OUT%\Viewer"
mkdir "%OUT%\lib"
mkdir "%OUT%\config"
mkdir "%OUT%\db"
mkdir "%OUT%\native"
mkdir "%OUT%\app"

echo [3/9] Copying Viewer source...
robocopy "%VIEWER_SOURCE%" "%OUT%\Viewer" /E /R:1 /W:1 /XD bin obj .vs /XF *.user *.suo *.pdb >nul
if errorlevel 8 (
    echo ERROR: Failed to copy Viewer source.
    goto :fail
)

echo [4/9] Copying MarineEnvironment runtime binaries...
for %%F in ("%CORE_OUTPUT%\*.dll" "%CORE_OUTPUT%\*.json" "%CORE_OUTPUT%\*.xml") do (
    if exist "%%~fF" copy /Y "%%~fF" "%OUT%\lib\" >nul
)
if not exist "%OUT%\lib\MarineEnvironment.dll" (
    echo ERROR: MarineEnvironment.dll was not copied.
    goto :fail
)

echo [5/9] Converting Viewer project reference to DLL reference...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p = '%OUT%\Viewer\MarineEnvironment.Viewer.csproj';" ^
  "$c = [System.IO.File]::ReadAllText($p);" ^
  "$old = '<ProjectReference Include=\"..\MarineEnvironment\MarineEnvironment.csproj\" />';" ^
  "$new = '<Reference Include=\"MarineEnvironment\">' + [Environment]::NewLine + '      <HintPath>..\lib\MarineEnvironment.dll</HintPath>' + [Environment]::NewLine + '      <Private>true</Private>' + [Environment]::NewLine + '    </Reference>';" ^
  "if (-not $c.Contains($old)) { throw 'ProjectReference to MarineEnvironment was not found.' };" ^
  "$c = $c.Replace($old, $new);" ^
  "$c = $c.Replace('..\..\native\win-x64\*.dll', '..\native\win-x64\*.dll');" ^
  "[System.IO.File]::WriteAllText($p, $c, (New-Object System.Text.UTF8Encoding($false)));"
if errorlevel 1 goto :fail

echo [6/9] Copying config and native runtime...
robocopy "%CONFIG_SOURCE%" "%OUT%\config" /E /R:1 /W:1 >nul
if errorlevel 8 (
    echo ERROR: Failed to copy config.
    goto :fail
)

if exist "%NATIVE_SOURCE%" (
    mkdir "%OUT%\native\win-x64" >nul 2>&1
    robocopy "%NATIVE_SOURCE%" "%OUT%\native\win-x64" /E /R:1 /W:1 /XF *.pdb >nul
    if errorlevel 8 (
        echo ERROR: Failed to copy native runtime.
        goto :fail
    )
) else (
    echo WARNING: native\win-x64 was not found.
    >"%OUT%\native\README.txt" echo Copy the complete x64 NetCDF-C/HDF5 runtime into native\win-x64 before running sources that require it.
)

if "%COPY_DB%"=="1" (
    if exist "%DB_SOURCE%" (
        echo [7/9] Copying runtime DB...
        robocopy "%DB_SOURCE%" "%OUT%\db" /E /R:1 /W:1 /XD .git bin obj >nul
        if errorlevel 8 (
            echo ERROR: Failed to copy DB.
            goto :fail
        )
    ) else (
        echo [7/9] WARNING: db directory was not found. Creating an empty db folder.
        >"%OUT%\db\README.txt" echo Place runtime database files here. Paths in config\marineenvironment.json are relative to this folder.
    )
) else (
    echo [7/9] DB copy skipped because COPY_DB=0.
    >"%OUT%\db\README.txt" echo DB files are delivered separately. Place them here or update config\marineenvironment.json.
)

echo [8/9] Creating Viewer-only solution and validating build...
pushd "%OUT%"
dotnet new sln -n MarineEnvironment.Viewer --force >nul
if errorlevel 1 (
    popd
    goto :fail
)
dotnet sln "MarineEnvironment.Viewer.sln" add "Viewer\MarineEnvironment.Viewer.csproj" >nul
if errorlevel 1 (
    popd
    goto :fail
)
dotnet build "Viewer\MarineEnvironment.Viewer.csproj" -c Release
if errorlevel 1 (
    popd
    goto :fail
)
popd

if exist "%OUT%\Viewer\bin\Release\net5.0-windows" (
    robocopy "%OUT%\Viewer\bin\Release\net5.0-windows" "%OUT%\app" /E /R:1 /W:1 /XF *.pdb >nul
    if errorlevel 8 (
        echo ERROR: Failed to copy prebuilt Viewer.
        goto :fail
    )
)

rem Keep delivered Viewer source clean.
if exist "%OUT%\Viewer\bin" rmdir /s /q "%OUT%\Viewer\bin"
if exist "%OUT%\Viewer\obj" rmdir /s /q "%OUT%\Viewer\obj"

echo [9/9] Copying vendor README...
if exist "%README_SOURCE%" (
    copy /Y "%README_SOURCE%" "%OUT%\README.md" >nul
) else (
    >"%OUT%\README.txt" echo MarineEnvironment vendor delivery
)

echo.
echo ============================================================
echo  SUCCESS
echo ============================================================
echo Delivery created:
echo   %OUT%
echo.
echo Included:
echo   - Viewer source
echo   - MarineEnvironment.dll without core C# source
echo   - Prebuilt Viewer application
echo   - config
echo   - db ^(COPY_DB=%COPY_DB%^)
echo   - native runtime when available
echo.
echo Excluded:
echo   - src\MarineEnvironment source
echo   - KodcDiagnostics
echo   - KhoaSeasonalSmoke
echo   - Goci2Downloader
echo   - tests
echo   - .git / .github
echo   - bin / obj / PDB
echo.
exit /b 0

:fail
echo.
echo ============================================================
echo  FAILED
echo ============================================================
echo Vendor delivery was not completed.
exit /b 1
