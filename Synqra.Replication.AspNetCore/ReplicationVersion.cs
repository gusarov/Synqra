namespace Synqra.Replication.AspNetCore;

/// <summary>
/// The build this master runs and its reload demand, told to every client that asks (<see cref="VersionRequest"/>) on connect and
/// pushed as they change. A host registers it (with its build) for the replication endpoint to use; without it, the endpoint answers
/// with no build and never asks for a reload.
/// </summary>
public sealed class ReplicationVersion
{
	readonly object _gate = new();
	string? _build;
	Guid _reloadEpoch;

	public ReplicationVersion(string? build = null)
	{
		_build = build;
	}

	/// <summary>The build announced: a client on another build reloads.</summary>
	public string? Build
	{
		get
		{
			lock (_gate)
			{
				return _build;
			}
		}
	}

	/// <summary>Raised with the frame to push and the one client it is for, or null for every client that asked.</summary>
	public event Action<ServerVersion, Guid?>? Changed;

	/// <summary>The frame a client is told on connect.</summary>
	public ServerVersion Frame
	{
		get
		{
			lock (_gate)
			{
				return new ServerVersion { Build = _build, ReloadEpoch = _reloadEpoch };
			}
		}
	}

	/// <summary>Has every open client (or the one <paramref name="node"/>) reload, as after a schema change; a client connecting later does not.</summary>
	public void RequireReload(Guid? node = null)
	{
		ServerVersion frame;
		lock (_gate)
		{
			frame = new ServerVersion { Build = _build, ReloadEpoch = Guid.CreateVersion7() };
			if (node is null)
			{
				_reloadEpoch = frame.ReloadEpoch;
			}
		}
		Changed?.Invoke(frame, node);
	}

	/// <summary>Tells the open clients (or the one <paramref name="node"/>) that this master runs <paramref name="build"/>: those on another build reload.</summary>
	public void Announce(string? build, Guid? node = null)
	{
		ServerVersion frame;
		lock (_gate)
		{
			if (node is null)
			{
				_build = build;
			}
			frame = new ServerVersion { Build = build, ReloadEpoch = _reloadEpoch };
		}
		Changed?.Invoke(frame, node);
	}
}
