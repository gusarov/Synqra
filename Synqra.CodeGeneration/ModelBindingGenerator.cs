// Copy this to a model you need to trace:
// #define SYNQRA_CODEGEN_TRACE

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Synqra;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using static Synqra.CodeGeneration.CodeGenHelpers;

using ClassesProviderT = (
	  string? errorMessage
	, System.Exception? exception
	// -- OR --
	, Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax Clazz
	, Microsoft.CodeAnalysis.INamedTypeSymbol Data
	, Microsoft.CodeAnalysis.INamedTypeSymbol Ibm
	, Microsoft.CodeAnalysis.INamedTypeSymbol Ipc
	, Microsoft.CodeAnalysis.INamedTypeSymbol Ipcg
	, Microsoft.CodeAnalysis.INamedTypeSymbol Pceh
	, Microsoft.CodeAnalysis.INamedTypeSymbol Pcgeh
	, Synqra.CodeGeneration.LinkFrameworkSymbols LinkSymbols
	);

namespace Synqra.CodeGeneration;

using TheCombinedSource = (
		ClassesProviderT ClassData
	, string Tfm
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

	private static readonly DiagnosticDescriptor LinkNavDiagnostic = new(
		id: "SYNQRA003",
		title: "Invalid link navigation property",
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
		InitializeCore(context);
	}

	private System.Reflection.Assembly? CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
	{
		return null;
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
				return Get("build_property.TargetFramework");
			});

			var missingReferences = context.CompilationProvider.Select((comp, _) =>
			{
				var missing = new List<string>();
				if (comp.GetTypeByMetadataName("Synqra.IBindableModel") is null)
					missing.Add("Synqra.IBindableModel");
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
					try
					{
						var exp = node is ClassDeclarationSyntax classDeclaration
							&& (classDeclaration.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == "SynqraModel")))
							&& classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword
							);
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

						var classDeclaration = (ClassDeclarationSyntax)ctx.Node;
						var symbol = ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, cancelToken) ?? throw new Exception("symbol");

						var comp = ctx.SemanticModel.Compilation;

						var ibm = comp.GetTypeByMetadataName("Synqra.IBindableModel");
						if (ibm is null)
						{
							return (null, null, default!, default!, default!, default!, default!, default!, default!, default!);
						}
						var ipc = comp.GetTypeByMetadataName("System.ComponentModel.INotifyPropertyChanged") ?? throw new Exception("System.ComponentModel.INotifyPropertyChanged");
						var ipcg = comp.GetTypeByMetadataName("System.ComponentModel.INotifyPropertyChanging") ?? throw new Exception("System.ComponentModel.INotifyPropertyChanging");
						var pceh = comp.GetTypeByMetadataName("System.ComponentModel.PropertyChangedEventHandler") ?? throw new Exception("System.ComponentModel.PropertyChangedEventHandler");
						var pcgeh = comp.GetTypeByMetadataName("System.ComponentModel.PropertyChangingEventHandler") ?? throw new Exception("System.ComponentModel.PropertyChangingEventHandler");
						var linkSymbols = new LinkFrameworkSymbols(comp);

						cancelToken.ThrowIfCancellationRequested();

						return (null, null, classDeclaration, symbol, ibm, ipc, ipcg, pceh, pcgeh, linkSymbols);
					}
					catch (Exception ex)
					{
						EmergencyLog.Default.Error($"transform", ex);
						return ($"Error processing class: {ex.Message}", ex, default!, default!, default!, default!, default!, default!, default!, default!);
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

	/// <summary>
	/// This method is where the real work of the generator is done.
	/// </summary>
	static void Execute(SourceProductionContext context, TheCombinedSource combinedData)
	{
		var errorBody = new StringBuilder();

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

			var exclude = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)
			{
				classData.Ibm,
			};

			string tfm = combinedData.Tfm;
			if (tfm == null || classData.Clazz is null)
			{
				return;
			}

			var netVer = tfm.StartsWith("net") ? Version.TryParse(tfm[3..], out var version) ? version : null : null;
			var doesSupportField = false;

			var clazz = classData.Clazz;
			filePath = clazz.SyntaxTree.FilePath;

			var classMembers = classData.Clazz.Members;
			DebugLog($"GENERATE FOR {clazz.Identifier} : {classData.Data.BaseType} ({clazz.SyntaxTree.FilePath})...");

			bool isComponent = classData.Data.AllInterfaces.Any(i =>
				i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Synqra.IComponent");

			bool isContainer = classData.Data.AllInterfaces.Any(i =>
				i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Synqra.IComponentContainer");
			bool userDeclaredComponents = classData.Clazz.Members
				.OfType<PropertyDeclarationSyntax>()
				.Any(p => p.Identifier.Text == "Components")
				|| classData.Data.GetMembers("Components")
					.Any(s => s is IPropertySymbol or IFieldSymbol);
			bool emitComponentsCollection = isContainer && !userDeclaredComponents;

			// ECS: a plain top-level model is its own self-owned root component. Infra + facets/links excluded.
			bool isCommand = classData.Data.AllInterfaces.Any(i =>
				i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Synqra.ISynqraCommand");
			bool isEventType = classData.Data.AllInterfaces.Any(i =>
				i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Synqra.IEvent");
			bool isRootTypeForComponent = classData.Data.BaseType is null || classData.Data.BaseType.SpecialType == SpecialType.System_Object;
			bool isRootComponent = isRootTypeForComponent && !isComponent && !isCommand && !isEventType;

			string commandTypeName        = (isComponent || isRootComponent) ? "ChangeComponentPropertyCommand" : "ChangeObjectPropertyCommand";
			string preAttachGuardExtra    = isComponent ? " || __containerId == default" : "";
			string targetObjectExpr       = isComponent ? "null" : "this";
			string targetIdExpr           = isComponent ? "__containerId" : "__store.GetId(this)";
			string targetTypeIdExpr       = isComponent ? "__containerTypeId" : "__store.TypeMetadataProvider.GetTypeMetadata(GetType()).TypeId";
			string collectionIdExpr       = isComponent ? "__containerCollectionId" : "__collectionId ?? Guid.Empty";
			string componentExtraFields   = isComponent
				? @",
					ComponentTypeId = __store.TypeMetadataProvider.GetTypeMetadata(GetType()).TypeId,
					ComponentId = (this is global::Synqra.IIdentifiable<global::System.Guid> __idable ? __idable.Id : global::System.Guid.Empty)"
				: isRootComponent
				? @",
					ComponentTypeId = __store.TypeMetadataProvider.GetTypeMetadata(GetType()).TypeId,
					ComponentId = __store.GetId(this)"
				: "";
			string submissionOptionsArg = isComponent
				? ", new global::Synqra.CommandSubmissionOptions { ExpectedLastEventId = __store.GetLastEventId(__containerId) }"
				: ", new global::Synqra.CommandSubmissionOptions { ExpectedLastEventId = __store.GetLastEventId(__store.GetId(this)) }";

			bool isRootType = classData.Data.BaseType is null || classData.Data.BaseType.SpecialType == SpecialType.System_Object;
			bool isSealed = classData.Data.IsSealed;
			var virtualKeyword = isSealed ? "" : " virtual";

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
			Add("using System;");
			Add("using System.ComponentModel;");
			Add("using System.Diagnostics;");
			Add("using Synqra;");
			Add("using Synqra.BinarySerializer;");
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
			body.AppendLine($"");
			body.AppendLine($"namespace {calcClassNamespace?.Name};");
			body.AppendLine();
			body.AppendLine();
			var componentIface = isComponent ? ", global::Synqra.IBindableComponent" : "";
			var ifaces = ($" : {FQN(classData.Ibm)}, {FQN(classData.Ipc)}, {FQN(classData.Ipcg)}{componentIface}");
			bool hasLinkNav = clazz.Members.OfType<PropertyDeclarationSyntax>()
				.Any(p => classData.Data.GetMembers(p.Identifier.Text).OfType<IPropertySymbol>().FirstOrDefault() is { } ps && TryGetLinkNav(ps, classData.LinkSymbols) is not null);
			var linkAwareIface = hasLinkNav ? (isRootType ? ", global::Synqra.ILinkAware" : " : global::Synqra.ILinkAware") : "";
			body.AppendLine($"{clazz.Modifiers} class {clazz.Identifier}{(isRootType ? ifaces : null)}{linkAwareIface}");
			body.AppendLine("{");

			body.AppendLine($"\tstatic {clazz.Identifier}()");
			body.AppendLine($"\t{{");
			body.AppendLine($"\t\tSynqraJsonTypeInfoResolver.RegisterGeneratedModel<{clazz.Identifier}>();");
			body.AppendLine($"\t}}");
			body.AppendLine($"");

			if (isRootType)
			{
				body.AppendLine($$"""
	[ThreadStatic]
	protected static bool _assigning; // when true, the source of the change is model binding due to new events reaching the context, so it is external change. This way, when setter see false here - it means the source is a client code, direct property change by consumer.

	public event PropertyChangedEventHandler? PropertyChanged;
	public event PropertyChangingEventHandler? PropertyChanging;

	protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	protected void OnPropertyChanging(string propertyName) => PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(propertyName));
	partial void OnAttached();

	protected global::Synqra.IObjectStore? __store;

	global::Synqra.IObjectStore? IBindableModel.Store
	{
		get => __store;
	}

	protected Guid? __collectionId;
	Guid? IBindableModel.CollectionId => __collectionId;

	void IBindableModel.Attach(global::Synqra.IObjectStore store, Guid collectionId)
	{
		if (__store is not null && __store != store)
		{
			throw new global::System.InvalidOperationException("Store can only be set once.");
		}
		if (__collectionId != default && __collectionId != collectionId)
		{
			throw new global::System.InvalidOperationException("CollectionId can only be set once.");
		}
		__store = store;
		__collectionId = collectionId;
		{{(emitComponentsCollection ? "EnsureComponentsWrapper().Attach(store, collectionId);" : "")}}
		OnAttached();
	}
""");
				if (emitComponentsCollection)
				{
					body.AppendLine($$"""

	// IComponentContainer: generator-emitted Components property. Backed by
	// StoreBoundComponentsCollection so that user-driven `.Components.Add(c)` /
	// `.Components.Remove(c)` produce AddComponentCommand / DeleteComponentCommand
	// when the container is store-attached; the projection's event-apply path
	// reaches the inner data via TryAdd / BypassRemove and stays command-free.
	private global::Synqra.StoreBoundComponentsCollection? __components;

	public global::Synqra.IComponentsCollection Components => EnsureComponentsWrapper();

	global::Synqra.StoreBoundComponentsCollection EnsureComponentsWrapper()
	{
		var existing = __components;
		if (existing is not null) return existing;
		var wrapper = new global::Synqra.StoreBoundComponentsCollection(this);
		var prior = global::System.Threading.Interlocked.CompareExchange(
			ref __components, wrapper, null);
		return prior ?? wrapper;
	}
""");
				}
				if (isComponent)
				{
					body.AppendLine($$"""

	// IBindableComponent: container linkage emitted only for IComponent classes.
	protected global::System.Guid __containerId;
	protected global::System.Guid __containerTypeId;
	protected global::System.Guid __containerCollectionId;

	void global::Synqra.IBindableComponent.AttachToContainer(
		global::Synqra.IObjectStore store,
		global::System.Guid containerId,
		global::System.Guid containerTypeId,
		global::System.Guid containerCollectionId)
	{
		if (__store is not null && __store != store)
		{
			throw new global::System.InvalidOperationException("Store can only be set once on a component.");
		}
		if (__containerId != default && __containerId != containerId)
		{
			throw new global::System.InvalidOperationException("Component is already attached to a different container.");
		}
		__store = store;
		__containerId = containerId;
		__containerTypeId = containerTypeId;
		__containerCollectionId = containerCollectionId;
	}

	// First-class component identity, independent of [Component(IsUnique)] cardinality.
	// Auto-assigned at construction; the projection re-stamps it from the event's ComponentId
	// on materialize/replay so a rehydrated instance keeps its persisted id.
	protected global::System.Guid __id = global::Synqra.GuidExtensions.CreateVersion7();
	global::System.Guid global::Synqra.IIdentifiable<global::System.Guid>.Id => __id;
	void global::Synqra.IBindableComponent.SetComponentId(global::System.Guid id) => __id = id;
""");
				}
				body.AppendLine($$"""

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

	protected{{virtualKeyword}} void SetCore(string name, object? value)
	{
		switch (name)
		{
""");
				foreach (var pro in GetAllInstancePropertiesWithAncestors(classData.Data, exclude).Where(p => p.SetMethod is not null && TryGetLinkNav(p, classData.LinkSymbols) is null))
				{
					if (pro.Type.ToString() == "int")
					{
						body.AppendLine($$"""
			case "{{pro.Name}}":
			{
				if (value is long l)
				{
					this.{{pro.Name}} = ({{FQN(pro.Type)}})l;
				}
				else
				{
					this.{{pro.Name}} = ({{FQN(pro.Type)}})value!;
				}
				break;
			}
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
	$$"""
		}
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
				foreach (var pro in GetAllInstancePropertiesOfType(classData.Data))
				{
					// Opt-in nav setters (e.g. Parent { get; set; }) have both accessors, so the
					// get/set filter above doesn't exclude them — but they're a live query backed by
					// SetSingle, not a stored field, so they must never be hydrated from a dictionary.
					if (TryGetLinkNav(pro, classData.LinkSymbols) is not null)
					{
						continue;
					}
					if (pro.Type.ToString() == "int")
					{
						body.AppendLine($$"""
			case "{{pro.Name}}":
			{
				if (value is long l)
				{
					this.{{pro.Name}} = ({{FQN(pro.Type)}})l;
				}
				else
				{
					this.{{pro.Name}} = ({{FQN(pro.Type)}})value!;
				}
				break;
			}
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
		default:
			base.SetCore(name, value);
			break;
	}
}
""");
			}

			#region Fields and Properties

			body.AppendLine($$"""

""");
			var linkAwareEntries = new List<(string LinkTypeFqn, string End, string PropertyName)>();
			foreach (var pro in clazz.Members.OfType<PropertyDeclarationSyntax>())
			{
				if (!pro.Modifiers.Any(x => x.ToString() == "partial"))
				{
					continue;
				}

				// Link navigation: [To]/[From]/[Related] partial properties are not stored columns —
				// the generator emits a live, store-backed view instead of a backing field + setter.
				var propSymbol = classData.Data.GetMembers(pro.Identifier.Text).OfType<IPropertySymbol>().FirstOrDefault();
				var nav = propSymbol is not null ? TryGetLinkNav(propSymbol, classData.LinkSymbols) : null;
				if (nav is not null)
				{
					if (nav.MissingLinkType)
					{
						context.ReportDiagnostic(Diagnostic.Create(LinkNavDiagnostic, pro.Identifier.GetLocation(),
							$"Navigation property '{pro.Identifier}' is node-typed, so it must name its link type, e.g. [To(typeof(MyLink))]."));
						continue;
					}
					if (!nav.IsLinkTyped && nav.LinkTypeResolved && !nav.IsPrimitiveLink)
					{
						context.ReportDiagnostic(Diagnostic.Create(LinkNavDiagnostic, pro.Identifier.GetLocation(),
							$"Navigation property '{pro.Identifier}' is node-typed over link '{nav.LinkTypeFqn}', which carries payload. Expose the link itself (a collection of '{nav.LinkTypeFqn}') so the payload is reachable, or drop the payload to keep it a primitive link."));
						continue;
					}

					linkAwareEntries.Add((nav.LinkTypeFqn, nav.End, pro.Identifier.Text));

					string coll = nav.IsLinkTyped
						? $"new global::Synqra.LinkEndCollection<{nav.LinkTypeFqn}>(this, global::Synqra.LinkEnd.{nav.End})"
						: $"new global::Synqra.NodeLinkCollection<{nav.ElementTypeFqn}, {nav.LinkTypeFqn}>(this, global::Synqra.LinkEnd.{nav.End})";

					if (nav.IsCollection)
					{
						body.AppendLine(
$$"""

	[global::System.Text.Json.Serialization.JsonIgnore]
	public partial {{pro.Type}} {{pro.Identifier}} => {{coll}};

""");
					}
					else if (nav.WantsSetter)
					{
						// Opt-in: the consumer declared { get; set; }, not { get; }. Setting replaces
						// whatever single link already occupies this role (if any); null clears it.
						body.AppendLine(
$$"""

	[global::System.Text.Json.Serialization.JsonIgnore]
	public partial {{pro.Type}} {{pro.Identifier}}
	{
		get => {{coll}}.SingleOrDefault();
		set => {{coll}}.SetSingle(value);
	}

""");
					}
					else
					{
						body.AppendLine(
$$"""

	[global::System.Text.Json.Serialization.JsonIgnore]
	public partial {{pro.Type}} {{pro.Identifier}} => {{coll}}.SingleOrDefault();

""");
					}
					continue;
				}

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
			if (_assigning || __store is null{{preAttachGuardExtra}})
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
				EmergencyLog.Default.Debug($"SBX {GetType().Name} PropertyChanging: {nameof({{pro.Identifier}})} from {oldValue} to {value} " + new StackTrace());
				var task = __store.SubmitCommandAsync(new {{commandTypeName}}
				{
					CommandId = GuidExtensions.CreateVersion7(),
					CollectionId = {{collectionIdExpr}},

					TargetObject = {{targetObjectExpr}},
					TargetId = {{targetIdExpr}},
					TargetTypeId = {{targetTypeIdExpr}},

					PropertyName = nameof({{pro.Identifier}}),
					OldValue = oldValue,
					NewValue = value{{componentExtraFields}}
				}{{submissionOptionsArg}});
				if (!OperatingSystem.IsBrowser())
				{
					task.GetAwaiter().GetResult();
				}
			}
		}
	}

""");
			}

			#endregion

			if (hasLinkNav)
			{
				// One ILinkAware.OnLinkChanged per class, grouping every nav property by the (link
				// type, end) it watches — the store calls this on both endpoints of every link change
				// so live-query nav properties (no backing field, so nothing else would notice) still
				// raise INotifyPropertyChanged. Comparing types via `==` on a `System.Type` parameter
				// (not pattern-matching on the link's runtime type) keeps this exhaustive without a
				// dependency on the link hierarchy.
				body.AppendLine(
$$"""

	void global::Synqra.ILinkAware.OnLinkChanged(global::System.Type linkType, global::Synqra.LinkEnd selfEnd)
	{
""");
				foreach (var group in linkAwareEntries.GroupBy(e => e.LinkTypeFqn))
				{
					body.AppendLine($"\t\tif (linkType == typeof({group.Key}))");
					body.AppendLine("\t\t{");
					foreach (var entry in group)
					{
						var endCheck = entry.End == "Either"
							? "true"
							: $"selfEnd == global::Synqra.LinkEnd.{entry.End}";
						body.AppendLine($"\t\t\tif ({endCheck}) {{ OnPropertyChanged(nameof({entry.PropertyName})); }}");
					}
					body.AppendLine("\t\t}");
				}
				body.AppendLine(
"""
	}

""");
			}

			body.AppendLine("}");

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
		}
	}
}