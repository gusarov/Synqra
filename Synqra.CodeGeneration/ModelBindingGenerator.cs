using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Synqra;
using Microsoft.CodeAnalysis.Text;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

// using TheSource = (Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax clazz, Microsoft.CodeAnalysis.SemanticModel sem, string? data);
using TheSource = (
	  Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax clazz
	, Microsoft.CodeAnalysis.INamedTypeSymbol data
	, Microsoft.CodeAnalysis.INamedTypeSymbol ibm
	, Microsoft.CodeAnalysis.INamedTypeSymbol ipc
	, Microsoft.CodeAnalysis.INamedTypeSymbol ipcg
	, Microsoft.CodeAnalysis.INamedTypeSymbol pceh
	, Microsoft.CodeAnalysis.INamedTypeSymbol pcgeh
	);
using System.Transactions;
using System.Collections.Specialized;

namespace Synqra.CodeGeneration;

using TheCombinedSource = (
	  TheSource src
	, (string tfm
	, string SynqraBuildBox
	  ) buildProps
	);

[Generator(LanguageNames.CSharp)]
public class ModelBindingGenerator : IIncrementalGenerator
{
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

			var buildProps = context.AnalyzerConfigOptionsProvider.Select((provider, _) =>
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

			var classesProvider = context.SyntaxProvider.CreateSyntaxProvider<TheSource>(
				predicate: static (SyntaxNode node, CancellationToken cancelToken) =>
				{
					//the predicate should be super lightweight to filter out items that are not of interest quickly
					try
					{
						var exp = node is ClassDeclarationSyntax classDeclaration && classDeclaration.Identifier.ToString().EndsWith("Model") && classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword);
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

						var comp = ctx.SemanticModel.Compilation;
						var ibm = comp.GetTypeByMetadataName("Synqra.IBindableModel") ?? throw new Exception("Can't resolve Synqra.IBindableModel. Please, add reference to Syncra.Model");
						var ipc = comp.GetTypeByMetadataName("System.ComponentModel.INotifyPropertyChanged") ?? throw new Exception("System.ComponentModel.INotifyPropertyChanged");
						var ipcg = comp.GetTypeByMetadataName("System.ComponentModel.INotifyPropertyChanging") ?? throw new Exception("System.ComponentModel.INotifyPropertyChanging");
						var pceh = comp.GetTypeByMetadataName("System.ComponentModel.PropertyChangedEventHandler") ?? throw new Exception("System.ComponentModel.PropertyChangedEventHandler");
						var pcgeh = comp.GetTypeByMetadataName("System.ComponentModel.PropertyChangingEventHandler") ?? throw new Exception("System.ComponentModel.PropertyChangingEventHandler");

						var zlsSym = ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, cancelToken) ?? throw new Exception("zlsSym");
						cancelToken.ThrowIfCancellationRequested();


						return (classDeclaration, zlsSym, ibm, ipc, ipcg, pceh, pcgeh);
					}
					catch (Exception ex)
					{
						EmergencyLog.Default.Error($"transform", ex);
						throw;
					}
				});

			context.RegisterSourceOutput(
				  classesProvider.Combine(buildProps)
				, static (SourceProductionContext sourceProductionContext, TheCombinedSource data) => Execute(data, sourceProductionContext)
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

	static IEnumerable<(double, string)> GetAllSchemas(ClassDeclarationSyntax clazz)
	{
		foreach (var attr in clazz.AttributeLists)
		{
			// bool useAttribute = false;
			int i = 0;
			EmergencyLog.Default.Debug("> AttrNode: " + attr.ToFullString());
			foreach (var item in attr.ChildNodes())
			{
				EmergencyLog.Default.Debug(">> ChildNode: " + item.ToFullString());
				foreach (var item2 in item.ChildNodes())
				{
					EmergencyLog.Default.Debug(">>> ChildNode: " + item2.ToFullString());
					if (item2.ToFullString() == "Schema")
					{
						EmergencyLog.Default.Debug("!!! " + i);
					}
					foreach (var item3 in item2.ChildNodes())
					{
						EmergencyLog.Default.Debug(">>>> ChildNode: " + item3.ToFullString());
					}
					if (i++ == 0)
					{
						if (item2.ToFullString() == "Schema")
						{
							EmergencyLog.Default.Debug("! SELECTED NEXT AFTER: " + item2.ToFullString());
							i = -1;
							continue;
						}
					}
					else if (i == 0)
					{
						int sc = 0;
						double ver = 0;
						EmergencyLog.Default.Debug("! ChildNode: " + item2.ToFullString());
						foreach (var item3 in item2.ChildNodes())
						{
							EmergencyLog.Default.Debug(">>>> ChildNode: " + item3.ToFullString());
							if (sc++ == 0)
							{
								if (double.TryParse(item3.ToFullString(), out ver))
								{
									EmergencyLog.Default.Debug("!!! Schema Version: " + ver);
									continue;
								}
							}
							else
							{
								var s = item3.ToFullString().Trim('"');
								EmergencyLog.Default.Debug("!!! Schema String: " + s);
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

	/// <summary>
	/// This method is where the real work of the generator is done
	/// This ensures optimal performance by only executing the generator when needed
	/// The method can be named whatever you want but Execute seems to be the standard 
	/// </summary>
	static void Execute(TheCombinedSource combinedData, SourceProductionContext context)
	{
		TheSource data = combinedData.src;
		string tfm = combinedData.buildProps.tfm;
		string SynqraBuildBox = combinedData.buildProps.SynqraBuildBox;
		if (tfm == null || data.clazz is null)
		{
			return;
		}

		var netVer = tfm.StartsWith("net") ? Version.TryParse(tfm[3..], out var version) ? version : null : null;
		var doesSupportField = false; // netVer != null && netVer.Major >= 6;

		try
		{
			var clazz = data.clazz;

			var sourceContent = CodeGenUtils.Default.ReadFile(clazz.SyntaxTree.FilePath);
			EmergencyLog.Default.Debug($"EXECUTE SyntaxTree.FilePath={clazz.SyntaxTree.FilePath} ioSourceContent={sourceContent} node.text={clazz.ToFullString()}");
			var line = clazz.SyntaxTree.GetLineSpan(clazz.GetLocation().SourceSpan).StartLinePosition.Line;
			if (clazz.AttributeLists.Count == 0)
			{
				EmergencyLog.Default.Debug($"GetLineSpan() {line} {clazz.Span.Start}");
			}
			else
			{
				var a = clazz.AttributeLists.First().Span.Start;
				var b = clazz.AttributeLists.First().Span.End;
				var c = clazz.AttributeLists.Last().Span.Start;
				var d = clazz.AttributeLists.Last().Span.End;
				EmergencyLog.Default.Debug($"GetLineSpan() {line}/{a}/{b}/{c}/{d}");

				string lastSchema = "";
				foreach (var item in GetAllSchemas(clazz))
				{
					EmergencyLog.Default.Debug("Schema: " + item);
					lastSchema = item.Item2;
				}

				var sb = new StringBuilder(sourceContent);
				var ver = 2025.99;
				var schema = "1 Subject str? Number zig";
				if (lastSchema != schema)
				{
					EmergencyLog.Default.Debug("*********** Schema drift! path= " + clazz.SyntaxTree.FilePath);
					sb.Insert(d, $"\r\n[Schema({ver}, \"{schema}\")]");
					CodeGenUtils.Default.WriteFile(SynqraBuildBox, clazz.SyntaxTree.FilePath, sb.ToString());
				}
				else
				{
					EmergencyLog.Default.Debug("*********** Schema already present as latest: " + lastSchema);
				}
				EmergencyLog.Default.Debug(sb.ToString());

			}

			EmergencyLog.Default.Debug("!! SynqraBuildBox = " + SynqraBuildBox);

			var classMembers = data.clazz.Members;
			EmergencyLog.Default.Debug($"TFM {tfm} Analyze {clazz.Identifier}...");
			EmergencyLog.Default.Debug($"{data.pceh} pceh {data.pcgeh} pcgeh {data.ibm} ibm {data.ipc} ipc {data.ipcg} ipcg");

			foreach (var item in classMembers)
			{
				EmergencyLog.Default.Debug($" {item.GetType().Name} {item} {item.AttributeLists} [{item.FullSpan}] {item.Kind()}");
			}

			// check if the methods we want to add exist already 
			var setMethod = classMembers.FirstOrDefault(member => member is MethodDeclarationSyntax method && method.Identifier.Text == "Set");

			// SynqraEmergencyLog.Default.Debug("[+] Checked if methods exist in class: " + setMethod);

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
			// SynqraEmergencyLog.Default.Debug("[+] Added using statements to generated class");

			body.AppendLine();

			// The previous Descendent Node check has been removed as it was only intended to help produce the error seen in logging
			BaseNamespaceDeclarationSyntax? calcClassNamespace = clazz.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
			calcClassNamespace ??= clazz.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();

			if (calcClassNamespace is null)
			{
				EmergencyLog.Default.Error($"Could not find namespace for {clazz.Identifier}", null);
			}
			EmergencyLog.Default.Debug($"Found calcClassNamespace={calcClassNamespace?.Name}");
			body.AppendLine($"");
			body.AppendLine($"namespace {calcClassNamespace?.Name};");
			body.AppendLine();
			// body.AppendLine($"// Synqra Model Target: {Synqra.SynqraTargetInfo.TargetFramework}");
			body.AppendLine();
			body.AppendLine($"{clazz.Modifiers} class {clazz.Identifier} : {FQN(data.ibm)}, {FQN(data.ipc)}, {FQN(data.ipcg)}");
			body.AppendLine("{");
			body.AppendLine();
			body.AppendLine($"\tpublic event PropertyChangedEventHandler? PropertyChanged;");
			body.AppendLine($"\tpublic event PropertyChangingEventHandler? PropertyChanging;");
			body.AppendLine();

			//if the methods do not exist, we will add them
			if (setMethod is null)
			{
				//when using a raw string the first " is the far left margin in the file
				//if you want the proper indention on the methods you will want to tab the string content at least once
				body.AppendLine(
$$"""
	global::Synqra.ISynqraStoreContext? __store;

	global::Synqra.ISynqraStoreContext? IBindableModel.Store
	{
		get => __store;
		set
		{
			if (__store is not null && __store != value)
			{
				throw new global::System.InvalidOperationException("Store can only be set once.");
			}
			__store = value;
		}
	}

	[ThreadStatic]
	static bool _assigning; // when true, the source of the change is model binding due to new events reaching the context, so it is external change. This way, when setter see false here - it means the source is a client code, direct property change by consumer.

	void IBindableModel.Set(string name, object? value)
	{
		var previous = _assigning;
		_assigning = true;
		try
		{
			switch (name)
			{
""");
				foreach (var pro in clazz.Members.OfType<PropertyDeclarationSyntax>())
				{
						body.AppendLine($$"""
				case "{{pro.Identifier}}":
					{{pro.Identifier}} = ({{pro.Type}})value!;
					break;
""");
				}
				body.AppendLine(
"""
			}
		}
		finally
		{
			_assigning = previous;
		}
	}
""");
			}


			foreach (var pro in clazz.Members.OfType<PropertyDeclarationSyntax>())
			{
				if (!doesSupportField)
				{
					body.AppendLine(
$$"""
	private {{pro.Type}} __{{pro.Identifier}};
""");
				}
				body.AppendLine(
$$"""

	// tfm={{tfm}}	// doesSupportField={{doesSupportField}}

	partial void On{{pro.Identifier}}Changing({{pro.Type}} value);
	partial void On{{pro.Identifier}}Changing({{pro.Type}} oldValue, {{pro.Type}} value);
	partial void On{{pro.Identifier}}Changed({{pro.Type}} value);
	partial void On{{pro.Identifier}}Changed({{pro.Type}} oldValue, {{pro.Type}} value);

	public partial {{pro.Type}} {{pro.Identifier}}
	{
		get => {{(doesSupportField ? "field" : "__" + pro.Identifier)}};
		set
		{
			var oldValue = {{(doesSupportField ? "field" : "__" + pro.Identifier)}};
			if (_assigning || __store is null)
			{
				var pci = PropertyChanging;
				var pce = PropertyChanged;
				if (pci is null && pce is null)
				{
					On{{pro.Identifier}}Changing(value);
					On{{pro.Identifier}}Changing(oldValue, value);
					{{(doesSupportField ? "field" : "__" + pro.Identifier)}} = value;
					On{{pro.Identifier}}Changed(value);
					On{{pro.Identifier}}Changed(oldValue, value);
				}
				else if (!Equals(oldValue, value))
				{
					On{{pro.Identifier}}Changing(value);
					On{{pro.Identifier}}Changing(oldValue, value);
					pci?.Invoke(this, new PropertyChangingEventArgs(nameof({{pro.Identifier}})));
					{{(doesSupportField ? "field" : "__" + pro.Identifier)}} = value;
					On{{pro.Identifier}}Changed(value);
					On{{pro.Identifier}}Changed(oldValue, value);
					pce?.Invoke(this, new PropertyChangedEventArgs(nameof({{pro.Identifier}})));
				}
			}
			else
			{
				On{{pro.Identifier}}Changing(value);
				On{{pro.Identifier}}Changing(oldValue, value);
				__store.SubmitCommandAsync(new ChangeObjectPropertyCommand
				{
					CommandId = GuidExtensions.CreateVersion7(),
					ContainerId = default,
					CollectionId = default,

					Target = this,
					TargetId = __store.GetId(this, null, GetMode.RequiredId),
					TargetTypeId = default,
					// TargetTypeId = __store.GetId(this, null, GetMode.RequiredId),

					PropertyName = nameof({{pro.Identifier}}),
					OldValue = oldValue,
					NewValue = value
				}).GetAwaiter().GetResult();
			}
		}
	}
""");
			}


			body.AppendLine("}");
			//while a bit crude it is a simple way to add the methods to the class

			EmergencyLog.Default.Debug("[+] Added methods to generated class");

			//to write our source file we can use the context object that was passed in
			//this will automatically use the path we provided in the target projects csproj file
			var fileName = $"{Path.GetFileNameWithoutExtension(clazz.SyntaxTree.FilePath)}_{clazz.Identifier}.Generated.cs";
			context.AddSource(fileName, SourceText.From(body.ToString(), Encoding.UTF8));
			EmergencyLog.Default.Debug($"[+] Added source to context {fileName}");
		}
		catch (Exception ex)
		{
			EmergencyLog.Default.Error($"Execute", ex);
		}
	}
}