using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Synqra.Tests.Performance;

namespace Synqra.Tests.Helpers;

class TestConfiguration(string theKey, string theValue) : IConfiguration
{
	TestConfigurationSection? _keyValue;

	public string? this[string key] { get => key == theKey ? theValue : null; set => throw new NotImplementedException(); }

	public IEnumerable<IConfigurationSection> GetChildren()
	{
		return [_keyValue ?? (_keyValue = new TestConfigurationSection(theKey, theValue))];
	}

	public IChangeToken GetReloadToken()
	{
		throw new NotImplementedException();
	}

	public IConfigurationSection GetSection(string key)
	{
		return key == theKey ? _keyValue ?? (_keyValue = new TestConfigurationSection(theKey, theValue)) : null;
	}
}

