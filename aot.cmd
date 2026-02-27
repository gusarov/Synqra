del Tests\Synqra.Tests\bin\Release\net10.0\win-x64\publish\Synqra.Tests.exe
dotnet publish Tests/Synqra.Tests -c Release /clp:ErrorsOnly -m:1 -tl:off -r win-x64 -f net10.0
if errorlevel 1 exit /b 1
::Tests\Synqra.Tests\bin\Release\net10.0\win-x64\publish\Synqra.Tests.exe --treenode-filter /*/*/PerformanceTests/*
::Tests\Synqra.Tests\bin\Release\net10.0\win-x64\publish\Synqra.Tests.exe 
Tests\Synqra.Tests\bin\Release\net10.0\win-x64\publish\Synqra.Tests.exe --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"
if errorlevel 1 exit /b 1
