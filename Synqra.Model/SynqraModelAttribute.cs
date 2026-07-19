using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class SynqraModelAttribute : Attribute
{
	public SynqraModelAttribute()
	{

	}

	public SynqraModelAttribute(string synqraTypeId)
	{
		SynqraTypeId = Guid.TryParse(synqraTypeId ?? throw new ArgumentNullException(nameof(synqraTypeId)), out var guid) ? guid : throw new ArgumentException("Invalid GUID format", nameof(synqraTypeId));
	}

	public Guid? SynqraTypeId { get; }
}

/// <summary>
/// Registers one or more <b>former</b> type ids as back-compat aliases for this model, so events/
/// documents persisted under an old id still resolve after the current <see cref="SynqraModelAttribute"/>
/// id changes. This is how a type's id can be changed without orphaning existing data — the migration
/// mechanism. Repeatable (a type may have accumulated several old ids over time).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class SynqraLegacyTypeIdAttribute : Attribute
{
	public SynqraLegacyTypeIdAttribute(string synqraTypeId)
	{
		SynqraTypeId = Guid.Parse(synqraTypeId ?? throw new ArgumentNullException(nameof(synqraTypeId)));
	}

	public Guid SynqraTypeId { get; }
}
