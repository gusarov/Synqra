#if NET8_0_OR_GREATER
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Synqra;

/// <summary>
/// The browser's localStorage, through its named properties (localStorage[key] is getItem/setItem). Synchronous and outside
/// IndexedDB's locks, so a tab frozen mid IndexedDB transaction has already left its lines where other tabs can read them.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class LocalStorageEmergencyLogStorage : IEmergencyLogStorage
{
	private JSObject? _localStorage;

	private JSObject LocalStorage => _localStorage ??= OpenLocalStorage();

	public string? GetItem(string key)
	{
		string? value;
		try
		{
			value = LocalStorage.GetPropertyAsString(key);
		}
		catch (JSException ex)
		{
			throw new EmergencyLogStorageException($"localStorage refused to read {key}", ex);
		}
		return value;
	}

	public void SetItem(string key, string value)
	{
		try
		{
			LocalStorage.SetProperty(key, value);
		}
		catch (JSException ex)
		{
			throw new EmergencyLogStorageException($"localStorage refused to write {key} ({value.Length} chars)", ex);
		}
	}

	private JSObject OpenLocalStorage()
		=> JSHost.GlobalThis.GetPropertyAsJSObject("localStorage")
		?? throw new EmergencyLogStorageException("this page has no localStorage", null);
}
#endif
