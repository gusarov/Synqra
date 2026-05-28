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

	/// <summary>
	/// Opt the generated property setters into optimistic concurrency control.
	/// When <c>true</c>, every setter-generated <see cref="ChangeObjectPropertyCommand"/>
	/// is submitted with the target's current version as
	/// <see cref="CommandSubmissionOptions.ExpectedTargetVersion"/>. The projection
	/// rejects the command if the target has moved on (raising
	/// <see cref="ConcurrencyException"/>).
	/// <para>
	/// Defaults to <c>false</c> to preserve historical last-writer-wins behaviour.
	/// </para>
	/// </summary>
	public bool OptimisticConcurrency { get; set; }
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
