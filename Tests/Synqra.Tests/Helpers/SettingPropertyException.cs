namespace Synqra.Tests.Helpers;

[Serializable]
public class SettingPropertyException : Exception
{
	public SettingPropertyException()
	{
	}

	public SettingPropertyException(string message) : base(message)
	{
	}

	public SettingPropertyException(string message, Exception inner) : base(message, inner)
	{
	}
}

