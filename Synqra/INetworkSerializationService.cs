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
		EmergencyLog.Default.Debug($"°8 JSON Sent: {inv}{Environment.NewLine}{JsonSerializer.Serialize(inv, AppJsonContext.Default.Options)}");
		return JsonSerializer.SerializeToUtf8Bytes(inv, _jsonSerializerContext.Options);
	}

	public T Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(ReadOnlySpan<byte> bytes)
	{
		var operation = JsonSerializer.Deserialize<T>(bytes, _jsonSerializerContext.Options) ?? throw new Exception();
		EmergencyLog.Default.Debug($"°9 JSON Received: : {operation}{Environment.NewLine}{JsonSerializer.Serialize(operation, AppJsonContext.Default.Options)}");
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
		Reinitialize(); // This is temporary to improve stability during initial development. Actual idea is to allow stateful serializers

		ArraySegment<byte> span = buffer == default ? new byte[EventReplicationService.DefaultFrameSize] : buffer; // stackalloc byte[EventReplicationService.DefaultFrameSize];
		int pos = 0;
		_sbxSerializerSender.Serialize(span, obj, ref pos);
		span = span[..pos];

#if DEBUG
		var json = JsonSerializer.Serialize(obj, AppJsonContext.Default.TransportOperation);
		EmergencyLog.Default.Debug($"°8 SBX Sent: {obj}{Environment.NewLine}{json}");
		EmergencyLog.Default.DebugHexDump(span);

		var pos2 = 0;
		var des = _sbxSerializerSenderVerify.Deserialize<T>(span, ref pos2);
		var json2 = JsonSerializer.Serialize(des, AppJsonContext.Default.TransportOperation);
		EmergencyLog.Default.Debug($"°8 SBX Verify: {des}{Environment.NewLine}{json2}");
		if (json != json2)
		{
			for (int i = 0, m = Math.Max(json.Length, json2.Length); i < m; i++)
			{
				if (json[i] != json2[i])
				{
					EmergencyLog.Default.Error($"Serialization mismatch at char {i}: '{(i < json.Length ? json[i] : ' ')}' vs '{(i < json2.Length ? json2[i] : ' ')}'. Context: {json[..i][Math.Max(0, i - 10)..]}");
					break;
				}
			}
			throw new Exception($"Serialization mismatch! Failed to verify, see emergency logs at {EmergencyLog.Default.LogPath}.");
		}
#endif
		return span;
	}

	public T Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(ReadOnlySpan<byte> bytes)
	{
		Reinitialize(); // This is temporary to improve stability during initial development. Actual idea is to allow stateful serializers

		int pos = 0;
		var des = _sbxSerializerReceiver.Deserialize<T>(bytes, ref pos);
		EmergencyLog.Default.Debug($"°9 SBC Received: {des}{Environment.NewLine}{JsonSerializer.Serialize(des, AppJsonContext.Default.TransportOperation)}");
		if (pos != bytes.Length)
		{
			throw new Exception("Did not consume all bytes");
		}
		return des;
	}

}