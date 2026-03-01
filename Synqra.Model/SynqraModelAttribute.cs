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

	public SynqraModelAttribute(Guid synqraTypeId)
	{
		SynqraTypeId = synqraTypeId;
	}

	public Guid? SynqraTypeId { get; init; }
}
