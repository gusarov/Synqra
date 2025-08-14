using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.ComponentModel;
using System.Text;
using Synqra;

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

/*
class GeneratorLogging
{
	public static void SetLogFilePath(string path)
	{
	}
	public static void LogMessage(string message, LoggingLevel? level = null)
	{
	}
}
enum LoggingLevel
{
	Trace,
	Debug,
	Info,
	Warning,
	Error,
	Fatal
}
*/


[Generator(LanguageNames.CSharp)]
public class ModelBindingGenerator : IIncrementalGenerator
{
	public ModelBindingGenerator()
	{
#if DEBUG
		GeneratorLogging.SetLogFilePath($"C:\\Temp\\GenLog.txt");
#endif
	}

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var calculatorClassesProvider = context.SyntaxProvider.CreateSyntaxProvider<TheSource>(
			predicate: static (SyntaxNode node, CancellationToken cancelToken) =>
			{
				//the predicate should be super lightweight to filter out items that are not of interest quickly
				try
				{
					var exp = node is ClassDeclarationSyntax classDeclaration && classDeclaration.Identifier.ToString().EndsWith("Model") && classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword);
					// GeneratorLogging.LogMessage($"[+] Checking {node.GetType().Name}");

					return exp;
				}
				catch (Exception ex)
				{
					GeneratorLogging.LogMessage($"[-] {ex}");
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
			  calculatorClassesProvider
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
	/// </summary>d
	/// <param name="calculatorClass"></param>
	/// <param name="context"></param>
	static void Execute(TheSource data, SourceProductionContext context)
	{
		try
		{
			var calculatorClass = data.clazz;
			var calculatorClassMembers = data.clazz.Members;
			GeneratorLogging.LogMessage($"[+] Found {calculatorClassMembers.Count} members in the class. {data.data.GetType().Name}");
			GeneratorLogging.LogMessage($"{data.pceh} pceh");
			GeneratorLogging.LogMessage($"{data.pcgeh} pcgeh");
			GeneratorLogging.LogMessage($"{data.ibm} ibm");
			GeneratorLogging.LogMessage($"{data.ipc} ipc");
			GeneratorLogging.LogMessage($"{data.ipcg} ipcg");

			/*
			foreach (var item in calculatorClassMembers)
			{
				GeneratorLogging.LogMessage($" {item.GetType().Name} {item} {item.AttributeLists} [{item.FullSpan}] {item.Kind()}");
			}
			*/

			// check if the methods we want to add exist already 
			var setMethod = calculatorClassMembers.FirstOrDefault(member => member is MethodDeclarationSyntax method && method.Identifier.Text == "Set");

			GeneratorLogging.LogMessage("[+] Checked if methods exist in Calculator class");

			// this string builder will hold our source code for the methods we want to add
			var body = new StringBuilder();
			body.AppendLine("using Synqra;");
			body.AppendLine("using System.ComponentModel;");
			foreach (var usingStatement in calculatorClass.SyntaxTree.GetCompilationUnitRoot().Usings)
			{
				body.AppendLine(usingStatement.ToString());
			}
			GeneratorLogging.LogMessage("[+] Added using statements to generated class");

			body.AppendLine();

			// The previous Descendent Node check has been removed as it was only intended to help produce the error seen in logging
			BaseNamespaceDeclarationSyntax? calcClassNamespace = calculatorClass.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
			calcClassNamespace ??= calculatorClass.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();

			if (calcClassNamespace is null)
			{
				GeneratorLogging.LogMessage("[-] Could not find namespace for Calculator class", LoggingLevel.Error);
			}
			GeneratorLogging.LogMessage($"[+] Found namespace for Calculator class {calcClassNamespace?.Name}");
			body.AppendLine($"namespace {calcClassNamespace?.Name};");
			body.AppendLine();
			body.AppendLine();
			body.AppendLine($"{calculatorClass.Modifiers} class {calculatorClass.Identifier} : {FQN(data.ibm)}, {FQN(data.ipc)}, {FQN(data.ipcg)}");
			body.AppendLine("{");
			body.AppendLine();
			body.AppendLine($"\tpublic event {FQN(data.pceh)}? PropertyChanged;");
			body.AppendLine($"\tpublic event {FQN(data.pcgeh)}? PropertyChanging;");
			body.AppendLine();

			//if the methods do not exist, we will add them
			if (setMethod is null)
			{
				//when using a raw string the first " is the far left margin in the file
				//if you want the proper indention on the methods you will want to tab the string content at least once
				body.AppendLine(
$$"""
	global::Synqra.ISynqraStoreContext? IBindableModel.Store
	{
		get => field;
		set
		{
			if (field is not null && field != value)
			{
				throw new global::System.InvalidOperationException("Store can only be set once.");
			}
			field = value;
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
				foreach (var pro in calculatorClass.Members.OfType<PropertyDeclarationSyntax>())
				{
					body.AppendLine(
$$"""
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


			foreach (var pro in calculatorClass.Members.OfType<PropertyDeclarationSyntax>())
			{
				body.AppendLine(
	//private {{pro.Type}} __{{pro.Identifier}};
$$"""
	partial void On{{pro.Identifier}}Changing({{pro.Type}} value);
	partial void On{{pro.Identifier}}Changing({{pro.Type}} oldValue, {{pro.Type}} value);
	partial void On{{pro.Identifier}}Changed({{pro.Type}} value);
	partial void On{{pro.Identifier}}Changed({{pro.Type}} oldValue, {{pro.Type}} value);

	public partial {{pro.Type}} {{pro.Identifier}}
	{
		get => field;
		set
		{
			var bm = (IBindableModel)this;
			var oldValue = field;
			if (_assigning || bm.Store is null)
			{
				if (!global::System.Collections.Generic.EqualityComparer<{{pro.Type}}>.Default.Equals(oldValue, value))
				{
					On{{pro.Identifier}}Changing(value);
					On{{pro.Identifier}}Changing(oldValue, value);
					PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(nameof({{pro.Identifier}})));
					field = value;
					On{{pro.Identifier}}Changed(value);
					On{{pro.Identifier}}Changed(oldValue, value);
					PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof({{pro.Identifier}})));
				}
			}
			else
			{
				bm.Store.SubmitCommandAsync(new ChangeObjectPropertyCommand
				{
					Target = this,
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

			GeneratorLogging.LogMessage("[+] Added methods to generated class");

			//to write our source file we can use the context object that was passed in
			//this will automatically use the path we provided in the target projects csproj file
			context.AddSource($"{Path.GetFileNameWithoutExtension(calculatorClass.SyntaxTree.FilePath)}_{calculatorClass.Identifier}.Generated.cs", SourceText.From(body.ToString(), Encoding.UTF8));
			GeneratorLogging.LogMessage("[+] Added source to context");
		}
		catch (Exception e)
		{
			GeneratorLogging.LogMessage($"[-] Exception occurred in generator: {e}", LoggingLevel.Error);
		}
	}
}