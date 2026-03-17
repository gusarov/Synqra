using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Synqra.CodeGeneration;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace Synqra.SourceGenerator.Tests;

internal class SourceGeneratorTests
{
	[Test]
	public void Should_generate_code_for_list()
	{
		var source = @"
using Synqra;
using System.Collections.Generic;
namespace Test;

[SynqraModel]
[Schema(2025.776, ""1 Integers IList<int>"")]
[Schema(1.0, ""1"")]
public partial class SampleModel
{
	public partial IList<int> ListInt { get; set; }
}
";

		var result = RunGenerator(source);

		Assert.That(result.GeneratedSources.Any(s => s.Contains("partial class SampleModel")), Is.True);
		Assert.That(result.Errors, Is.Empty, string.Join(Environment.NewLine, result.Errors));
	}

	[Test]
	public void Should_generate_code_for_dict()
	{
		var source = @"
using Synqra;
using System.Collections.Generic;
namespace Test;

[SynqraModel]
[Schema(1.0, ""1"")]
public partial class SampleModel
{
	public partial IDictionary<string, object> DictInt { get; set; }
}
";

		var result = RunGenerator(source);

		Assert.That(result.GeneratedSources.Any(s => s.Contains("partial class SampleModel")), Is.True);
		Assert.That(result.Errors, Is.Empty, string.Join(Environment.NewLine, result.Errors));
	}

	[Test]
	public void Should_generate_compilable_code_for_multiple_int_properties()
	{
		var source = @"
using Synqra;
namespace Test;

[SynqraModel]
[Schema(1.0, ""1 First int Second int"")]
public partial class SampleModel
{
	public partial int First { get; set; }
	public partial int Second { get; set; }
}
";

		var result = RunGenerator(source);
		var generated = result.GeneratedSources.Single();

		Assert.That(generated, Does.Contain("case \"First\":"));
		Assert.That(generated, Does.Contain("case \"Second\":"));
		Assert.That(result.Errors, Is.Empty, string.Join(Environment.NewLine, result.Errors));
	}

	[Test]
	public void Should_generate_long_to_int_coercion_for_derived_int_properties()
	{
		var source = @"
using Synqra;
namespace Test;

[SynqraModel]
[Schema(1.0, ""1 BaseValue int"")]
public partial class BaseModel
{
	public partial int BaseValue { get; set; }
}

[SynqraModel]
[Schema(2.0, ""1 BaseValue int DerivedValue int"")]
public partial class DerivedModel : BaseModel
{
	public partial int DerivedValue { get; set; }
}
";

		var result = RunGenerator(source);
		var generated = string.Join(Environment.NewLine, result.GeneratedSources);

		Assert.That(generated, Does.Contain("case \"DerivedValue\":"));
		Assert.That(generated, Does.Contain("if (value is long l)"));
		Assert.That(result.Errors, Is.Empty, string.Join(Environment.NewLine, result.Errors));
	}

	private static (string[] GeneratedSources, Diagnostic[] Errors) RunGenerator(string source)
	{
		var generator = new ModelBindingGenerator();

		var compilation = CSharpCompilation.Create("GeneratorUnitTest")
			.AddSyntaxTrees(CSharpSyntaxTree.ParseText(source))
			.AddReferences(GetMetadataReferences())
			.WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		var driver = CSharpGeneratorDriver.Create(generator)
			.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

		var runResult = driver.GetRunResult();
		var generatedSources = runResult.Results
			.SelectMany(x => x.GeneratedSources)
			.Select(x => x.SourceText.ToString())
			.ToArray();
		var errors = outputCompilation.GetDiagnostics()
			.Where(x => x.Severity == DiagnosticSeverity.Error)
			.ToArray();

		return (generatedSources, errors);
	}

	private static MetadataReference[] GetMetadataReferences()
	{
		var references = new HashSet<string>(
			((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
				.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
			StringComparer.OrdinalIgnoreCase);

		references.Add(typeof(IBindableModel).Assembly.Location);
		references.Add(typeof(SchemaAttribute).Assembly.Location);
		references.Add(typeof(Synqra.BinarySerializer.ISbxSerializer).Assembly.Location);
		references.Add(typeof(GuidExtensions).Assembly.Location);
		references.Add(typeof(EmergencyLog).Assembly.Location);

		return references
			.Select(path => MetadataReference.CreateFromFile(path))
			.ToArray();
	}
}
