// Copy this to a model you need to trace:
// #define SYNQRA_CODEGEN_TRACE

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Synqra;
using Synqra.CodeGeneration;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using static Synqra.CodeGeneration.CodeGenHelpers;

using BuildPropsProviderT = (
	  string Tfm
	, string SynqraBuildBox
	);
using SbxClassesProviderT = (
	  string? errorMessage
	, System.Exception? exception
	// -- OR --
	, Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax Clazz
	, Microsoft.CodeAnalysis.INamedTypeSymbol Data
	, Microsoft.CodeAnalysis.INamedTypeSymbol Ibm
	, Microsoft.CodeAnalysis.INamedTypeSymbol Ssa
	, Synqra.CodeGeneration.LinkFrameworkSymbols LinkSymbols
	);

namespace Synqra.BinarySerializer.SourceGenerator;

using SbxCombinedSource = (
		SbxClassesProviderT ClassData
	, BuildPropsProviderT BuildProps
	);

[Generator(LanguageNames.CSharp)]
public class SbxBindingGenerator : IIncrementalGenerator
{
	private static readonly DiagnosticDescriptor MissingReferenceDiagnostic = new(
		id: "SBX001",
		title: "SBX serializer generator prerequisites missing",
		messageFormat: "Synqra.BinarySerializer source generation requires a reference to Synqra.Model and Synqra.BinarySerializer (missing type(s): {0})",
		category: "Synqra.SbxBindingGenerator",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor GenerationFailureDiagnostic = new(
		id: "SBX002",
		title: "SBX serializer generator failed",
		messageFormat: "{0}",
		category: "Synqra.SbxBindingGenerator",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	[Conditional("SYNQRA_CODEGEN_TRACE")]
	private static void DebugLog(string message)
	{
		EmergencyLog.Default.LogTrace(message);
	}

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		InitializeCore(context);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private void InitializeCore(IncrementalGeneratorInitializationContext context)
	{
		try
		{
			var buildPropsProvider = context.AnalyzerConfigOptionsProvider.Select((provider, _) =>
			{
				var g = provider.GlobalOptions;
				string Get(string name) => g.TryGetValue(name, out var v) ? v : string.Empty;
				var tfm = Get("build_property.TargetFramework");
				var SynqraBuildBox = Get("build_property.SynqraBuildBox");
				return (tfm, SynqraBuildBox);
			});

			var missingReferences = context.CompilationProvider.Select((comp, _) =>
			{
				var missing = new List<string>();
				if (comp.GetTypeByMetadataName("Synqra.IBindableModel") is null)
					missing.Add("Synqra.IBindableModel");
				if (comp.GetTypeByMetadataName("Synqra.SchemaAttribute") is null)
					missing.Add("Synqra.SchemaAttribute");
				if (comp.GetTypeByMetadataName("Synqra.BinarySerializer.ISbxSerializer") is null)
					missing.Add("Synqra.BinarySerializer.ISbxSerializer");
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

			var classesProvider = context.SyntaxProvider.CreateSyntaxProvider<SbxClassesProviderT>(
				predicate: static (SyntaxNode node, CancellationToken cancelToken) =>
				{
					try
					{
						return node is ClassDeclarationSyntax classDeclaration
							&& classDeclaration.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == "SynqraModel"))
							&& classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword);
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
						var classDeclaration = (ClassDeclarationSyntax)ctx.Node;
						var symbol = ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, cancelToken) ?? throw new Exception("symbol");
						var comp = ctx.SemanticModel.Compilation;
						var ibm = comp.GetTypeByMetadataName("Synqra.IBindableModel");
						var ssa = comp.GetTypeByMetadataName("Synqra.SchemaAttribute");
						if (ibm is null || ssa is null)
						{
							return (null, null, default!, default!, default!, default!, default!);
						}
						var linkSymbols = new LinkFrameworkSymbols(comp);
						cancelToken.ThrowIfCancellationRequested();
						return (null, null, classDeclaration, symbol, ibm, ssa, linkSymbols);
					}
					catch (Exception ex)
					{
						EmergencyLog.Default.Error($"transform", ex);
						return ($"Error processing class: {ex.Message}", ex, default!, default!, default!, default!, default!);
					}
				});

			context.RegisterSourceOutput(
				  classesProvider
				  .Combine(buildPropsProvider)
				, Execute
				);
		}
		catch (Exception ex)
		{
			EmergencyLog.Default.Error($"Initialize", ex);
			throw;
		}
	}

