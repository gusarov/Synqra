// Copy this to a model you need to trace:
// #define SYNQRA_CODEGEN_TRACE

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Synqra;
using System;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Transactions;

using BuildPropsProviderT = (
	  string Tfm
	, string SynqraBuildBox
	);
// using TheSource = (Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax clazz, Microsoft.CodeAnalysis.SemanticModel sem, string? data);
using ClassesProviderT = (
	  string? errorMessage
	, System.Exception? exception
	// -- OR --
	, Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax Clazz
	, Microsoft.CodeAnalysis.INamedTypeSymbol Data
	, Microsoft.CodeAnalysis.INamedTypeSymbol Ibm
	, Microsoft.CodeAnalysis.INamedTypeSymbol Ssa
	, Microsoft.CodeAnalysis.INamedTypeSymbol Ipc
	, Microsoft.CodeAnalysis.INamedTypeSymbol Ipcg
	, Microsoft.CodeAnalysis.INamedTypeSymbol Pceh
	, Microsoft.CodeAnalysis.INamedTypeSymbol Pcgeh
	);

/*
using AttributesProviderT = (
	  Microsoft.CodeAnalysis.INamedTypeSymbol Type
	, Microsoft.CodeAnalysis.AttributeData Attr
	);
*/

namespace Synqra.CodeGeneration;

using TheCombinedSource = (
		ClassesProviderT ClassData // 1
	, BuildPropsProviderT BuildProps // 2
	);

[Generator(LanguageNames.CSharp)]
public class ModelBindingGenerator : IIncrementalGenerator
{
	private static readonly DiagnosticDescriptor MissingReferenceDiagnostic = new(
		id: "SYNQRA001",
		title: "Synqra model generator prerequisites missing",
		messageFormat: "Synqra source generation requires a reference to Synqra.Model (missing type(s): {0})",
		category: "Synqra.ModelBindingGenerator",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor GenerationFailureDiagnostic = new(
		id: "SYNQRA002",
		title: "Synqra model generator failed",
		messageFormat: "{0}",
		category: "Synqra.ModelBindingGenerator",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static bool _enableTrace = false;

	[Conditional("SYNQRA_CODEGEN_TRACE")]
	private static void DebugLog(string message)
	{
		if (_enableTrace)
		{
			EmergencyLog.Default.LogTrace(message);
		}
	}

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		// AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
		// AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
		InitializeCore(context);
	}

	private System.Reflection.Assembly? CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
	{
		// SynqraEmergencyLog.Default.LogMessage($"[Asm Resolve] {args.Name}");
		return null;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private void InitializeCore(IncrementalGeneratorInitializationContext context)
	{
		try
		{
			/*
			const string SchemaAttributeFullName = "Synqra.SchemaAttribute";

			var attributesProvider = context.SyntaxProvider.ForAttributeWithMetadataName<AttributesProviderT>(
				fullyQualifiedMetadataName: SchemaAttributeFullName,
				predicate: static (node, _) => node is TypeDeclarationSyntax, // class/struct/record
				transform: static (ctx, _) =>
				{
					// The target symbol (class/record/struct)
					var type = (INamedTypeSymbol)ctx.TargetSymbol;

					// The specific attribute instance that matched
					var attr = ctx.Attributes[0]; // there can be multiple matches; handle as needed

					return (Type: type, Attr: attr);
				});
			*/

			/*
			var schemaAttrSymbol =
				context.CompilationProvider.Select(static (c, _) =>
					c.GetTypeByMetadataName(SchemaAttr));
			*/

			var buildPropsProvider = context.AnalyzerConfigOptionsProvider.Select((provider, _) =>
			{
				var g = provider.GlobalOptions;
				string Get(string name) => g.TryGetValue(name, out var v) ? v : string.Empty;

				// Commonly useful properties:
				var tfm = Get("build_property.TargetFramework");           // e.g. "net8.0", "net8.0-windows10.0.19041.0"
				var SynqraBuildBox = Get("build_property.SynqraBuildBox");           // e.g. "net8.0", "net8.0-windows10.0.19041.0"
				/*
				var tfms = Get("build_property.TargetFrameworks");          // multi-target list (design-time only; normal builds run per TFM)
				var tpi = Get("build_property.TargetPlatformIdentifier");  // e.g. "windows" (present when using OS-specific TFMs)
				var tpv = Get("build_property.TargetPlatformVersion");     // e.g. "10.0.19041.0"
				var rid = Get("build_property.RuntimeIdentifier");         // e.g. "win-x64"
				var rids = Get("build_property.RuntimeIdentifiers");        // e.g. "win-x64;linux-x64"
				var conf = Get("build_property.Configuration");             // Debug/Release
				var osver = Get("build_property.TargetOSVersion");           // newer SDKs
				var osid = Get("build_property.TargetOS");                  // newer SDKs
				return new BuildProps(tfm, tfms, tpi, tpv, rid, rids, conf, osid, osver);
				*/
				return (tfm, SynqraBuildBox);
			});

			var missingReferences = context.CompilationProvider.Select((comp, _) =>
			{
				var missing = new List<string>();
				if (comp.GetTypeByMetadataName("Synqra.IBindableModel") is null)
					missing.Add("Synqra.IBindableModel");
				if (comp.GetTypeByMetadataName("Synqra.SchemaAttribute") is null)
					missing.Add("Synqra.SchemaAttribute");
				return missing;
			});

			context.RegisterSourceOutput(
				missingReferences,
				static (ctx, missing) =>
				{
					if (missing.Count == 0)
					{
						return;
					}
					ctx.ReportDiagnostic(Diagnostic.Create(
						MissingReferenceDiagnostic,
						Location.None,
						string.Join(", ", missing)));
				});

			var classesProvider = context.SyntaxProvider.CreateSyntaxProvider<ClassesProviderT>(
				predicate: static (SyntaxNode node, CancellationToken cancelToken) =>
				{
					//the predicate should be super lightweight to filter out items that are not of interest quickly
					try
					{
						var exp = node is ClassDeclarationSyntax classDeclaration
							&& (classDeclaration.Identifier.ToString().EndsWith("Model") || classDeclaration.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == "SynqraModel")))
							&& classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword
							);
						// GeneratorLogging.LogMessage($"[+] Checking {node.GetType().Name} {node}: {exp}");
						return exp;
					}
					catch (Exception ex)
					{
						EmergencyLog.Default.Error($"predicate", ex);
						throw;
					}
				},
				transform: static (GeneratorSyntaxContext ctx, CancellationToken cancelToken) =>
				{
					try
					{
						cancelToken.ThrowIfCancellationRequested();

						//the transform is called only when the predicate returns true, so it can do a bit more heavyweight work but should mainly be about getting the data we want to work with later
						var classDeclaration = (ClassDeclarationSyntax)ctx.Node;
						var symbol = ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, cancelToken) ?? throw new Exception("symbol");

						var comp = ctx.SemanticModel.Compilation;

						var ibm = comp.GetTypeByMetadataName("Synqra.IBindableModel");
						var ssa = comp.GetTypeByMetadataName("Synqra.SchemaAttribute");
						if (ibm is null || ssa is null)
						{
							return (null, null, default!, default!, default!, default!, default!, default!, default!, default!);
						}
						var ipc = comp.GetTypeByMetadataName("System.ComponentModel.INotifyPropertyChanged") ?? throw new Exception("System.ComponentModel.INotifyPropertyChanged");
						var ipcg = comp.GetTypeByMetadataName("System.ComponentModel.INotifyPropertyChanging") ?? throw new Exception("System.ComponentModel.INotifyPropertyChanging");
						var pceh = comp.GetTypeByMetadataName("System.ComponentModel.PropertyChangedEventHandler") ?? throw new Exception("System.ComponentModel.PropertyChangedEventHandler");
						var pcgeh = comp.GetTypeByMetadataName("System.ComponentModel.PropertyChangingEventHandler") ?? throw new Exception("System.ComponentModel.PropertyChangingEventHandler");
						// var schemaAttribute = comp.GetTypeByMetadataName("Synqra.SchemaAttribute") ?? throw new Exception("Synqra.SchemaAttribute");

						cancelToken.ThrowIfCancellationRequested();

						/*
						var schemaAttrs = (schemaAttribute is null)
							? ImmutableArray<AttributeData>.Empty
							: all.Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, schemaAttribute))
								 .ToImmutableArray();
						*/

						return (null, null, classDeclaration, symbol, ibm, ssa, ipc, ipcg, pceh, pcgeh);
					}
					catch (Exception ex)
					{
						EmergencyLog.Default.Error($"transform", ex);
						return ($"Error processing class: {ex.Message}", ex, default!, default!, default!, default!, default!, default!, default!, default!);
						// throw;
					}
				});

