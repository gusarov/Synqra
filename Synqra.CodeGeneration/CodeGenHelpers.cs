using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace Synqra.CodeGeneration;

internal static class CodeGenHelpers
{
	internal static string FQN(ITypeSymbol t) =>
		(t ?? throw new InvalidOperationException("Type not found"))
		.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

	internal static bool HasIgnoreAttribute(IPropertySymbol p)
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

	internal static IEnumerable<IPropertySymbol> GetAllInstancePropertiesOfType(INamedTypeSymbol type)
	{
		foreach (var p in type.GetMembers().OfType<IPropertySymbol>())
		{
			if (p.IsStatic) continue;
			if (p.IsIndexer) continue;
			if (p.DeclaredAccessibility == Accessibility.Private && !SymbolEqualityComparer.Default.Equals(p.ContainingType, type))
				continue;
			if (p.SetMethod == null || p.GetMethod == null)
			{
				continue;
			}
			if (HasIgnoreAttribute(p))
			{
				continue;
			}
			yield return p;
		}
	}

	// Enumerate instance properties across the full inheritance chain, most-base first, no duplicates by name.
	internal static IEnumerable<IPropertySymbol> GetAllInstancePropertiesWithAncestors(INamedTypeSymbol type, ISet<INamedTypeSymbol> except)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var types = new List<INamedTypeSymbol>();

		for (var t = type; t != null; t = t.BaseType)
		{
			if (!except.Contains(t))
			{
				types.Add(t);
			}
		}

		types.Reverse(); // base types first

		foreach (var t in types)
		{
			foreach (var p in GetAllInstancePropertiesOfType(t))
			{
				if (seen.Add(p.Name) && !p.Name.Contains("IBindableModel"))
				{
					yield return p;
				}
			}
		}
	}

	internal static string? GetFieldName(PropertyDeclarationSyntax syntax, bool? doesSupportField = null)
	{
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

	internal static string? GetFieldName(IPropertySymbol symbol, bool? doesSupportField = null)
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
}
