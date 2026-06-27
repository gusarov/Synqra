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

	// ----------------------------------------------------------------- Link navigation

	/// <summary>Classification of a <c>[To]</c>/<c>[From]</c>/<c>[Related]</c> navigation property.</summary>
	internal sealed class LinkNavInfo
	{
		public string End = "";            // "Source" | "Target" | "Either"
		public bool IsCollection;
		public bool IsLinkTyped;
		public string ElementTypeFqn = "";
		public string LinkTypeFqn = "";
		public bool LinkTypeResolved;
		public bool IsPrimitiveLink = true;
		public bool MissingLinkType;

		/// <summary>
		/// The user declared this single-valued node-typed property with a setter
		/// (<c>{ get; set; }</c>, not <c>{ get; }</c>) — opt-in, never forced. Collections don't get
		/// a setter regardless of this flag; "set the whole collection at once" isn't a sensible op.
		/// </summary>
		public bool WantsSetter;
	}

	/// <summary>Returns nav info when the property carries a link-navigation attribute, otherwise null.</summary>
	internal static LinkNavInfo? TryGetLinkNav(IPropertySymbol prop)
	{
		AttributeData? navAttr = null;
		string? end = null;
		foreach (var a in prop.GetAttributes())
		{
			switch (a.AttributeClass?.Name)
			{
				case "ToAttribute": navAttr = a; end = "Source"; break;
				case "FromAttribute": navAttr = a; end = "Target"; break;
				case "RelatedAttribute": navAttr = a; end = "Either"; break;
			}
			if (navAttr != null)
			{
				break;
			}
		}
		if (navAttr is null || end is null)
		{
			return null;
		}

		var info = new LinkNavInfo { End = end, WantsSetter = prop.SetMethod is not null };

		ITypeSymbol elementType;
		if (prop.Type is INamedTypeSymbol nt && nt.IsGenericType && nt.TypeArguments.Length == 1 && IsSupportedNavCollection(nt))
		{
			info.IsCollection = true;
			elementType = nt.TypeArguments[0];
		}
		else
		{
			info.IsCollection = false;
			elementType = prop.Type;
		}

		info.IsLinkTyped = InheritsLink(elementType);
		info.ElementTypeFqn = FQN(elementType);

		INamedTypeSymbol? linkTypeSymbol = null;
		if (info.IsLinkTyped)
		{
			linkTypeSymbol = elementType as INamedTypeSymbol;
		}
		else if (navAttr.ConstructorArguments.Length > 0 && navAttr.ConstructorArguments[0].Value is INamedTypeSymbol et)
		{
			linkTypeSymbol = et;
		}
		else
		{
			info.MissingLinkType = true;
		}

		if (linkTypeSymbol is not null)
		{
			info.LinkTypeResolved = true;
			info.LinkTypeFqn = FQN(linkTypeSymbol);
			info.IsPrimitiveLink = IsPrimitiveLink(linkTypeSymbol);
		}

		return info;
	}

	static bool IsSupportedNavCollection(INamedTypeSymbol nt)
	{
		var def = nt.ConstructedFrom.ToDisplayString();
		return def.StartsWith("System.Collections.Generic.ICollection<", StringComparison.Ordinal)
			|| def.StartsWith("System.Collections.Generic.IList<", StringComparison.Ordinal)
			|| def.StartsWith("System.Collections.Generic.IReadOnlyList<", StringComparison.Ordinal)
			|| def.StartsWith("System.Collections.Generic.IReadOnlyCollection<", StringComparison.Ordinal)
			|| def.StartsWith("System.Collections.Generic.IEnumerable<", StringComparison.Ordinal);
	}

	internal static bool InheritsLink(ITypeSymbol? t)
	{
		for (var b = t as INamedTypeSymbol; b is not null; b = b.BaseType)
		{
			if (b.Name == "Link" && b.ContainingNamespace?.ToDisplayString() == "Synqra")
			{
				return true;
			}
		}
		return false;
	}

	// A link is "primitive" when it adds no settable instance property of its own beyond the
	// framework endpoints. Walk from the concrete type up, stopping at the Synqra framework bases.
	static bool IsPrimitiveLink(INamedTypeSymbol linkType)
	{
		for (var t = linkType; t is not null; t = t.BaseType)
		{
			if ((t.Name is "Link" or "DirectedLink" or "UndirectedLink") && t.ContainingNamespace?.ToDisplayString() == "Synqra")
			{
				break;
			}
			foreach (var m in t.GetMembers().OfType<IPropertySymbol>())
			{
				if (!m.IsStatic && !m.IsIndexer && m.SetMethod is not null)
				{
					return false;
				}
			}
		}
		return true;
	}
}
