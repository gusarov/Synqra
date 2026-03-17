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
		SynqraTypeId = Guid.Parse(synqraTypeId ?? throw new ArgumentNullException(nameof(synqraTypeId)));
	}

	public Guid? SynqraTypeId { get; }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class SynqraLegacyTypeIdAttribute : Attribute
{
	public SynqraLegacyTypeIdAttribute(string synqraTypeId)
	{
		SynqraTypeId = Guid.Parse(synqraTypeId ?? throw new ArgumentNullException(nameof(synqraTypeId)));
	}

	public Guid SynqraTypeId { get; }
}
