dotnet publish Tests/Synqra.Tests -c Release -r win-x64
Tests\Synqra.Tests\bin\Release\net9.0\win-x64\publish\Synqra.Tests.exe --treenode-filter /*/*/PerformanceTests/*
