using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// A schema for Syncron serializer
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Interface, AllowMultiple = true)]
public class SchemaAttribute : Attribute
{
	public SchemaAttribute(double version, string schema)
	{
		Version = version;
		Schema = schema;
	}

	public double Version { get; }
	public string Schema { get; }
}
