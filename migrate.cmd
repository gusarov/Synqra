set c=%1
if ('%c%'=='') (
	set c=vRenamemeRenameme
)

cd Synqra.Projection.Sqlite
dotnet ef migrations add --framework net10.0 %c%
if errorlevel 1 exit /b 1
:: dotnet ef dbcontext optimize --framework net10.0 --precompile-queries
if errorlevel 1 exit /b 1
dotnet ef migrations script --framework net10.0 --output MigrationScripts/Latest.sql_
if errorlevel 1 exit /b 1

cd ..\Tests\Synqra.Tests
dotnet ef migrations add --framework net10.0 %c%
if errorlevel 1 exit /b 1
dotnet ef dbcontext optimize --framework net10.0 --precompile-queries
if errorlevel 1 exit /b 1
dotnet ef migrations script --framework net10.0 --output MigrationScripts/Latest.sql_
if errorlevel 1 exit /b 1

cd ..\..