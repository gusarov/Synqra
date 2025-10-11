using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Synqra.CodeGeneration;
using System.ComponentModel;

namespace Synqra.SourceGenerator.Tests;

internal class SourceGeneratorTests
{
	[Test]
	public async Task Should_generate_code()
	{
		var source = @"
using Synqra;
namespace Test;

[Schema(2025.776, ""1 Integers IList<int>"")]
[Schema(1.0, ""1"")]
public partial class SampleModel
{
	public partial IList<int> ListInt { get; set; }
}
";

		var generator = new ModelBindingGenerator();

		var compilation = CSharpCompilation.Create("GeneratorUnitTest")
			.AddSyntaxTrees(CSharpSyntaxTree.ParseText(source))
			.AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
			.AddReferences(MetadataReference.CreateFromFile(typeof(IBindableModel).Assembly.Location))
			.AddReferences(MetadataReference.CreateFromFile(typeof(SchemaAttribute).Assembly.Location))
			.AddReferences(MetadataReference.CreateFromFile(typeof(INotifyPropertyChanged).Assembly.Location))
			.WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
			;

		var driver = CSharpGeneratorDriver.Create(generator)
			.RunGeneratorsAndUpdateCompilation(compilation, out _, out var _)
			;

		// Verify the generated code
		var res = driver.GetRunResult();
		Console.WriteLine(res);
		foreach (var item in res.Diagnostics)
		{
			Console.WriteLine(item);
		}
	}
}