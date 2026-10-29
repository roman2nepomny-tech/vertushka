@echo off
setlocal
set SRC=%~dp0
set /p VER=<"%SRC%version.txt"
echo Build Vertushka %VER%
"C:\Program Files\dotnet\dotnet.exe" publish "%SRC%Vertushka\Vertushka.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=%VER% -o "%SRC%build\app"
if errorlevel 1 exit /b 1
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=%VER% "%SRC%installer\vertushka.iss"
endlocal