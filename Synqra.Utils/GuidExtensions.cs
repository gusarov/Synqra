using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Synqra;

public static class GuidExtensions
{
	static Encoding _utf8 = new UTF8Encoding(false, false);

	public static unsafe int GetVersion(this Guid guid, bool zeroForDefault = true)
	{
		if (zeroForDefault && guid == Guid.Empty)
		{
			return 0;
		}
		var variant = guid.GetVariant();
		if (variant != 1)
		{
			throw new Exception($"Cannot get version of non-RFC UUIDs. Variant is {variant} but only 1 is supported");
		}
#if NET9_0_OR_GREATER
		return guid.Version;
#elif __NET8_0_OR_GREATER
		ref var guidMap = ref MemoryMarshal.AsRef<GuidMap>(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref guid, 1))); // no copy, just reinterpret
		return guidMap._c >> 12;
#else
		byte* bytes = (byte*)&guid;
		byte versionByte = bytes[BitConverter.IsLittleEndian ? 7 : 6]; // due to mixed Endian layout of GUIDs, version is actually in byte 7, not 6.
		return versionByte >> 4;
#endif
	}

	public static unsafe int GetVariant(this Guid guid)
	{
		byte variantByte;
		byte* bytes = (byte*)&guid;
		variantByte = bytes[8];

		// The variant is determined by the pattern of the most significant bits:
		// 0xxxxxxx - 0 (Apollo NCS Legacy)
		// 10xxxxxx - 1 (RFC 4122)
		// 110xxxxx - 2 (Microsoft Legacy)
		// 111xxxxx - 3 (Future/reserved)
		int variant;
		if ((variantByte & 0x80) == 0x00) // 0b0xx
			variant = 0;
		else if ((variantByte & 0xC0) == 0x80) // 0b10x
			variant = 1;
		else if ((variantByte & 0xE0) == 0xC0) // 0b110x
			variant = 2;
		else
			variant = 3; // Reserved
		return variant;
	}

	public static Guid CreateVersion7() => CreateVersion7(DateTimeOffset.UtcNow);

	public static unsafe Guid CreateVersion7(DateTimeOffset timestamp)
	{
#if NET9_0_OR_GREATER
		return Guid.CreateVersion7();
#else
		var g = Guid.NewGuid();

		long unix_ts_ms = timestamp.ToUnixTimeMilliseconds();
		if (unix_ts_ms < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(timestamp), "Timestamp must be after Unix epoch (1970-01-01T00:00:00Z)");
		}

		byte* bytes = (byte*)&g;
		uint a = (uint)((unix_ts_ms >> 16) & 0xFFFFFFFF);
		ushort b = (ushort)((unix_ts_ms >> 0) & 0xFFFF);
		ushort c = (ushort)((((unix_ts_ms >> 48) & 0x0FFF) | (0x7 << 12))); // version 7 in high nibble

		*(uint*)bytes = a;
		*(ushort*)(bytes + 4) = b;
		*(ushort*)(bytes + 6) = c;

		bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

		return g;
