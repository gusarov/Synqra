using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Synqra;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
sealed class SynqraModelAttribute : Attribute
{
	public SynqraModelAttribute()
	{
		
	}

	public SynqraModelAttribute(Type type)
	{
	}
}
 