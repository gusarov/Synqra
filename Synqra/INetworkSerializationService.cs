using Synqra.BinarySerializer;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

public interface INetworkSerializationService
{
	bool IsTextOrBinary { get; }
	ulong Magic { get; }
	void Reinitialize();
	ArraySegment<byte> Serialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(T inv, ArraySegment<byte> buffer);
	[Obsolete("Use Serialize with buffer parameter to reduce allocations")]
	ArraySegment<byte> Serialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(T inv) => Serialize(inv, default);
	T Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(ReadOnlySpan<byte> bytes);
}

public class JsonNetworkSerializationService : INetworkSerializationService
{
	private readonly JsonSerializerContext _jsonSerializerContext;

	const ulong _magic = 0xBC8ED5144A534F4Eul; // "JSON"
	public ulong Magic => _magic;

	public bool IsTextOrBinary => true;

	public JsonNetworkSerializationService(JsonSerializerContext? jsonSerializerContext = null)
	{
		_jsonSerializerContext = jsonSerializerContext ?? AppJsonContext.Default;
	}

	public void Reinitialize()
	{
	}

	public ArraySegment<byte> Serialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(T inv, ArraySegment<byte> buffer)
	{
		return JsonSerializer.SerializeToUtf8Bytes(inv, _jsonSerializerContext.Options);
	}

	public T Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(ReadOnlySpan<byte> bytes)
	{
		var operation = JsonSerializer.Deserialize<T>(bytes, _jsonSerializerContext.Options) ?? throw new Exception();
		return operation;
	}
}

public class SbxNetworkSerializationService : INetworkSerializationService
{
	const ulong _magic = 0xBC8ED5144A534258ul; // "SBX"

	private SBXSerializer _sbxSerializerSenderVerify = new SBXSerializer();
	private SBXSerializer _sbxSerializerSender = new SBXSerializer();
	private SBXSerializer _sbxSerializerReceiver = new SBXSerializer();

	public bool IsTextOrBinary => false;

	public ulong Magic => _magic;

	public void Reinitialize()
	{
		_sbxSerializerSender = new SBXSerializer();
		_sbxSerializerSenderVerify = new SBXSerializer();
		_sbxSerializerReceiver = new SBXSerializer();

	}

	public ArraySegment<byte> Serialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(T obj, ArraySegment<byte> buffer)
	{
		ArraySegment<byte> span = buffer == default ? new byte[EventReplicationService.DefaultFrameSize] : buffer; // stackalloc byte[EventReplicationService.DefaultFrameSize];
		int pos = 0;
		_sbxSerializerSender.Serialize(span, obj, ref pos);
		span = span[..pos];

#if DEBUG
		var json = JsonSerializer.Serialize(obj, AppJsonContext.Default.TransportOperation);
		EmergencyLog.Default.Debug("°8 SEND Serialize: " + json);
		EmergencyLog.Default.DebugHexDump(span);

		var des = _sbxSerializerSenderVerify.Deserialize<T>(span, ref pos);
		var json2 = JsonSerializer.Serialize(des, AppJsonContext.Default.TransportOperation);
		EmergencyLog.Default.Debug("°8 SEND VERIFY: " + json2);
		if (json != json2)
		{
			throw new Exception($"Serialization mismatch! Failed to verify, see emergency logs at {EmergencyLog.Default.LogPath}.");
		}
#endif
		return span;
	}

	public T Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(ReadOnlySpan<byte> bytes)
	{
		int pos = 0;
		var des = _sbxSerializerReceiver.Deserialize<T>(bytes, ref pos);
		if (pos != bytes.Length)
		{
			throw new Exception("Did not consume all bytes");
		}
		return des;
	}

}