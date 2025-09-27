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
	, Microsoft.CodeAnalysis.INamedTypeSymbol? data
	, Microsoft.CodeAnalysis.INamedTypeSymbol? ibm
	, Microsoft.CodeAnalysis.INamedTypeSymbol? ipc
	, Microsoft.CodeAnalysis.INamedTypeSymbol? ipcg
	, Microsoft.CodeAnalysis.INamedTypeSymbol? pceh
	, Microsoft.CodeAnalysis.INamedTypeSymbol? pcgeh
	);

namespace Synqra.CodeGeneration;
 
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
				  EmergencyLog.Default.Message($"[-] {ex}");
				  throw;
			  }
		  },
		  transform: static (GeneratorSyntaxContext ctx, CancellationToken cancelToken) =>
		  {
			  cancelToken.ThrowIfCancellationRequested();

			  //the transform is called only when the predicate returns true, so it can do a bit more heavyweight work but should mainly be about getting the data we want to work with later
			  var classDeclaration = (ClassDeclarationSyntax)ctx.Node;

			  var comp = ctx.SemanticModel.Compilation;
			  var ibm = comp.GetTypeByMetadataName("Synqra.IBindableModel");
			  var ipc = comp.GetTypeByMetadataName("System.ComponentModel.INotifyPropertyChanged");
			  var ipcg = comp.GetTypeByMetadataName("System.ComponentModel.INotifyPropertyChanging");
			  var pceh = comp.GetTypeByMetadataName("System.ComponentModel.PropertyChangedEventHandler");
			  var pcgeh = comp.GetTypeByMetadataName("System.ComponentModel.PropertyChangingEventHandler");

			  var zlsSym = ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, cancelToken);
			  cancelToken.ThrowIfCancellationRequested();

			  return (classDeclaration, zlsSym, ibm, ipc, ipcg, pceh, pcgeh);

		  });

		context.RegisterSourceOutput(
			  classesProvider
			, static (SourceProductionContext sourceProductionContext, TheSource data) => Execute(data, sourceProductionContext)
			);
	}

	static string FQN(ITypeSymbol? t) =>
	(t ?? throw new InvalidOperationException("Type not found"))
	.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

	/// <summary>
	/// This method is where the real work of the generator is done
	/// This ensures optimal performance by only executing the generator when needed
	/// The method can be named whatever you want but Execute seems to be the standard 
	/// </summary>
	static void Execute(TheSource data, SourceProductionContext context)
	{
		try
		{
			var clazz = data.clazz;
			var classMembers = data.clazz.Members;
			EmergencyLog.Default.Message($"[+] Analyze {clazz.Identifier}...");
			EmergencyLog.Default.Message($"{data.pceh} pceh");
			EmergencyLog.Default.Message($"{data.pcgeh} pcgeh");
			EmergencyLog.Default.Message($"{data.ibm} ibm");
			EmergencyLog.Default.Message($"{data.ipc} ipc");
			EmergencyLog.Default.Message($"{data.ipcg} ipcg");

			foreach (var item in classMembers)
			{
				EmergencyLog.Default.Debug($" {item.GetType().Name} {item} {item.AttributeLists} [{item.FullSpan}] {item.Kind()}");
			}

			// check if the methods we want to add exist already 
			var setMethod = classMembers.FirstOrDefault(member => member is MethodDeclarationSyntax method && method.Identifier.Text == "Set");

			// SynqraEmergencyLog.Default.Debug("[+] Checked if methods exist in class: " + setMethod);

			// this string builder will hold our source code for the methods we want to add
			var body = new StringBuilder();
			body.AppendLine("using Synqra;");
			body.AppendLine("using System.ComponentModel;");
			foreach (var usingStatement in clazz.SyntaxTree.GetCompilationUnitRoot().Usings)
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
				EmergencyLog.Default.Debug($"[-] Could not find namespace for {clazz.Identifier}");
			}
			EmergencyLog.Default.Debug($"[+] Found calcClassNamespace={calcClassNamespace?.Name}");
			body.AppendLine($"using System.ComponentModel;");
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
					{{pro.Identifier}} = ({{pro.Type}})value;
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
				body.AppendLine(
$$"""
	// private {{pro.Type}} __{{pro.Identifier}};

	partial void On{{pro.Identifier}}Changing({{pro.Type}} value);
	partial void On{{pro.Identifier}}Changing({{pro.Type}} oldValue, {{pro.Type}} value);
	partial void On{{pro.Identifier}}Changed({{pro.Type}} value);
	partial void On{{pro.Identifier}}Changed({{pro.Type}} oldValue, {{pro.Type}} value);

	public partial {{pro.Type}} {{pro.Identifier}}
	{
		get => field;
		set
		{
			var oldValue = field;
			if (_assigning || __store is null)
			{
				var pci = PropertyChanging;
				var pce = PropertyChanged;
				if (pci is null && pce is null)
				{
					On{{pro.Identifier}}Changing(value);
					On{{pro.Identifier}}Changing(oldValue, value);
					field = value;
					On{{pro.Identifier}}Changed(value);
					On{{pro.Identifier}}Changed(oldValue, value);
				}
				else if (!Equals(oldValue, value))
				{
					On{{pro.Identifier}}Changing(value);
					On{{pro.Identifier}}Changing(oldValue, value);
					pci?.Invoke(this, new PropertyChangingEventArgs(nameof({{pro.Identifier}})));
					field = value;
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
			EmergencyLog.Default.Message($"[-] Exception occurred in generator: {ex}");
		}
	}
}