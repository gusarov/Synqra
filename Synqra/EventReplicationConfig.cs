using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

public class EventReplicationConfig
{
	public virtual ushort Port { get; set; }
}

public class DelegatedEventReplicationConfig : EventReplicationConfig
{
	private readonly Func<ushort> _func;

	public DelegatedEventReplicationConfig(Func<ushort> func)
	{
		_func = func;
	}

	public override ushort Port => _func();
}