	static (double, string) GetSchemaData(AttributeData attr)
	{
		DebugLog("Schema: " + attr);
		return ((double)attr.ConstructorArguments[0].Value!, (string)attr.ConstructorArguments[1].Value!);
	}

	static IEnumerable<(double, string)> GetAllSchemasSymbol(ITypeSymbol symbol, ITypeSymbol schemaAttribute)
	{
		return symbol.GetAttributes()
			.Where(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, schemaAttribute))
			.Select(GetSchemaData);
	}

	static IEnumerable<(double, string)> GetAllSchemas(ClassDeclarationSyntax clazz)
	{
		foreach (var attr in clazz.AttributeLists)
		{
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
					// intentionally empty
				}
				else if (i++ == 0)
				{
					if (item.ToFullString() == "Schema")
					{
						i = -1;
					}
				}
			}
		}
	}

	/// <summary>
	/// Returns the suffix for the ISbxSerializer.Deserialize* method to call for the given property type.
	/// </summary>
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
			{
				var innerType = named.TypeArguments[0];
				if (innerType is INamedTypeSymbol innerNamed)
				{
					switch (innerNamed.SpecialType)
					{
						case SpecialType.None:
						{
							break;
						}
					}
				}
				return "Nullable" + DeserializeMethod(innerType, debug: debug);
			}

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
				return $"/*named.IsGenericType*/<{named.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>";
			}

			DebugLog($"[Type {debug}] °2 {named}");
			if (named.ToString().EndsWith("IDictionary<string, object>")
				|| named.ToString().EndsWith("IDictionary<string, object>?")
				|| named.ToString().EndsWith("IDictionary<string, object?>")
				|| named.ToString().EndsWith("IDictionary<string, object?>?")
				)
			{
				return "Dict<string, object>";
			}
			// Concrete IDictionary-derived types (e.g. ObjectData : Dictionary<string, object?>)
			// must be detected as dictionaries here, before the IEnumerable fallback below would
			// otherwise mis-classify them as List<KeyValuePair<,>> (Dictionary implements both).
			if (TryGetIDictionaryKeyAndElement(named, out var namedDictKey, out var namedDictElem))
			{
				return $"Dict<{namedDictKey}, {namedDictElem}>";
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

		if (TryGetIEnumerableElement(type, out var elementType2))
		{
			return $"List/*TryGetIEnumerableElement*/<{elementType2}>";
		}

		if (type is IArrayTypeSymbol array)
		{
			return $"List/*BottomIsArray*/<{array.ElementType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>";
		}

		return $"/*None*/<{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>";
	}

	static bool TryGetIEnumerableElement(ITypeSymbol type, out string elementTypeName)
	{
		if (type is IArrayTypeSymbol array)
		{
			elementTypeName = array.ElementType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
			return true;
		}

		if (type is INamedTypeSymbol named)
		{
			if (named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
			{
				elementTypeName = named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
				return true;
			}

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

		if (type is IArrayTypeSymbol array)
		{
			elementTypeName = array.ElementType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
			return false;
		}

		if (type is INamedTypeSymbol named)
		{
			if (named.IsGenericType &&
				(named.Name == "IDictionary" || named.Name == "Dictionary") &&
				named.TypeArguments.Length == 2)
			{
				keyTypeName = named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
				elementTypeName = named.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
				return true;
			}

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

	/// <summary>
	/// Parses a schema string like "1 OldName string NewProp int" into ordered (name, type) pairs.
	/// The first token is the schema format version prefix and is skipped.
	/// </summary>
	static List<(string Name, string Type)> ParseSchemaFields(string schemaString)
	{
		var result = new List<(string, string)>();
		if (string.IsNullOrWhiteSpace(schemaString))
		{
			return result;
		}

		var tokens = schemaString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
		// First token is the schema format prefix (e.g. "1"), skip it.
		for (int i = 1; i + 1 < tokens.Length; i += 2)
		{
			result.Add((tokens[i], tokens[i + 1]));
		}
		return result;
	}

	/// <summary>
	/// Builds a mapping from any schema field name (including old/renamed names) to the current property name.
	/// Walks consecutive schema versions: if a name disappears and a new name appears at the same position
	/// with the same type, it is treated as a rename.
	/// </summary>
	static Dictionary<string, string> BuildSchemaFieldMapping(
		(double Version, string SchemaString)[] schemas
		, HashSet<string> currentPropertyNames)
	{
		var map = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var name in currentPropertyNames)
		{
			map[name] = name;
		}

		if (schemas.Length == 0)
		{
			return map;
		}

		var sorted = schemas.OrderBy(s => s.Version).ToArray();
		var parsedSchemas = sorted.Select(s => (s.Version, Fields: ParseSchemaFields(s.SchemaString))).ToArray();

		for (int i = 0; i < parsedSchemas.Length - 1; i++)
		{
			var olderFields = parsedSchemas[i].Fields;
			var newerFields = parsedSchemas[i + 1].Fields;
			var newerNames = new HashSet<string>(newerFields.Select(f => f.Name), StringComparer.Ordinal);

			for (int pos = 0; pos < olderFields.Count; pos++)
			{
				var oldField = olderFields[pos];
				if (newerNames.Contains(oldField.Name))
				{
					continue;
				}

				if (pos < newerFields.Count && newerFields[pos].Type == oldField.Type)
				{
					var newName = newerFields[pos].Name;
					var olderNames = new HashSet<string>(olderFields.Select(f => f.Name), StringComparer.Ordinal);
					if (!olderNames.Contains(newName))
					{
						map[oldField.Name] = newName;
					}
				}
			}
		}

		// Resolve transitive renames: A→B, B→C becomes A→C
		foreach (var key in map.Keys.ToList())
		{
			var resolved = map[key];
			var visited = new HashSet<string>(StringComparer.Ordinal) { key };
			while (map.TryGetValue(resolved, out var next) && next != resolved && visited.Add(next))
			{
				resolved = next;
			}
			map[key] = resolved;
		}

		return map;
	}

	/// <summary>
	/// For a given schema version, returns the ordered list of fields to serialize/deserialize,
	/// each resolved to the current property symbol. Returns null entries for fields that
	/// cannot be mapped (removed fields).
	/// </summary>
	static List<(string SchemaFieldName, IPropertySymbol? Property)> ResolveSchemaFieldsForVersion(
		string schemaString
		, Dictionary<string, string> fieldMapping
		, Dictionary<string, IPropertySymbol> propertyLookup)
	{
		var schemaFields = ParseSchemaFields(schemaString);
		var result = new List<(string, IPropertySymbol?)>(schemaFields.Count);

		foreach (var (name, type) in schemaFields)
		{
			string currentName = fieldMapping.TryGetValue(name, out var mapped) ? mapped : name;
			propertyLookup.TryGetValue(currentName, out var prop);
			result.Add((name, prop));
		}

		return result;
	}

	static void Execute(SourceProductionContext context, SbxCombinedSource combinedData)
	{
		var errorBody = new StringBuilder();

		if (combinedData.ClassData.errorMessage is not null || combinedData.ClassData.exception is not null)
		{
			var message = combinedData.ClassData.exception is null
				? combinedData.ClassData.errorMessage ?? "SBX binding generation error"
				: $"{combinedData.ClassData.errorMessage ?? "SBX binding generation error"}; {combinedData.ClassData.exception}";

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

			var exclude = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)
			{
				classData.Ibm,
			};

			string tfm = combinedData.BuildProps.Tfm;
			string SynqraBuildBox = combinedData.BuildProps.SynqraBuildBox;
			if (tfm == null || classData.Clazz is null)
			{
				return;
			}

			var clazz = classData.Clazz;
			filePath = clazz.SyntaxTree.FilePath;

			DebugLog($"SBX GENERATE FOR {clazz.Identifier} ({clazz.SyntaxTree.FilePath})...");

			bool isRootType = classData.Data.BaseType is null || classData.Data.BaseType.SpecialType == SpecialType.System_Object;
			bool isSealed = classData.Data.IsSealed;
			var virtualKeyword = isSealed ? "" : " virtual";

			// -- Schema detection / drift --
			// Opt-in link nav setters (e.g. Parent { get; set; }) have both accessors but are a live
			// query backed by SetSingle, not a stored field — they must never reach the binary schema.
			string suggestedSchema = "1";
			foreach (var pro in GetAllInstancePropertiesWithAncestors(classData.Data, exclude))
			{
				if (TryGetLinkNav(pro, classData.LinkSymbols) is not null)
				{
					continue;
				}
				suggestedSchema += " " + pro.Name + " " + pro.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
			}

			var originalSourceContent = clazz.SyntaxTree.GetText().ToString();
			var schemas = GetAllSchemasSymbol(classData.Data, classData.Ssa).ToArray();

			if (clazz.AttributeLists.Count > 0)
			{
				var a = clazz.AttributeLists.First().Span.Start;
				var b = clazz.AttributeLists.First().Span.End;
				var c = clazz.AttributeLists.Last().Span.Start;
				var d = clazz.AttributeLists.Last().Span.End;
				var lastAttribute = originalSourceContent.Substring(c, (d - c));
				var iIdx = lastAttribute.IndexOf('"');
				var eIdx = lastAttribute.LastIndexOf('"');
				// The last attribute is only a [Schema("...")] fallback source when it actually
				// carries a quoted string. When it doesn't (e.g. [SynqraModel] alone, or
				// [Component(IsUnique = true)] with no quotes), there is no textual schema to
				// extract — treat it as empty rather than computing Substring(0, -1), which would
				// throw and make the generator emit a broken .Errors.Generated.cs. An empty
				// fallback lets the drift detector below seed a fresh [Schema] attribute.
				var lastAttributeSchema = (iIdx >= 0 && eIdx > iIdx) ? lastAttribute.Substring(iIdx + 1, eIdx - iIdx - 1) : string.Empty;

				var lastSchemaEntry = schemas.Length == 0
					? (0d, string.Empty)
					: schemas.OrderBy(s => s.Item1).Last();
				double lastVer = lastSchemaEntry.Item1;
				string lastSchema = string.IsNullOrWhiteSpace(lastSchemaEntry.Item2) ? lastAttributeSchema : lastSchemaEntry.Item2;
				var sb = new StringBuilder(originalSourceContent);

				if (lastSchema != suggestedSchema)
				{
					DebugLog($"Schema drift! path={clazz.SyntaxTree.FilePath} lastSchema={lastSchema} suggestedSchema={suggestedSchema}");
					var now = DateTime.Now;
					var year1 = new DateTime(now.Date.Year, 1, 1);
					var year2 = new DateTime(now.Date.Year + 1, 1, 1);
					var ver = now.Year + Math.Round((now - year1).TotalHours / (year2 - year1).TotalHours, 3);
					if (lastVer >= ver)
					{
						ver = lastVer + 0.001;
					}
					sb.Insert(d, FormattableString.Invariant($"\r\n[Schema({ver:F3}, \"{suggestedSchema}\")]"));
					CodeGenUtils.Default.WriteFile(SynqraBuildBox, clazz.SyntaxTree.FilePath, originalSourceContent, sb.ToString());
				}
				else
				{
					DebugLog("Schema already present as latest: " + lastSchema);
				}
			}

			// -- Build property lookup and schema field mapping for per-version serialization --
			var allProperties = GetAllInstancePropertiesWithAncestors(classData.Data, exclude).Where(p => TryGetLinkNav(p, classData.LinkSymbols) is null).ToArray();
			var propertyLookup = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
			foreach (var p in allProperties)
			{
				propertyLookup[p.Name] = p;
			}
			var currentPropertyNames = new HashSet<string>(propertyLookup.Keys, StringComparer.Ordinal);
			var schemaFieldMapping = BuildSchemaFieldMapping(
				schemas.Select(s => (s.Item1, s.Item2)).ToArray()
				, currentPropertyNames);

			// -- Generate source --
			var body = new StringBuilder();
			body.AppendLine("#nullable enable");
			HashSet<string> usingsSet = new HashSet<string>();
			List<string> usingsList = new List<string>();
			void Add(string u)
			{
				if (usingsSet.Add(u))
				{
					usingsList.Add(u);
				}
			}
			Add("using System;");
			Add("using Synqra.BinarySerializer;");
			foreach (var usingStatement in clazz.SyntaxTree.GetCompilationUnitRoot().Usings)
			{
				Add(usingStatement.ToString());
			}
			foreach (var u in usingsList)
			{
				body.AppendLine(u);
			}

			body.AppendLine();

			BaseNamespaceDeclarationSyntax? calcClassNamespace = clazz.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
			calcClassNamespace ??= clazz.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();

			body.AppendLine($"");
			body.AppendLine($"namespace {calcClassNamespace?.Name};");
			body.AppendLine();
			body.AppendLine();

			body.AppendLine($"{clazz.Modifiers} class {clazz.Identifier}");
			body.AppendLine("{");

			// -- ISbxSerializer bridge methods + GetCore/SetCore --

#if DEBUG
			var isUnitTest = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "nunit.engine");
			if (isUnitTest)
			{
				schemas = [(1.2f, "1")];
			}
#endif

			if (isRootType)
			{
				body.AppendLine($$"""
	void IBindableModel.Get(ISbxSerializer serializer, float version, in Span<byte> buffer, ref int pos)
	{
		GetCore(serializer, version, in buffer, ref pos);
	}

	void IBindableModel.Set(ISbxSerializer serializer, float version, in ReadOnlySpan<byte> buffer, ref int pos)
	{
		SetCore(serializer, version, in buffer, ref pos);
	}

	protected{{virtualKeyword}} void GetCore(ISbxSerializer serializer, float version, in Span<byte> buffer, ref int pos)
	{
""");
				EmitGetCore(body, clazz, schemas, schemaFieldMapping, propertyLookup, virtualKeyword, classData.Data);
				body.AppendLine("""
	}

""");
				body.AppendLine($$"""
	protected{{virtualKeyword}} void SetCore(ISbxSerializer serializer, float version, in ReadOnlySpan<byte> buffer, ref int pos)
	{
""");
				EmitSetCore(body, clazz, schemas, schemaFieldMapping, propertyLookup, classData.Data);
				body.AppendLine("""
	}
""");
			}
			else
			{
				body.AppendLine("""
	protected override void GetCore(ISbxSerializer serializer, float version, in Span<byte> buffer, ref int pos)
	{
""");
				EmitGetCore(body, clazz, schemas, schemaFieldMapping, propertyLookup, virtualKeyword, classData.Data);
				body.AppendLine("""
	}

""");
				body.AppendLine("""
	protected override void SetCore(ISbxSerializer serializer, float version, in ReadOnlySpan<byte> buffer, ref int pos)
	{
""");
				EmitSetCore(body, clazz, schemas, schemaFieldMapping, propertyLookup, classData.Data);
				body.AppendLine("""
	}
""");
			}

			body.AppendLine("}");

			var fileName = $"{Path.GetFileNameWithoutExtension(clazz.SyntaxTree.FilePath)}_{clazz.Identifier}.Sbx.Generated.cs";
			context.AddSource(fileName, SourceText.From(body.ToString(), Encoding.UTF8));
			DebugLog($"[+] SBX Added source to context {fileName}");
		}
		catch (Exception ex)
		{
			errorBody.AppendLine("#error CodeGenerationException");
			errorBody.AppendLine("// ********** ERROR DURING SBX CODE GENERATION **********");
			errorBody.AppendLine("// " + ex);
			var fileName = $"{Path.GetFileNameWithoutExtension(filePath)}.SbxErrors.Generated.cs";
			context.AddSource(fileName, SourceText.From(errorBody.ToString(), Encoding.UTF8));
			try
			{
				EmergencyLog.Default.LogError(ex, $"Execute {ex}");
			}
			catch { }
		}
	}

	static void EmitGetCore(
		StringBuilder body
		, ClassDeclarationSyntax clazz
		, (double, string)[] schemas
		, Dictionary<string, string> schemaFieldMapping
		, Dictionary<string, IPropertySymbol> propertyLookup
		, string virtualKeyword
		, INamedTypeSymbol containingType)
	{
		bool doesSupportField = false;
		body.AppendLine($"\t\tEmergencyLog.Default.Debug($\"SBX {clazz.Identifier} IBindableModel.Get\");");

		string? els = null;
		bool any = false;
		foreach (var item in schemas)
		{
			any = true;
			var x = FormattableString.Invariant($"\t\t{els}if (version == {item.Item1}f)");
			body.AppendLine(x);
			body.AppendLine($"\t\t{{");
			body.AppendLine($"\t\t\tEmergencyLog.Default.Debug($\"SBX {clazz.Identifier} IBindableModel.Get - if schema {item.Item1}\");");
			body.AppendLine($"\t\t\t// Positional Fields:");
			var getCoreFields = ResolveSchemaFieldsForVersion(item.Item2, schemaFieldMapping, propertyLookup);
			foreach (var (schemaName, pro) in getCoreFields)
			{
				if (pro is null)
				{
					body.AppendLine($"\t\t\t// WARNING: field '{schemaName}' from schema {item.Item1} could not be mapped to a current property");
					continue;
				}
				body.AppendLine($"\t\t\tEmergencyLog.Default.Debug($\"SBX {clazz.Identifier} IBindableModel.Get - {item.Item1} {pro.Name}\");");
				var access = (!doesSupportField && SymbolEqualityComparer.Default.Equals(pro.ContainingType, containingType))
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
			body.AppendLine($"\t\t\tEmergencyLog.Default.Error($\"SBX {clazz.Identifier} IBindableModel.Get - unknown version {{version}}\");");
			body.AppendLine($"\t\t\tthrow new Exception($\"Unknown schema version {{version}} of {clazz.Identifier}\");");
			body.AppendLine($"\t\t}}");
		}
	}

	static void EmitSetCore(
		StringBuilder body
		, ClassDeclarationSyntax clazz
		, (double, string)[] schemas
		, Dictionary<string, string> schemaFieldMapping
		, Dictionary<string, IPropertySymbol> propertyLookup
		, INamedTypeSymbol containingType)
	{
		bool doesSupportField = false;
		string? els = null;
		bool any = false;
		foreach (var item in schemas)
		{
			any = true;
			var x = FormattableString.Invariant($"\t\t{els}if (version == {item.Item1}f)");
			body.AppendLine(x);
			body.AppendLine($"\t\t{{");
			body.AppendLine($"\t\t\t// Positional Fields:");
			var setCoreFields = ResolveSchemaFieldsForVersion(item.Item2, schemaFieldMapping, propertyLookup);
			foreach (var (schemaName, pro) in setCoreFields)
			{
				if (pro is null)
				{
					body.AppendLine($"\t\t\t// WARNING: field '{schemaName}' from schema {item.Item1} could not be mapped to a current property");
					continue;
				}
				var target = (!doesSupportField && SymbolEqualityComparer.Default.Equals(pro.ContainingType, containingType))
					? GetFieldName(pro)
					: "this." + pro.Name;
				var method = DeserializeMethod(pro.Type, debug: containingType.Name);
				var call = $"serializer.Deserialize{method}(in buffer, ref pos)";
				if (method != null && method.StartsWith("Dict<")
					&& pro.Type is INamedTypeSymbol dictType
					&& dictType.TypeKind == TypeKind.Class
					&& dictType.Name != "Dictionary")
				{
					// Concrete IDictionary-derived target (e.g. ObjectData): DeserializeDict returns a
					// plain Dictionary, so copy-construct the target type rather than casting — a cast
					// would throw because the runtime object isn't an instance of the subclass.
					body.AppendLine($"\t\t\t{target} = new {FQN(pro.Type)}({call});");
				}
				else
				{
					body.AppendLine($"\t\t\t{target} = ({FQN(pro.Type)}){call};");
				}
			}
			body.AppendLine($"\t\t}}");
			els = "else ";
		}
		if (any)
		{
			body.AppendLine($"\t\telse");
		}
		body.AppendLine($"\t\t{{");
		body.AppendLine($"\t\t\tEmergencyLog.Default.Error($\"SBX {clazz.Identifier} IBindableModel.Set - unknown version {{version}}\");");
		body.AppendLine($"\t\t\tthrow new Exception($\"Unknown schema version {{version}} of {clazz.Identifier}\");");
		body.AppendLine($"\t\t}}");
	}

}

