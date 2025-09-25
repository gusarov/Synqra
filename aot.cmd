del Tests\Synqra.Tests\bin\Release\net9.0\win-x64\publish\Synqra.Tests.exe
dotnet publish Tests/Synqra.Tests -c Release -r win-x64 -f net9.0
::Tests\Synqra.Tests\bin\Release\net9.0\win-x64\publish\Synqra.Tests.exe --treenode-filter /*/*/PerformanceTests/*
::Tests\Synqra.Tests\bin\Release\net9.0\win-x64\publish\Synqra.Tests.exe 
Tests\Synqra.Tests\bin\Release\net9.0\win-x64\publish\Synqra.Tests.exe --treenode-filter "/*/*/*[(Ca1tegory!=Performance)&(CI!=false)]/*[(Ca2tegory!=Performance)&(CI!=false)]"
