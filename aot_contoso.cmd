dotnet publish Contoso/Contoso.WebHost -c Release -r win-x64 -f net10.0
if errorlevel 1 exit /b 1
Contoso\Contoso.WebHost\bin\Release\net10.0\win-x64\publish\Contoso.WebHost.exe
if errorlevel 1 exit /b 1
