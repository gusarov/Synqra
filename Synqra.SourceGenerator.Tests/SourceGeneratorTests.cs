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

	[Test]
	public void Setter_always_emits_CommandSubmissionOptions_with_ExpectedLastEventId()
	{
		// Optimistic concurrency is the default for generated setters.
		// No per-model opt-in is required; the projection-side check fires only
		// when ExpectedLastEventId is non-empty, and the generator always sets it
		// to the last event id the projection has applied to this target.
		// Same semantic as `git push --force-with-lease=<sha>`.
		var source = @"
using Synqra;
namespace Test;

[SynqraModel]
[Schema(2026.500, ""1 Subject string?"")]
public sealed partial class SomeModel
{
	public partial string? Subject { get; set; }
}
";

		var result = RunGenerator(source);
		var generated = string.Join(Environment.NewLine, result.GeneratedSources);

		Assert.That(generated, Does.Contain("global::Synqra.CommandSubmissionOptions"),
			"Generated setter must emit a CommandSubmissionOptions argument unconditionally.");
		Assert.That(generated, Does.Contain("ExpectedLastEventId = __store.GetLastEventId"),
			"Generated setter must reference the current last event id of the target.");
		Assert.That(generated, Does.Not.Contain("ExpectedTargetVersion"),
			"The numeric-version field is gone; only event-id-based precondition remains.");
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

	private static (string[] GeneratedSources, Diagnostic[] Errors) RunBothGenerators(string source)
	{
		var compilation = CSharpCompilation.Create("GeneratorUnitTest")
			.AddSyntaxTrees(CSharpSyntaxTree.ParseText(source))
			.AddReferences(GetMetadataReferences())
			.WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		var driver = CSharpGeneratorDriver.Create(
				new ModelBindingGenerator()
				, new Synqra.BinarySerializer.SourceGenerator.SbxBindingGenerator()
			)
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

[TestFixture]
internal class SbxGeneratorTests
{
	[Test]
	public void Should_generate_GetCore_and_SetCore_for_schema()
	{
		var source = @"
using Synqra;
namespace Test;

[SynqraModel]
[Schema(1.0, ""1 Subject string?"")]
public sealed partial class SomeModel
{
	public partial string? Subject { get; set; }
}
";

		var result = RunSbxGenerator(source);

		Assert.That(result.GeneratedSources.Any(s => s.Contains("GetCore")), Is.True,
			"SBX generator must emit GetCore");
		Assert.That(result.GeneratedSources.Any(s => s.Contains("SetCore")), Is.True,
			"SBX generator must emit SetCore");
		Assert.That(result.Errors, Is.Empty, string.Join(Environment.NewLine, result.Errors));
	}

	[Test]
	public void Should_generate_IBindableModel_sbx_bridge_methods()
	{
		var source = @"
using Synqra;
namespace Test;

[SynqraModel]
[Schema(1.0, ""1 Value int"")]
public sealed partial class FlatModel
{
	public partial int Value { get; set; }
}
";

		var result = RunSbxGenerator(source);
		var generated = string.Join(Environment.NewLine, result.GeneratedSources);

		Assert.That(generated, Does.Contain("IBindableModel.Get(ISbxSerializer"),
			"SBX generator must emit IBindableModel.Get(ISbxSerializer...) bridge");
		Assert.That(generated, Does.Contain("IBindableModel.Set(ISbxSerializer"),
			"SBX generator must emit IBindableModel.Set(ISbxSerializer...) bridge");
		Assert.That(result.Errors, Is.Empty, string.Join(Environment.NewLine, result.Errors));
	}

	[Test]
	public void Should_generate_schema_version_dispatch()
	{
		var source = @"
using Synqra;
namespace Test;

[SynqraModel]
[Schema(1.0, ""1 Name string"")]
[Schema(2.0, ""1 Name string Age int"")]
public sealed partial class VersionedModel
{
	public partial string Name { get; set; }
	public partial int Age { get; set; }
}
";

		var result = RunSbxGenerator(source);
		var generated = string.Join(Environment.NewLine, result.GeneratedSources);

		Assert.That(generated, Does.Contain("version == 1"), "Schema version 1 dispatch must be present");
		Assert.That(generated, Does.Contain("version == 2"), "Schema version 2 dispatch must be present");
		Assert.That(result.Errors, Is.Empty, string.Join(Environment.NewLine, result.Errors));
	}

	[Test]
	public void Domain_generator_should_NOT_emit_ISbxSerializer_methods()
	{
		var source = @"
using Synqra;
namespace Test;

[SynqraModel]
[Schema(1.0, ""1 Subject string?"")]
public sealed partial class SomeModel
{
	public partial string? Subject { get; set; }
}
";

		var domainResult = RunDomainOnlyGenerator(source);
		var domainGenerated = string.Join(Environment.NewLine, domainResult.GeneratedSources);

		Assert.That(domainGenerated, Does.Not.Contain("ISbxSerializer"),
			"Domain generator output must not contain any ISbxSerializer references");
	}

	private static (string[] GeneratedSources, Diagnostic[] Errors) RunSbxGenerator(string source)
	{
		var generator = new Synqra.BinarySerializer.SourceGenerator.SbxBindingGenerator();

		var compilation = CSharpCompilation.Create("SbxGeneratorTest")
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

	private static (string[] GeneratedSources, Diagnostic[] Errors) RunDomainOnlyGenerator(string source)
	{
		var generator = new ModelBindingGenerator();

		var compilation = CSharpCompilation.Create("DomainGeneratorTest")
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