#endif
	}

	public static unsafe DateTime GetTimestamp(this Guid guid, bool zeroForDefault = true)
	{
		if (zeroForDefault && guid == Guid.Empty)
		{
			return default;
		}
		var ver = guid.GetVersion(zeroForDefault: zeroForDefault);

		var bytes = (byte*)&guid;
		var ints = (uint*)&guid;
		var shorts = (ushort*)(bytes + 4);

		switch (ver)
		{
			case 1:
			{
				var tsLow = ints[0];
				long tsMid = shorts[0];
				long tsHigh = shorts[1] & 0x0FFF;

				var greg_100_ns = (tsHigh << 48) | (tsMid << 32) | tsLow;
				long greg_Unix_offset = 0x01b21dd213814000;
				long unix_64_bit_100ns = greg_100_ns - greg_Unix_offset;

				var timestamp = 0x89F7FF5F7B58000 + unix_64_bit_100ns;
				return new DateTime(timestamp, DateTimeKind.Utc);
			}
			/*
			case 2:
				throw new NotImplementedException();
			*/
			case 6:
			{
				long tsHigh = ints[0];
				long tsMid = shorts[0];
				var tsLow = shorts[1] & 0x0FFF;

				var greg_100_ns = (tsHigh << 28) | (tsMid << 12) | tsLow;
				long greg_Unix_offset = 0x01b21dd213814000;
				long unix_64_bit_100ns = greg_100_ns - greg_Unix_offset;

				var timestamp = 0x89F7FF5F7B58000 + unix_64_bit_100ns;
				return new DateTime(timestamp, DateTimeKind.Utc);
			}
			case 7:
			{
				long tsHigh = ints[0];
				var tsLow = shorts[0];
				long unix_64_bit_ms = (tsHigh << 16) | tsLow;
				// return DateTimeOffset.FromUnixTimeMilliseconds(unix_64_bit_ms);
				// var timestamp = 0x89F7FF5F7B58000 + unix_64_bit_ms * 10;
				// return new DateTime(timestamp, DateTimeKind.Utc);
				return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(unix_64_bit_ms);
			}
			default:
				throw new Exception($"Cannot get timestamp of UUID v{ver}");
		}
	}

	// https://www.rfc-editor.org/rfc/rfc9562.html?utm_source=chatgpt.com#name-namespace-id-usage-and-allo
	static readonly Guid _namespaceDns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
	static readonly Guid _namespaceUrl = new Guid("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

	public static unsafe Guid CreateVersion3Dns(string name)
	{
		return CreateVersion3(_namespaceDns, name);
	}

	public static unsafe Guid CreateVersion3Url(string url)
	{
		return CreateVersion3(_namespaceUrl, url);
	}

	public static unsafe Guid CreateVersion5Dns(string name)
	{
		return CreateVersion5(_namespaceDns, name);
	}

	public static unsafe Guid CreateVersion5Url(string url)
	{
		return CreateVersion5(_namespaceUrl, url);
	}

	public static unsafe Guid CreateVersion3(Guid namespaceId, string name)
	{
		// var max = _utf8.GetMaxByteCount(name.Length);
		return CreateVersion3(namespaceId, _utf8.GetBytes(name));
	}

	public static unsafe Guid CreateVersion3(Guid namespaceId, byte[] raw)
	{
		using var md5 = MD5.Create();

		var nsBuf = namespaceId.ToByteArray();
		if (BitConverter.IsLittleEndian)
		{
			// Guid is mixed-endian, but RFC wants big-endian
			Array.Reverse(nsBuf, 0, 4);
			Array.Reverse(nsBuf, 4, 2);
			Array.Reverse(nsBuf, 6, 2);
		}

		md5.TransformBlock(nsBuf, 0, 16, null, 0);
		md5.TransformFinalBlock(raw, 0, raw.Length);
		var hash = md5.Hash;

#if DEBUG && NET9_0
		var hex = new Guid(Convert.ToHexString(hash));
#endif

		hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // Set Variant to 0b10xx
#if DEBUG && NET9_0
		var hex2 = new Guid(Convert.ToHexString(hash));
#endif
		hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // Set version to 3 (4-2-2 long-short-short Microsoft historical layout for mixed Endian means byte 6 is actually byte 7 before casting, because 2nd short is LittleEndian and it's bytes are swapped)
#if DEBUG && NET9_0
		var hex3 = new Guid(Convert.ToHexString(hash));
#endif

		if (BitConverter.IsLittleEndian)
		{
			// Guid is mixed-endian, but RFC wants big-endian
			Array.Reverse(hash, 0, 4);
			Array.Reverse(hash, 4, 2);
			Array.Reverse(hash, 6, 2);
		}

		return new Guid(hash[..16]); // we can set 6-th byte directly, but that requires to pass bigendian: true. And that's not available in .Net Standard 2.0. Instead, we just select proper byte manually.
	}

	public static unsafe Guid CreateVersion5(Guid namespaceId, string name)
	{
		return CreateVersion5(namespaceId, _utf8.GetBytes(name));
	}

	public static unsafe Guid CreateVersion5(Guid namespaceId, byte[] input)
	{
		using var sha1 = SHA1.Create();

		var nsBuf = namespaceId.ToByteArray();
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(nsBuf, 0, 4);
			Array.Reverse(nsBuf, 4, 2);
			Array.Reverse(nsBuf, 6, 2);
		}

		sha1.TransformBlock(nsBuf, 0, 16, null, 0);
		sha1.TransformFinalBlock(input, 0, input.Length);
		var hash = sha1.Hash;

		hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // Set Variant to 0b10xx
		hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // Set version to 5

		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(hash, 0, 4);
			Array.Reverse(hash, 4, 2);
			Array.Reverse(hash, 6, 2);
		}

		return new Guid(hash[..16]);

	}
}
