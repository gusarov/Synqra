namespace Synqra;

public static class LocalUndoExtensions
{
	extension(Event ev)
	{
		/// <summary>
		/// The command id of every local undo: family 0 code 00 (a one-off value), staging mode 9, node 1. An undo is what a client
		/// writes into its own log to take back an event the master refused; it is never sent.
		/// </summary>
		public Guid LocalUndoCommandId => new(0xC0DEADD0, 0x1032, 0x8000, 0x90, 0x00, 0, 0, 0, 0, 0, 0x01); // C0DEADD0-1032-8000-9000-000000000001

		/// <summary>Whether this event is a client's local undo of a refused event of its own: it stays in that client's log.</summary>
		public bool IsLocalUndo => ev.CommandId == ev.LocalUndoCommandId;
	}
}
