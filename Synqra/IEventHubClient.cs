namespace Synqra;

/// <summary>
/// This is SignalR client contract
/// </summary>
public interface IEventHubClient
{
	// DO NOT RENAME! Clients receive this method name
	public Task NewEvent(Event eventObject);
}