			context.RegisterSourceOutput(
				  classesProvider //1
				  .Combine(buildPropsProvider) //2
				, Execute
				);
		}
		catch (Exception ex)
		{
			EmergencyLog.Default.Error($"Initialize", ex);
			throw;
		}
	}

	static string FQN(ITypeSymbol t) =>
		(t ?? throw new InvalidOperationException("Type not found"))
		.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

	static (double, string) GetSchemaData(AttributeData attr)
	{
		DebugLog("Schema: " + attr);
		return ((double)attr.ConstructorArguments[0].Value!, (string)attr.ConstructorArguments[1].Value!);
	}

	static IEnumerable<(double, string)> GetAllSchemasSymbol(ITypeSymbol symbol, ITypeSymbol schemaAttribute)
	{
		return symbol.GetAttributes()
#if DEBUG1
			.Where(attr => attr.AttributeClass?.Name == schemaAttribute.Name)
#else
			.Where(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, schemaAttribute))
#endif
			.Select(GetSchemaData);
	}

	static IEnumerable<(double, string)> GetAllSchemas(ClassDeclarationSyntax clazz)
	{
		foreach (var attr in clazz.AttributeLists)
		{
			// bool useAttribute = false;
			int i = 0;
			DebugLog("> AttrNode: " + attr.ToFullString());
			foreach (var item in attr.ChildNodes())
			{
				DebugLog(">> ChildNode: " + item.ToFullString());
				foreach (var item2 in item.ChildNodes())
				{
					DebugLog(">>> ChildNode: " + item2.ToFullString());
					if (item2.ToFullString() == "Schema")
					{
						DebugLog("!!! " + i);
					}
					foreach (var item3 in item2.ChildNodes())
					{
						DebugLog(">>>> ChildNode: " + item3.ToFullString());
					}
					if (i++ == 0)
					{
						if (item2.ToFullString() == "Schema")
						{
							DebugLog("! SELECTED NEXT AFTER: " + item2.ToFullString());
							i = -1;
							continue;
						}
					}
					else if (i == 0)
					{
						int sc = 0;
						double ver = 0;
						DebugLog("! ChildNode: " + item2.ToFullString());
						foreach (var item3 in item2.ChildNodes())
						{
							DebugLog(">>>> ChildNode: " + item3.ToFullString());
							if (sc++ == 0)
							{
								if (double.TryParse(item3.ToFullString(), out ver))
								{
									DebugLog("!!! Schema Version: " + ver);
									continue;
								}
							}
							else
							{
								var s = item3.ToFullString().Trim('"');
								DebugLog("!!! Schema String: " + s);
								yield return (ver, s);
								break;
							}
						}
					}
				}

				if (i == -1)
				{
					/*
					EmergencyLog.Default.Debug("! ChildNode: " + item.ToFullString());
					foreach (var item2 in item.ChildNodes())
					{
						EmergencyLog.Default.Debug(">> ChildNode: " + item2.ToFullString());
						foreach (var item3 in item2.ChildNodes())
						{
							EmergencyLog.Default.Debug(">>> ChildNode: " + item3.ToFullString());
						}
						if (sc++ == 0)
						{
						}
						else
						{

						}
					}
					*/
				}
				else if (i++ == 0)
				{
					if (item.ToFullString() == "Schema")
					{
						i = -1;
						// continue;
					}
				}
			}
		}
	}	


	static string GetSchemaTypeDeclaration(TypeSyntax propertyType)
	{
		return propertyType.ToString();
		/*
		switch (type)
		{
			case Type t when t == typeof(string):
				return "str?";
			default:
				break;
		}
		*/
	}

	static bool HasIgnoreAttribute(IPropertySymbol p)
	{
		foreach (var attr in p.GetAttributes())
		{
			var fullName = attr.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			if (fullName is "global::System.Text.Json.Serialization.JsonIgnoreAttribute"
				|| fullName is "global::SbxIgnoreAttribute"
				|| fullName is "SbxIgnoreAttribute")
			{
				return true;
			}
		}
		return false;
	}

	static IEnumerable<IPropertySymbol> GetAllInstancePropertiesOfType(INamedTypeSymbol type)
	{
		foreach (var p in type.GetMembers().OfType<IPropertySymbol>())
		{
			if (p.IsStatic) continue;
			if (p.IsIndexer) continue;
			// Skip private members of base types
			if (p.DeclaredAccessibility == Accessibility.Private && !SymbolEqualityComparer.Default.Equals(p.ContainingType, type))
				continue;
			// if (!seen.Add(p.Name)) continue; // prefer most-base

			// Exclude properties marked with [JsonIgnore] or [SbxIgnore]
			if (p.GetAttributes().Any())
			{
				DebugLog($"Syncron Serializing Generator {p.Name} {p.GetAttributes()[0]} | {p.GetAttributes()[0].AttributeClass?.ToDisplayString()}");
			}
			if (HasIgnoreAttribute(p))
			{
				DebugLog($"Syncron Serializing Generator Ignored {p.Name} by {p.GetAttributes()[0].AttributeClass?.ToDisplayString()}");
				continue;
			}

			yield return p;
		}
	}

	// Enumerate instance properties across the full inheritance chain, most-base first, no duplicates by name.
	static IEnumerable<IPropertySymbol> GetAllInstancePropertiesWithAncestors(INamedTypeSymbol type)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var types = new List<INamedTypeSymbol>();
		for (var t = type; t != null; t = t.BaseType)
		{
			types.Add(t);
		}
		types.Reverse(); // base types first

		foreach (var t in types)
		{
			foreach (var p in GetAllInstancePropertiesOfType(t))
			{
				yield return p;
			}
		}
	}

	/// <summary>
	/// This method is where the real work of the generator is done
	/// This ensures optimal performance by only executing the generator when needed
	/// The method can be named whatever you want but Execute seems to be the standard 
	/// </summary>
	static void Execute(SourceProductionContext context, TheCombinedSource combinedData)
	{
		var errorBody = new StringBuilder();

		// convey an error message from analyzer:
		if (combinedData.ClassData.errorMessage is not null || combinedData.ClassData.exception is not null)
		{
			var message = combinedData.ClassData.exception is null
				? combinedData.ClassData.errorMessage ?? "Model binding generation error"
				: $"{combinedData.ClassData.errorMessage ?? "Model binding generation error"}; {combinedData.ClassData.exception}";

			context.ReportDiagnostic(Diagnostic.Create(
				GenerationFailureDiagnostic,
				combinedData.ClassData.Clazz?.Identifier.GetLocation() ?? Location.None,
				message));
			return;
		}
		string filePath = string.Empty;
		try
		{
			var classData = combinedData.ClassData;
			string tfm = combinedData.BuildProps.Tfm;
			string SynqraBuildBox = combinedData.BuildProps.SynqraBuildBox;
			if (tfm == null || classData.Clazz is null)
			{
				return;
			}

			var netVer = tfm.StartsWith("net") ? Version.TryParse(tfm[3..], out var version) ? version : null : null;
			var doesSupportField = false; // netVer != null && netVer.Major >= 6;

			var clazz = classData.Clazz;
			filePath = clazz.SyntaxTree.FilePath;

			var classMembers = classData.Clazz.Members;
			DebugLog($"GENERATE FOR {clazz.Identifier} : {classData.Data.BaseType} ({clazz.SyntaxTree.FilePath})...");

			INamedTypeSymbol rootType = classData.Data;
			while (rootType.BaseType is not null && rootType.BaseType.SpecialType != SpecialType.System_Object)
			{
				DebugLog($"{rootType} PARENT IS {rootType.BaseType}");
				rootType = rootType.BaseType;
			}

			bool isRootType = classData.Data.BaseType is null || classData.Data.BaseType.SpecialType == SpecialType.System_Object;

			// check if the methods we want to add exist already 
			// var setMethod = classMembers.FirstOrDefault(member => member is MethodDeclarationSyntax method && method.Identifier.Text == "Set");

			// this string builder will hold our source code for the methods we want to add
			var body = new StringBuilder();
			body.AppendLine("#nullable enable");
			HashSet<string> usingsSet = new HashSet<string>();
			List<string> usingsList = new List<string>();
			void Add(string usingStatement)
			{
				if (usingsSet.Add(usingStatement))
				{
					usingsList.Add(usingStatement);
				}
			}
			Add("using Synqra;");
			Add("using System.ComponentModel;");
			foreach (var usingStatement in clazz.SyntaxTree.GetCompilationUnitRoot().Usings)
			{
				Add(usingStatement.ToString());
			}
			foreach (var usingStatement in usingsList)
			{
				body.AppendLine(usingStatement.ToString());
			}

			body.AppendLine();

			BaseNamespaceDeclarationSyntax? calcClassNamespace = clazz.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
			calcClassNamespace ??= clazz.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();

			if (calcClassNamespace is null)
			{
				EmergencyLog.Default.Error($"Could not find namespace for {clazz.Identifier}", null);
			}
			// DebugLog($"Found calcClassNamespace={calcClassNamespace?.Name}");
			body.AppendLine($"");
			body.AppendLine($"namespace {calcClassNamespace?.Name};");
			body.AppendLine();
			// body.AppendLine($"// Synqra Model Target: {Synqra.SynqraTargetInfo.TargetFramework}");
			body.AppendLine();
			var ifaces = ($" : {FQN(classData.Ibm)}, {FQN(classData.Ipc)}, {FQN(classData.Ipcg)}");
			body.AppendLine($"{clazz.Modifiers} class {clazz.Identifier}{(isRootType ? ifaces : null)}");
			body.AppendLine("{");

			body.AppendLine($"\tstatic {clazz.Identifier}()");
			body.AppendLine($"\t{{");
			body.AppendLine($"\t\tSynqraJsonTypeInfoResolver.RegisterGeneratedModel<{clazz.Identifier}>();");
			body.AppendLine($"\t}}");
			body.AppendLine($"");


			#region SchemaDetection
			string suggestedSchema = "1";
			// Append inherited properties to schema suggestion (minimally qualified to keep it compact)
			foreach (var pro in GetAllInstancePropertiesWithAncestors(classData.Data)/*.Where(p => !classData.Data.Equals(p.ContainingType, SymbolEqualityComparer.Default))*/)
			{
				suggestedSchema += " " + pro.Name + " " + pro.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
			}

			// EmergencyLog.Default.Debug("[+] Added methods to generated class");

			var originalSourceContent = clazz.SyntaxTree.GetText().ToString();
			DebugLog($"EXECUTE SyntaxTree.FilePath={clazz.SyntaxTree.FilePath}");
			var line = clazz.SyntaxTree.GetLineSpan(clazz.GetLocation().SourceSpan).StartLinePosition.Line;

			var schemas = GetAllSchemasSymbol(classData.Data, classData.Ssa).ToArray();
			if (clazz.AttributeLists.Count == 0)
			{
				// DebugLog($"GetLineSpan() {line} {clazz.Span.Start}");
			}
			else
			{
				var a = clazz.AttributeLists.First().Span.Start;
				var b = clazz.AttributeLists.First().Span.End;
				var c = clazz.AttributeLists.Last().Span.Start;
				var d = clazz.AttributeLists.Last().Span.End;
				// DebugLog($"GetLineSpan() {line}/{a}/{b}/{c}/{d}");

				var lastSchemaEntry = schemas.Length == 0
					? (0d, string.Empty)
					: schemas.OrderBy(s => s.Item1).Last();
				double lastVer = lastSchemaEntry.Item1;
				string lastSchema = lastSchemaEntry.Item2;
				var sb = new StringBuilder(originalSourceContent);
				if (lastSchema != suggestedSchema)
				{
					var now = DateTime.Now;
					var year1 = new DateTime(now.Date.Year, 1, 1);
					var year2 = new DateTime(now.Date.Year + 1, 1, 1);
					var ver = now.Year + Math.Round((now - year1).TotalHours / (year2 - year1).TotalHours, 3);
					if (lastVer >= ver)
					{
						ver = lastVer + 0.001;
					}
					DebugLog($"*********** Schema drift! path={clazz.SyntaxTree.FilePath} clazz={clazz}");
					sb.Insert(d, $"\r\n[Schema({ver:F3}, \"{suggestedSchema}\")]");
					CodeGenUtils.Default.WriteFile(SynqraBuildBox, clazz.SyntaxTree.FilePath, originalSourceContent, sb.ToString());
				}
				else
				{
					DebugLog("*********** Schema already present as latest: " + lastSchema);
				}
				// EmergencyLog.Default.Debug(sb.ToString());

			}
			#endregion

			if (isRootType)
			{
				body.AppendLine("""
	[ThreadStatic]
	protected static bool _assigning; // when true, the source of the change is model binding due to new events reaching the context, so it is external change. This way, when setter see false here - it means the source is a client code, direct property change by consumer.

	public event PropertyChangedEventHandler? PropertyChanged;
	public event PropertyChangingEventHandler? PropertyChanging;

	protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	protected void OnPropertyChanging(string propertyName) => PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(propertyName));

	protected global::Synqra.ISynqraStoreContext? _store;

	global::Synqra.ISynqraStoreContext? IBindableModel.Store
	{
		get => _store;
		set
		{
			if (_store is not null && _store != value)
			{
				throw new global::System.InvalidOperationException("Store can only be set once.");
			}
			_store = value;
		}
	}

	void IBindableModel.Set(string name, object? value)
	{
		var previous = _assigning;
		_assigning = true;
		try
		{
			SetCore(name, value);
		}
		finally
		{
			_assigning = previous;
		}
	}

	void IBindableModel.Get(ISBXSerializer serializer, float version, in Span<byte> buffer, ref int pos)
	{
		GetCore(serializer, version, in buffer, ref pos);
	}

	void IBindableModel.Set(ISBXSerializer serializer, float version, in ReadOnlySpan<byte> buffer, ref int pos)
	{
		SetCore(serializer, version, in buffer, ref pos);
	}

	protected virtual void SetCore(string name, object? value)
	{
		switch (name)
		{
""");
				// Include properties from this class and all base classes that have a setter
				foreach (var pro in GetAllInstancePropertiesWithAncestors(classData.Data).Where(p => p.SetMethod is not null))
				{
					if (pro.Type.ToString() == "int")
					{
						body.AppendLine($$"""
			case "{{pro.Name}}":
				if (value is long l)
				{
					this.{{pro.Name}} = ({{FQN(pro.Type)}})l;
				} else {
					this.{{pro.Name}} = ({{FQN(pro.Type)}})value!;
				}
				break;
""");
					}
					else
					{
						body.AppendLine($$"""
			case "{{pro.Name}}":
				this.{{pro.Name}} = ({{FQN(pro.Type)}})value!;
				break;
""");
					}
				}
				body.AppendLine(
	"""
		}
	}

	protected virtual void GetCore(ISBXSerializer serializer, float version, in Span<byte> buffer, ref int pos)
	{
""");
				string? els = null;
				FormattableString x;
				body.AppendLine($"\t\tEmergencyLog.Default.Debug($\"Syncron Serializing {clazz.Identifier} IBindableModel.Get\");");

#if DEBUG
				var isUnitTest = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "nunit.engine");
				if (isUnitTest)
				{
					schemas = [(1.2f, "1")];
				}
#endif
				bool any = false;
				foreach (var item in schemas)
				{
					any = true;
					x = $"\t\t{els}if (version == {item.Item1}f)"; body.AppendLine(x.ToString(System.Globalization.CultureInfo.InvariantCulture));
					body.AppendLine($"\t\t{{");
					body.AppendLine($"\t\t\tEmergencyLog.Default.Debug($\"Syncron Serializing {clazz.Identifier} IBindableModel.Get - if schema {item.Item1}\");");
					body.AppendLine($"\t\t\t// Positional Fields:");
					// Serialize all readable instance properties (this type + base types)
					foreach (var pro in GetAllInstancePropertiesWithAncestors(classData.Data))
					{
						body.AppendLine($"\t\t\tEmergencyLog.Default.Debug($\"Syncron Serializing {clazz.Identifier} IBindableModel.Get - {item.Item1} {pro.Name}\");");
						// Use backing field only for properties declared in this class when we generated one; otherwise use the property
						var access = (!doesSupportField && SymbolEqualityComparer.Default.Equals(pro.ContainingType, classData.Data))
							? GetFieldName(pro, doesSupportField: false)
							: "this." + pro.Name;
						body.AppendLine($"\t\t\tserializer.Serialize(in buffer, {access}, ref pos);");
					}
					body.AppendLine($"\t\t}}");
					els = "else ";
				}
				if (any)
				{
					body.AppendLine($"\t\telse");
					body.AppendLine($"\t\t{{");
					body.AppendLine($"\t\t\tEmergencyLog.Default.Error($\"Syncron Serializing {clazz.Identifier} IBindableModel.Get - unknown version {{version}}\");");
					body.AppendLine($"\t\t\tthrow new Exception($\"Unknown schema version {{version}} of {clazz.Identifier}\");");
					body.AppendLine($"\t\t}}");
				}
				body.AppendLine("""
	}


	protected virtual void SetCore(ISBXSerializer serializer, float version, in ReadOnlySpan<byte> buffer, ref int pos)
	{
""");
					els = null;
					foreach (var item in schemas)
					{
						any = true;
						x = $"\t\t{els}if (version == {item.Item1}f)"; body.AppendLine(x.ToString(System.Globalization.CultureInfo.InvariantCulture));
						body.AppendLine($"\t\t{{");
						body.AppendLine($"\t\t\t// Positional Fields:");
						// Deserialize into all writable instance properties (this type + base types)
						foreach (var pro in GetAllInstancePropertiesWithAncestors(classData.Data))
						{
							var target = (!doesSupportField && SymbolEqualityComparer.Default.Equals(pro.ContainingType, classData.Data))
								? GetFieldName(pro)
								: "this." + pro.Name;
							body.AppendLine($"\t\t\t{target} = ({FQN(pro.Type)})serializer.Deserialize{DeserializeMethod(pro.Type, debug: classData.Data.Name)}(in buffer, ref pos);");
						}
						body.AppendLine($"\t\t}}");
						els = "else ";
					}
					if (any)
					{
						body.AppendLine($"\t\telse");
					}
					body.AppendLine($"\t\t{{");
					body.AppendLine($"\t\t\tEmergencyLog.Default.Error($\"Syncron Serializing {clazz.Identifier} IBindableModel.Set - unknown version {{version}}\");");
					body.AppendLine($"\t\t\tthrow new Exception($\"Unknown schema version {{version}} of {clazz.Identifier}\");");
					body.AppendLine($"\t\t}}");

					body.AppendLine("""
						}
""");
			}
			else
			{
				body.AppendLine($$"""
	protected override void SetCore(string name, object? value)
	{
		switch (name)
		{
""");
				// Include properties from this class and all base classes that have a setter
				foreach (var pro in GetAllInstancePropertiesOfType(classData.Data))
				{
					body.AppendLine($$"""
			case "{{pro.Name}}":
				this.{{pro.Name}} = ({{FQN(pro.Type)}})value!;
				break;
""");
				}
				body.AppendLine(
"""
			default:
				base.SetCore(name, value);
				break;
		}
	}

	protected override void GetCore(ISBXSerializer serializer, float version, in Span<byte> buffer, ref int pos)
	{
""");
				string? els = null;
				FormattableString x;
				body.AppendLine($"\t\tEmergencyLog.Default.Debug($\"Syncron Serializing {clazz.Identifier} IBindableModel.Get\");");

#if DEBUG
				var isUnitTest = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "nunit.engine");
				if (isUnitTest)
				{
					schemas = [(1.2f, "1")];
				}
#endif
				bool any = false;
				foreach (var item in schemas)
				{
					any = true;
					x = $"\t\t{els}if (version == {item.Item1}f)"; body.AppendLine(x.ToString(System.Globalization.CultureInfo.InvariantCulture));
					body.AppendLine($"\t\t{{");
					body.AppendLine($"\t\t\tEmergencyLog.Default.Debug($\"Syncron Serializing {clazz.Identifier} IBindableModel.Get - if schema {item.Item1}\");");
					body.AppendLine($"\t\t\t// Positional Fields:");
					// Serialize all readable instance properties (this type + base types)
					foreach (var pro in GetAllInstancePropertiesWithAncestors(classData.Data))
					{
						body.AppendLine($"\t\t\tEmergencyLog.Default.Debug($\"Syncron Serializing {clazz.Identifier} IBindableModel.Get - {item.Item1} {pro.Name}\");");
						// Use backing field only for properties declared in this class when we generated one; otherwise use the property
						var access = (!doesSupportField && SymbolEqualityComparer.Default.Equals(pro.ContainingType, classData.Data))
							? GetFieldName(pro)
							: "this." + pro.Name;
						body.AppendLine($"\t\t\tserializer.Serialize(in buffer, {access}, ref pos);");
					}
					body.AppendLine($"\t\t}}");
					els = "else ";
				}
				if (any)
				{
					body.AppendLine($"\t\telse");
					body.AppendLine($"\t\t{{");
					body.AppendLine($"\t\t\tEmergencyLog.Default.Error($\"Syncron Serializing {clazz.Identifier} IBindableModel.Get - unknown version {{version}}\");");
					body.AppendLine($"\t\t\tthrow new Exception($\"Unknown schema version {{version}} of  {clazz.Identifier} \");");
					body.AppendLine($"\t\t}}");
				}
				body.AppendLine("""
	}

	protected override void SetCore(ISBXSerializer serializer, float version, in ReadOnlySpan<byte> buffer, ref int pos)
	{
""");
				els = null;
				foreach (var item in schemas)
				{
					any = true;
					x = $"\t\t{els}if (version == {item.Item1}f)"; body.AppendLine(x.ToString(System.Globalization.CultureInfo.InvariantCulture));
					body.AppendLine($"\t\t{{");
					body.AppendLine($"\t\t\t// Positional Fields:");
					// Deserialize into all writable instance properties (this type + base types)
					foreach (var pro in GetAllInstancePropertiesWithAncestors(classData.Data).Where(p => p.SetMethod is not null))
					{
						var target = (!doesSupportField && SymbolEqualityComparer.Default.Equals(pro.ContainingType, classData.Data))
							? GetFieldName(pro)
							: "this." + pro.Name;
						body.AppendLine($"\t\t\t{target} = ({FQN(pro.Type)})serializer.Deserialize{DeserializeMethod(pro.Type, debug: classData.Data.Name)}(in buffer, ref pos);");
					}
					body.AppendLine($"\t\t}}");
					els = "else ";
				}
				if (any)
				{
					body.AppendLine($"\t\telse");
				}
				body.AppendLine($"\t\t{{");
				body.AppendLine($"\t\t\tEmergencyLog.Default.Error($\"Syncron Serializing {clazz.Identifier} IBindableModel.Set - unknown version {{version}}\");");
				body.AppendLine($"\t\t\tthrow new Exception($\"Unknown schema version {{version}} of  {clazz.Identifier} \");");
				body.AppendLine($"\t\t}}");

				body.AppendLine("""
	}
""");
					

			}

			#region Fields and Properties

			body.AppendLine($$"""

""");
			// First: properties declared in this class (keep original textual type form to minimize schema noise)
			foreach (var pro in clazz.Members.OfType<PropertyDeclarationSyntax>())
			{
				if (!pro.Modifiers.Any(x => x.ToString() == "partial"))
				{
					continue;
				}
				// suggestedSchema += " " + pro.Identifier + " " + GetSchemaTypeDeclaration(pro.Type);

				if (!doesSupportField)
				{
					body.AppendLine(
$$"""
	private {{pro.Type}} {{GetFieldName(pro)}};
""");
				}
				body.AppendLine(
$$"""

	// tfm={{tfm}}	// doesSupportField={{doesSupportField}}

	partial void On{{pro.Identifier}}Changing({{pro.Type}} value);
	partial void On{{pro.Identifier}}Changing({{pro.Type}} oldValue, {{pro.Type}} value);
	partial void On{{pro.Identifier}}Changed({{pro.Type}} value);
	partial void On{{pro.Identifier}}Changed({{pro.Type}} oldValue, {{pro.Type}} value);

	public {{(pro.Modifiers.Any(x=>x.ToString() == "required") ? "required ":"")}}partial {{pro.Type}} {{pro.Identifier}}
	{
		get => {{(doesSupportField ? "field" : GetFieldName(pro))}};
		set
		{
			var oldValue = {{(doesSupportField ? "field" : GetFieldName(pro))}};
			if (_assigning || _store is null)
			{
				On{{pro.Identifier}}Changing(value);
				On{{pro.Identifier}}Changing(oldValue, value);
				OnPropertyChanging(nameof({{pro.Identifier}}));
				{{(doesSupportField ? "field" : GetFieldName(pro))}} = value;
				On{{pro.Identifier}}Changed(value);
				On{{pro.Identifier}}Changed(oldValue, value);
				OnPropertyChanged(nameof({{pro.Identifier}}));
			}
			else
			{
				On{{pro.Identifier}}Changing(value);
				On{{pro.Identifier}}Changing(oldValue, value);
				_store.SubmitCommandAsync(new ChangeObjectPropertyCommand
				{
					CommandId = GuidExtensions.CreateVersion7(),
					ContainerId = default,
					CollectionId = default,

					Target = this,
					TargetId = _store.GetId(this),
					TargetTypeId = default,
					// TargetTypeId = _store.GetId(this),

					PropertyName = nameof({{pro.Identifier}}),
					OldValue = oldValue,
					NewValue = value
				}).GetAwaiter().GetResult();
			}
		}
	}

""");
			}

			#endregion

			body.AppendLine("}");

			// EmergencyLog.Default.Debug("!! SynqraBuildBox = " + SynqraBuildBox);


			//to write our source file we can use the context object that was passed in
			//this will automatically use the path we provided in the target projects csproj file
			var fileName = $"{Path.GetFileNameWithoutExtension(clazz.SyntaxTree.FilePath)}_{clazz.Identifier}.Generated.cs";
			context.AddSource(fileName, SourceText.From(body.ToString(), Encoding.UTF8));
			DebugLog($"[+] Added source to context {fileName}");
			DebugLog($"GENERATED FOR {clazz.Identifier} ({clazz.SyntaxTree.FilePath})");
		}
		catch (Exception ex)
		{
			errorBody.AppendLine("#error CodeGenerationException");
			errorBody.AppendLine("// ********** ERROR DURING CODE GENERATION **********");
			errorBody.AppendLine("// " + ex);
			var fileName = $"{Path.GetFileNameWithoutExtension(filePath)}.Errors.Generated.cs";
			context.AddSource(fileName, SourceText.From(errorBody.ToString(), Encoding.UTF8));
			try
			{
				EmergencyLog.Default.LogError(ex, $"Execute {ex}");
			}
			catch { }
			// throw;
		}
	}

	static string? GetFieldName(PropertyDeclarationSyntax syntax, bool? doesSupportField = null)
	{
		/*
		if (!doesSupportField && SymbolEqualityComparer.Default.Equals(pro.ContainingType, classData.Data))
		{
			return 
		}
		*/
		var identifier = syntax.Identifier.ToString();
		if (char.IsUpper(identifier[0]))
		{
			identifier = "_" + char.ToLowerInvariant(identifier[0]) + identifier[1..];
		}
		else
		{
			identifier = "_" + identifier;
		}
		return identifier;
	}

	static string? GetFieldName(IPropertySymbol symbol, bool? doesSupportField = null)
	{
		var identifier = symbol.Name.ToString();
		if (char.IsUpper(identifier[0]))
		{
			identifier = "_" + char.ToLowerInvariant(identifier[0]) + identifier[1..];
		}
		else
		{
			identifier = "_" + identifier;
		}
		return identifier;
	}

	static string? DeserializeMethod(ITypeSymbol type, string debug)
	{
		DebugLog($"[Type {debug}] <DeserializeMethod>: {type} ({type.GetType().Name})");
		var res = DeserializeMethodCore(type, debug: debug);
		DebugLog($"[Type {debug}] </DeserializeMethod>: {type} => {res}");
		return res;
	}
	static string? DeserializeMethodCore(ITypeSymbol type, string debug)
	{
		if (type is INamedTypeSymbol named)
		{
			// Handle Nullable<T>
			if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && named.TypeArguments.Length == 1)
				return DeserializeMethod(named.TypeArguments[0], debug: debug);

			// Handle primitive & predefined types
			switch (named.SpecialType)
			{
				case SpecialType.System_Boolean: return "Boolean";

				case SpecialType.System_SByte:
				case SpecialType.System_Int16:
				case SpecialType.System_Int32:
				case SpecialType.System_Int64:
					return "Signed";

				case SpecialType.System_Byte:
				case SpecialType.System_UInt16:
				case SpecialType.System_UInt32:
				case SpecialType.System_UInt64:
				case SpecialType.System_Char:
					return "Unsigned";

				case SpecialType.System_Single: return "Single";
				case SpecialType.System_Double: return "Double";
				case SpecialType.System_Decimal: return "Decimal";
				case SpecialType.System_String: return "String";
			}

			// Fallback for generics like List<T> or IReadOnlyList<T>
			if (named.IsGenericType &&
				named.TypeArguments.Length == 1 &&
				(named.Name is "IEnumerable" or "IList" or "IReadOnlyList" or "IReadOnlyCollection" or "List"))
			{
				DebugLog($"[Type {debug}] //// Lsit detected named.Name {named.Name}");

				var arg = named.TypeArguments[0];
				// return $"List<{arg.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>";
				return $"/*named.IsGenericType*/<{named.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>";
			}

			// Try matching implemented IEnumerable<T> interface
			DebugLog($"[Type {debug}] °2 {named}");
			if (named.ToString().EndsWith("IDictionary<string, object>")
				|| named.ToString().EndsWith("IDictionary<string, object>?")
				|| named.ToString().EndsWith("IDictionary<string, object?>")
				|| named.ToString().EndsWith("IDictionary<string, object?>?")
				)
			{
				return "Dict<string, object>";
			}
			foreach (var i in named.AllInterfaces)
			{
				DebugLog($"[Type {debug}] °1 Detected Interface: {i}");
			}
			foreach (var i in named.AllInterfaces)
			{
				if (i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
				{
					DebugLog($"[Type {debug}] °1 Selected Interface: {i}");
					return $"List/*SpecialType*/<{i.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>";
				}
			}
			// throw new Exception("Unknown collection type");
		}

		if (TryGetIDictionaryKeyAndElement(type, out var keyType, out var elementType1))
		{
			DebugLog($"[Type {debug}] //// Dictionary detected: {type} => Dict<{keyType}, {elementType1}>");
			return $"Dict<{keyType}, {elementType1}>";
		}
		else
		{
			DebugLog($"[Type {debug}] //// Unknown collection type detected: {type}");
		}

		// Handle IEnumerable<T> (and subclasses like List<T>, T[])
		if (TryGetIEnumerableElement(type, out var elementType2))
		{
			return $"List/*TryGetIEnumerableElement*/<{elementType2}>";
		}

		// Arrays as List<T>
		if (type is IArrayTypeSymbol array)
		{
			return $"List/*BottomIsArray*/<{array.ElementType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>";
		}

		return $"/*None*/<{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>";
	}


	static bool TryGetIEnumerableElement(ITypeSymbol type, out string elementTypeName)
	{
		// Handle arrays directly
		if (type is IArrayTypeSymbol array)
		{
			elementTypeName = array.ElementType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
			return true;
		}

		if (type is INamedTypeSymbol named)
		{
			// Directly generic IEnumerable<T>
			if (named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
			{
				elementTypeName = named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
				return true;
			}

			// Or any interface that implements it
			foreach (var i in named.AllInterfaces)
			{
				if (i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
				{
					elementTypeName = i.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
					return true;
				}
			}
		}

		elementTypeName = default!;
		return false;
	}

	static bool TryGetIDictionaryKeyAndElement(ITypeSymbol type, out string keyTypeName, out string elementTypeName)
	{
		keyTypeName = default!;
		elementTypeName = default!;

		// Handle arrays directly (not a dictionary, but for compatibility)
		if (type is IArrayTypeSymbol array)
		{
			elementTypeName = array.ElementType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
			return false;
		}

		if (type is INamedTypeSymbol named)
		{
			// Check if type is IDictionary<TKey, TValue> or Dictionary<TKey, TValue>
			if (named.IsGenericType &&
				(named.Name == "IDictionary" || named.Name == "Dictionary") &&
				named.TypeArguments.Length == 2)
			{
				keyTypeName = named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
				elementTypeName = named.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
				return true;
			}

			// Check interfaces for IDictionary<TKey, TValue>
			foreach (var i in named.AllInterfaces)
			{
				if (i.IsGenericType &&
					(i.Name == "IDictionary" || i.Name == "Dictionary") &&
					i.TypeArguments.Length == 2)
				{
					keyTypeName = i.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
					elementTypeName = i.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
					return true;
				}
			}
		}

		return false;
	}

	/*
	static string? DeserializeMethod(ITypeSymbol type)
	{
		// Pseudocode:
		// 1) If type is Nullable<T>, unwrap to T.
		// 2) If type is an array, return "List<elementType>".
		// 3) If type (or its interfaces) is/implements IEnumerable<T> (or IList<T>/IReadOnlyList<T>/IReadOnlyCollection<T>/ICollection<T>), return "List<T>".
		// 4) If type is an enum, map its underlying type to Signed/Unsigned suffix.
		// 5) Map primitive/special types to known suffixes (Boolean, Signed, Unsigned, Single, Double, Decimal, String).
		// 6) Otherwise return null (which results in calling serializer.Deserialize(...)).

		// 1) Unwrap Nullable<T>
		if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
		{
			type = named.TypeArguments[0];
		}

		// 2) Arrays => treat as List<T>
		if (type is IArrayTypeSymbol arrayType)
		{
			return $"List<{FQN(arrayType.ElementType)}>";
		}

		// 3) IEnumerable-like => treat as List<T>
		if (TryGetEnumerableElement(type, out var elementType))
		{
			return $"List<{FQN(elementType)}>";
		}

		// 4) Enums => map underlying type to Signed/Unsigned
		if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType && enumType.EnumUnderlyingType is { } underlying)
		{
			var enumSuffix = MapNumericSuffix(underlying.SpecialType);
			if (enumSuffix is not null)
				return enumSuffix;
		}

		// 5) Primitive/special types
		var suffix = MapSpecialTypeSuffix(type.SpecialType);
		if (suffix is not null)
			return suffix;

		// 6) Fallback: use generic Deserialize<T> (by returning null we produce "Deserialize(...)" in the generated code)
		return null;

		static string? MapSpecialTypeSuffix(SpecialType st)
		{
			switch (st)
			{
				case SpecialType.System_Boolean: return "Boolean";

				// signed integers
				case SpecialType.System_SByte:
				case SpecialType.System_Int16:
				case SpecialType.System_Int32:
				case SpecialType.System_Int64:
					return "Signed";

				// unsigned integers (+ char as unsigned)
				case SpecialType.System_Byte:
				case SpecialType.System_UInt16:
				case SpecialType.System_UInt32:
				case SpecialType.System_UInt64:
				case SpecialType.System_Char:
					return "Unsigned";

				case SpecialType.System_Single: return "Single";
				case SpecialType.System_Double: return "Double";
				case SpecialType.System_Decimal: return "Decimal";
				case SpecialType.System_String: return "String";

				default:
					return null;
			}
		}

		static string? MapNumericSuffix(SpecialType st)
		{
			// Map underlying enum numeric type to Signed/Unsigned
			switch (st)
			{
				case SpecialType.System_SByte:
				case SpecialType.System_Int16:
				case SpecialType.System_Int32:
				case SpecialType.System_Int64:
					return "Signed";

				case SpecialType.System_Byte:
				case SpecialType.System_UInt16:
				case SpecialType.System_UInt32:
				case SpecialType.System_UInt64:
				case SpecialType.System_Char:
					return "Unsigned";

				default:
					return null;
			}
		}

		static bool TryGetEnumerableElement(ITypeSymbol symbol, out ITypeSymbol elementType)
		{
			// Direct generic type check (e.g., IEnumerable<T>)
			if (symbol is INamedTypeSymbol named && named.Arity == 1 && IsEnumerableLike(named))
			{
				elementType = named.TypeArguments[0];
				return true;
			}

			// Check implemented interfaces (covers List<T>, IReadOnlyList<T>, etc.)
			foreach (var iface in symbol.AllInterfaces)
			{
				if (iface is INamedTypeSymbol inamed && inamed.Arity == 1 && IsEnumerableLike(inamed))
				{
					elementType = inamed.TypeArguments[0];
					return true;
				}
			}

			elementType = null!;
			return false;

			static bool IsEnumerableLike(INamedTypeSymbol s)
			{
				var def = s.ConstructedFrom?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				switch (def)
				{
					case "global::System.Collections.Generic.IEnumerable<T>":
					case "global::System.Collections.Generic.IList<T>":
					case "global::System.Collections.Generic.ICollection<T>":
					case "global::System.Collections.Generic.IReadOnlyList<T>":
					case "global::System.Collections.Generic.IReadOnlyCollection<T>":
						return true;
					default:
						return false;
				}
			}
		}
	}
	*/

	/*
	static string? DeserializeMethod(TypeSyntax type)
	{
		// Handle IEnumerable<T> (including fully-qualified names)
		if (TryGetIEnumerableElement(type, out var elementTypeName))
		{
			// Expecting serializer to expose: DeserializeEnumerable<T>(...)
			// return $"Enumerable<{elementTypeName}>";
			return $"List<{elementTypeName}>";
		}

		switch (type)
		{
			case NullableTypeSyntax nts:
				return DeserializeMethod(nts.ElementType);
			case PredefinedTypeSyntax pts:
				switch (pts.Keyword.Kind())
				{
					case SyntaxKind.BoolKeyword: return "Boolean";

					case SyntaxKind.ByteKeyword: return "Unsigned";
					case SyntaxKind.SByteKeyword: return "Signed";
					case SyntaxKind.IntKeyword: return "Signed";
					case SyntaxKind.UIntKeyword: return "Unsigned";
					case SyntaxKind.ShortKeyword: return "Signed";
					case SyntaxKind.UShortKeyword: return "Unsigned";
					case SyntaxKind.LongKeyword: return "Signed";
					case SyntaxKind.ULongKeyword: return "Unsigned";

					case SyntaxKind.FloatKeyword: return "Single";
					case SyntaxKind.DoubleKeyword: return "Double";
					case SyntaxKind.DecimalKeyword: return "Decimal";
					case SyntaxKind.StringKeyword: return "String";
					case SyntaxKind.CharKeyword: return "Unsigned";
					default:
						return "?? PTS: " + pts.Keyword.Kind() +"??";
				}
			case GenericNameSyntax gns when (
				   gns.Identifier.ValueText == "IEnumerable"
				|| gns.Identifier.ValueText == "IList"
				|| gns.Identifier.ValueText == "IReadOnlyList"
				|| gns.Identifier.ValueText == "IReadOnlyCollection"
				) && gns.TypeArgumentList.Arguments.Count == 1:
				// Fallback if top-level is directly IEnumerable<T> without qualifier
				// return $"Enumerable<{gns.TypeArgumentList.Arguments[0].ToString()}>";
				return $"List<{gns.TypeArgumentList.Arguments[0].ToString()}>";

			case QualifiedNameSyntax qns when qns.Right is GenericNameSyntax g2 && g2.Identifier.ValueText == "IEnumerable" && g2.TypeArgumentList.Arguments.Count == 1:
				// System.Collections.Generic.IEnumerable<T>
				// return $"Enumerable<{g2.TypeArgumentList.Arguments[0].ToString()}>";
				return $"List<{g2.TypeArgumentList.Arguments[0].ToString()}>";

			default:
				return $"?? {type} ({type.GetType().Name}) ??";
				// throw new Exception($"Unknown Deserialization method for {type} ({type.GetType().Name})");
		}
	}

	static bool TryGetIEnumerableElement(TypeSyntax t, out string elementTypeName)
	{
		switch (t)
		{
			case GenericNameSyntax g when g.Identifier.ValueText == "IEnumerable" && g.TypeArgumentList.Arguments.Count == 1:
				elementTypeName = g.TypeArgumentList.Arguments[0].ToString();
				return true;

			case QualifiedNameSyntax q when q.Right is GenericNameSyntax g2 && g2.Identifier.ValueText == "IEnumerable" && g2.TypeArgumentList.Arguments.Count == 1:
				elementTypeName = g2.TypeArgumentList.Arguments[0].ToString();
				return true;

			case AliasQualifiedNameSyntax a when a.Name is GenericNameSyntax g3 && g3.Identifier.ValueText == "IEnumerable" && g3.TypeArgumentList.Arguments.Count == 1:
				elementTypeName = g3.TypeArgumentList.Arguments[0].ToString();
				return true;

			default:
				elementTypeName = default!;
				return false;
		}
	}
	*/
}

/*
public sealed class SchemaDriftFix : CodeFixProvider
{
	public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("SYNQRA001");

	public override async Task RegisterCodeFixesAsync(CodeFixContext ctx)
	{
		var doc = ctx.Document;
		ctx.RegisterCodeFix(CodeAction.Create("Update schema", ct => ApplyAsync(doc, ct), nameof(SchemaDriftFix)), ctx.Diagnostics);
	}

	private async Task<Microsoft.CodeAnalysis.Document> ApplyAsync(Microsoft.CodeAnalysis.Document doc, CancellationToken ct)
	{
		var oldText = await doc.GetTextAsync(ct);
		var newText = "[Schema(11, \"New Schema!\")]"; // your generator’s logic/library
		return doc.WithText(SourceText.From(newText, oldText.Encoding ?? Encoding.UTF8));
	}
}
*/
