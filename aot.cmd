dotnet build Tests/Synqra.Tests -c Release

dotnet publish Tests/Synqra.Tests -c Release -r win-x64 -p:TargetFramework=net8.0
Tests\Synqra.Tests\bin\Release\net8.0\win-x64\publish\Synqra.Tests.exe

dotnet publish Tests/Synqra.Tests -c Release -r win-x64 -p:TargetFramework=net9.0
Tests\Synqra.Tests\bin\Release\net9.0\win-x64\publish\Synqra.Tests.exe

::dotnet publish Tests/Synqra.Tests -c Release -r win-x64 -p:TargetFramework=net10.0
::Tests\Synqra.Tests\bin\Release\net9.0\win-x64\publish\Synqra.Tests.exe --treenode-filter /*/*/PerformanceTests/*

