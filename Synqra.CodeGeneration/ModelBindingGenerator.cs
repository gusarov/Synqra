using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Synqra.Model;
using System.ComponentModel;
using System.Text;

namespace Synqra.CodeGeneration;

[Generator]
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
		var calculatorClassesProvider = context.SyntaxProvider.CreateSyntaxProvider(
		  predicate: (SyntaxNode node, CancellationToken cancelToken) =>
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
				  GeneratorLogging.LogMessage($"[-] {ex}");
				  throw;
			  }
		  },
		  transform: (GeneratorSyntaxContext ctx, CancellationToken cancelToken) =>
		  {
			  //the transform is called only when the predicate returns true, so it can do a bit more heavyweight work but should mainly be about getting the data we want to work with later
			  var classDeclaration = (ClassDeclarationSyntax)ctx.Node;
			  return classDeclaration;
		  });

		context.RegisterSourceOutput(calculatorClassesProvider, (sourceProductionContext, calculatorClass)
			=> Execute(calculatorClass, sourceProductionContext));
	}
	/// <summary>
	/// This method is where the real work of the generator is done
	/// This ensures optimal performance by only executing the generator when needed
	/// The method can be named whatever you want but Execute seems to be the standard 
	/// </summary>
	/// <param name="calculatorClass"></param>
	/// <param name="context"></param>
	public void Execute(ClassDeclarationSyntax calculatorClass, SourceProductionContext context)
	{
		try
		{
			var calculatorClassMembers = calculatorClass.Members;
			GeneratorLogging.LogMessage($"[+] Found {calculatorClassMembers.Count} members in the Calculator class");

			foreach (var item in calculatorClassMembers)
			{
				GeneratorLogging.LogMessage($" {item.GetType().Name} {item} {item.AttributeLists} [{item.FullSpan}] {item.Kind()}");
			}

			// check if the methods we want to add exist already 
			var setMethod = calculatorClassMembers.FirstOrDefault(member => member is MethodDeclarationSyntax method && method.Identifier.Text == "Set");

			GeneratorLogging.LogMessage("[+] Checked if methods exist in Calculator class");

			// this string builder will hold our source code for the methods we want to add
			var body = new StringBuilder();
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
			body.AppendLine($"// Synqra Model Target: {Synqra.Model.SynqraModelRuntimeInfo.TargetFramework}");
			body.AppendLine();
			body.AppendLine($"{calculatorClass.Modifiers} class {calculatorClass.Identifier} : global::{typeof(IBindableModel).FullName}, global::{typeof(INotifyPropertyChanging).FullName}, global::{typeof(INotifyPropertyChanged).FullName}");
			body.AppendLine("{");
			body.AppendLine();
			body.AppendLine("\tpublic event PropertyChangedEventHandler? PropertyChanged;");
			body.AppendLine("\tpublic event PropertyChangingEventHandler? PropertyChanging;");
			body.AppendLine();

			//if the methods do not exist, we will add them
			if (setMethod is null)
			{
				//when using a raw string the first " is the far left margin in the file
				//if you want the proper indention on the methods you will want to tab the string content at least once
				body.AppendLine(
$$"""
	global::{{typeof(ISynqraStoreContext).FullName}} IBindableModel.Store
	{
		get => field;
		set => field = value;
	}

	void IBindableModel.Set(string name, object? value)
	{
		switch (name)
		{
""");
				foreach (var pro in calculatorClass.Members.OfType<PropertyDeclarationSyntax>())
				{
					body.AppendLine(
$$"""
			case "{{pro.Identifier}}":
				this.__{{pro.Identifier}} = value as string;
				break;
""");
				}
				body.AppendLine(
"""
		}
	}
""");
			}


			foreach (var pro in calculatorClass.Members.OfType<PropertyDeclarationSyntax>())
			{
				body.AppendLine(
$$"""
	private {{pro.Type}} __{{pro.Identifier}};

	partial void On{{pro.Identifier}}Changing({{pro.Type}} value);
	partial void On{{pro.Identifier}}Changing({{pro.Type}} oldValue, {{pro.Type}} value);
	partial void On{{pro.Identifier}}Changed({{pro.Type}} value);
	partial void On{{pro.Identifier}}Changed({{pro.Type}} oldValue, {{pro.Type}} value);

	public partial {{pro.Type}} {{pro.Identifier}}
	{
		get => field;
		set
		{
            if (!global::System.Collections.Generic.EqualityComparer<string>.Default.Equals(field, value))
            {
                On{{pro.Identifier}}Changing(value);
                On{{pro.Identifier}}Changing(default, value);
                // OnPropertyChanging(new PropertyChanging);
                field = value;
                OnProperty3Changed(value);
                OnProperty3Changed(default, value);
                // OnPropertyChanged(global::CommunityToolkit.Mvvm.ComponentModel.__Internals.__KnownINotifyPropertyChangedArgs.Property3);
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
			context.AddSource($"{Path.GetFileNameWithoutExtension(calculatorClass.SyntaxTree.FilePath)}_{calculatorClass.Identifier}.Generated.cs", body.ToString());
			GeneratorLogging.LogMessage("[+] Added source to context");
		}
		catch (Exception e)
		{
			GeneratorLogging.LogMessage($"[-] Exception occurred in generator: {e}", LoggingLevel.Error);
		}
	}
}