using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Synqra;

public static class GuidExtensions
{
	static Encoding _utf8 = new UTF8Encoding(false, false);
	static long _unixEpochTicks = 0x089F7FF5F7B58000; // new DateTime(1970, 01, 01, 0, 0, 0, DateTimeKind.Utc).Ticks;
	static long _gregEpochTicks = 0x06ED6223E4344000; // new DateTime(1582, 10, 15, 0, 0, 0, DateTimeKind.Utc).Ticks;

	public static unsafe int GetVariant(this Guid guid)
	{
		byte variantByte = ((byte*)&guid)[8];

		// The variant is determined by the pattern of the most significant bits:
		// 0xxxxxxx - 0 (Apollo NCS Legacy)
		// 10xxxxxx - 1 (RFC 4122)
		// 110xxxxx - 2 (Microsoft Legacy)
		// 111xxxxx - 3 (Future/Reserved/very unlikely to be used)
		if ((variantByte & 0x80) == 0x00) // 0b0xx
		{
			return 0;
		}
		else if ((variantByte & 0xC0) == 0x80) // 0b10x
		{
			return 1;
		}
		else if ((variantByte & 0xE0) == 0xC0) // 0b110x
		{
			return 2;
		}
		else
		{
			return 3;
		}
	}

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
				return new DateTime(_gregEpochTicks + greg_100_ns, DateTimeKind.Utc);
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
				return new DateTime(_gregEpochTicks + greg_100_ns, DateTimeKind.Utc);
			}
			case 7:
			{
				long tsHigh = ints[0];
				var tsLow = shorts[0];
				long unix_64_bit_ms = (tsHigh << 16) | tsLow;

				return new DateTime(_unixEpochTicks).AddMilliseconds(unix_64_bit_ms);
			}
			default:
				throw new Exception($"Cannot get timestamp of UUID v{ver}");
		}
	}

	// https://www.rfc-editor.org/rfc/rfc9562.html?utm_source=chatgpt.com#name-namespace-id-usage-and-allo
	static readonly Guid _namespaceDns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
	static readonly Guid _namespaceUrl = new Guid("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

	[Obsolete("Use CreateVersion7 instead")]
	public static unsafe Guid CreateVersion1(ushort clockSeq, long node)
	{
		return CreateVersion1(DateTime.UtcNow, clockSeq, node);
	}

	[Obsolete("Use CreateVersion7 instead")]
	public static unsafe Guid CreateVersion1(DateTimeOffset dateTime, ushort clockSeq, long node)
	{
		var greg_100_ns = dateTime.ToUniversalTime().Ticks - _gregEpochTicks;

		Guid g = default;
		byte* bytes = (byte*)&g;

		*(uint*)&g = (uint)greg_100_ns; // write time_low
		*(ushort*)(bytes + 4) = (ushort)(greg_100_ns >> 32); // write time_mid
		*(ushort*)(bytes + 6) = (ushort)((ushort)(greg_100_ns >> 48) | (ushort)(1 << 12)); // write time_high & version 1

		clockSeq = (ushort)(clockSeq & 0x3FFF | 0x8000); // variant 1, 0x10: RFC 4122
		if (BitConverter.IsLittleEndian)
		{
			clockSeq = (ushort)((clockSeq << 8) | (clockSeq >> 8));
		}
		*(ushort*)(bytes + 8) = clockSeq;

		byte* nodeBytes = (byte*)&node;
		if (BitConverter.IsLittleEndian)
		{
			for (byte i = 0; i < 6; i++)
			{
				bytes[10 + i] = nodeBytes[5 - i];
			}
		}
		else
		{
			for (byte i = 0; i < 6; i++)
			{
				bytes[10 + i] = nodeBytes[i];
			}
		}
		return g;
	}

	[Obsolete("Use v5 with SHA1 or v8 with current best hash function")]
	public static unsafe Guid CreateVersion3Dns(string name)
	{
		return CreateVersion3(_namespaceDns, name);
	}

	[Obsolete("Use v5 with SHA1 or v8 with current best hash function")]
	public static unsafe Guid CreateVersion3Url(string url)
	{
		return CreateVersion3(_namespaceUrl, url);
	}

	[Obsolete("Use v5 with SHA1 or v8 with current best hash function")]
	public static unsafe Guid CreateVersion3(Guid namespaceId, string name)
	{
		// var max = _utf8.GetMaxByteCount(name.Length);
		return CreateVersion3(namespaceId, _utf8.GetBytes(name));
	}

	[Obsolete("Use v5 with SHA1 or v8 with current best hash function")]
	public static unsafe Guid CreateVersion3(Guid namespaceId, byte[] raw)
	{
		using var md5 = MD5.Create();

		ValidateNamespaceId(namespaceId);
		var nsBuf = namespaceId.ToByteArray();
		if (BitConverter.IsLittleEndian)
		{
			// Guid is mixed-endian
			Array.Reverse(nsBuf, 0, 4);
			Array.Reverse(nsBuf, 4, 2);
			Array.Reverse(nsBuf, 6, 2);
		}

		md5.TransformBlock(nsBuf, 0, 16, null, 0);
		md5.TransformFinalBlock(raw, 0, raw.Length);
		var hash = md5.Hash;

		hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // Set Variant to 0b10xx
		hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // Set version to 3

		if (BitConverter.IsLittleEndian)
		{
			// Guid is mixed-endian
			Array.Reverse(hash, 0, 4);
			Array.Reverse(hash, 4, 2);
			Array.Reverse(hash, 6, 2);
		}

		return new Guid(hash[..16]); // back to ram
	}

	public static unsafe Guid CreateVersion5Dns(string name)
	{
		return CreateVersion5(_namespaceDns, name);
	}

	public static unsafe Guid CreateVersion5Url(string url)
	{
		return CreateVersion5(_namespaceUrl, url);
	}

	public static unsafe Guid CreateVersion5(Guid namespaceId, string name)
	{
		return CreateVersion5(namespaceId, _utf8.GetBytes(name));
	}

	public static unsafe Guid CreateVersion5(Guid namespaceId, byte[] input)
	{
		using var sha1 = SHA1.Create();

		ValidateNamespaceId(namespaceId);
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

	[Obsolete("Use CreateVersion7 instead")]
	public static unsafe Guid CreateVersion6(ushort clockSeq, long node)
	{
		return CreateVersion6(DateTime.UtcNow, clockSeq, node);
	}

	[Obsolete("Use CreateVersion7 instead")]
	public static unsafe Guid CreateVersion6(DateTimeOffset dateTime, ushort clockSeq, long node)
	{
		var greg_100_ns = dateTime.ToUniversalTime().Ticks - _gregEpochTicks;

		Guid g = default;
		byte* bytes = (byte*)&g;

		*(uint*)&g = (uint)(greg_100_ns >> 28); // write time_high
		*(ushort*)(bytes + 4) = (ushort)(greg_100_ns >> 12); // write time_mid
		*(ushort*)(bytes + 6) = (ushort)((ushort)(greg_100_ns & 0x0FFF) | (ushort)(6 << 12)); // write time_low & version 6

		clockSeq = (ushort)(clockSeq & 0x3FFF | 0x8000); // variant 1, 0x10: RFC 4122
		if (BitConverter.IsLittleEndian)
		{
			clockSeq = (ushort)((clockSeq << 8) | (clockSeq >> 8));
		}
		*(ushort*)(bytes + 8) = clockSeq;

		byte* nodeBytes = (byte*)&node;
		if (BitConverter.IsLittleEndian)
		{
			for (byte i = 0; i < 6; i++)
			{
				bytes[10 + i] = nodeBytes[5 - i];
			}
		}
		else
		{
			for (byte i = 0; i < 6; i++)
			{
				bytes[10 + i] = nodeBytes[i];
			}
		}
		return g;
	}

	public static Guid CreateVersion7() => CreateVersion7(DateTime.UtcNow);

	// static volatile nint _state = 0;
	// static long _prevUnixTsMs = 0;
	// static int _prevSubMs = 0;
	private static long _prevOmniStamp;


	/*
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static (long tick4, int sub) NextTick4AndSub(long unixTicks)
	{
		long now4 = unixTicks >> 2;                 // ticks / 4 (100ns*4)
		int seed = (int)((unixTicks % 10_000) >> 2); // 0..2499 (your initial sub from ticks>>2)

		while (true)
		{
			Interlocked.MemoryBarrier();
			long cur = Volatile.Read(ref _state);
			long cur4 = cur >> 12;
			int curSub = (int)(cur & 0xFFF);

			long new4; int newSub;

			if (now4 > cur4)
			{
				// New 4-tick window: seed from actual time remainder
				new4 = now4;
				newSub = seed;
			}
			else
			{
				// Same or past window: monotonic bump
				new4 = cur4;
				newSub = curSub + 1;
			}

			// If we’ve consumed the 12-bit lane, roll by +1ms (== +2500 in tick4 units)
			if (newSub > 0xFFF)
			{
				new4 = new4 + 2500; // +1 ms because 10_000 ticks / 4 = 2500
				newSub = 0;
			}

			long next = (new4 << 12) | (uint)newSub;
			if (Interlocked.CompareExchange(ref _state, next, cur) == cur)
				return (new4, newSub);
		}
	}

	public static unsafe Guid CreateVersion7(DateTimeOffset timestamp)
	{
		var g = Guid.NewGuid(); 

		long ticks = timestamp.ToUniversalTime().Ticks;
		long unix_ticks = ticks - _unixEpochTicks;

		var (tick4, sub) = NextTick4AndSub(unix_ticks);

		long unix_ts_ms = (tick4 << 2) / 10000;

		byte* bytes = (byte*)&g;
		uint a = (uint)((unix_ts_ms >> 16) & 0xFFFFFFFF);
		ushort b = (ushort)unix_ts_ms;

		ushort c = (ushort)((((ushort)(sub)) | (0x7 << 12))); // version 7 in high nibble and 12 bits of sub-ms time

		*(uint*)bytes = a;
		*(ushort*)(bytes + 4) = b;
		*(ushort*)(bytes + 6) = c;

		bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 1, 0x10: RFC 4122
		return g;
	}
	*/

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static unsafe Guid CreateVersion7(DateTimeOffset timestamp)
	{
		return CreateVersion7(timestamp.ToUniversalTime().Ticks);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static unsafe Guid CreateVersion7(DateTime timestamp)
	{
		return CreateVersion7(timestamp.ToUniversalTime().Ticks);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static unsafe Guid CreateVersion7(long ticks)
	{
		// Monotonicy
		// Option 0: artificially bump ts to counter if input ts repeats (limited velocity, 1 GUID per 1 ms)
		// Option 1: intra-ms counter in rand_a short (empty bytes in normal case, but can be randomly seeded except 1 bit, guessable in 1 ms space)
		//       a) start from 0 and count if repeated ms
		//       b) start from random and reset MSB to get 50% space
		// Option 2: random_b is random first time in ms, but after that it increments like bigint or BigEndian Long (guessable in 1 ms space)
		// Option 3: use rand_a for high precision time (DateTime already has 100ns precision) This can be combined with other options. E.g. very good combo with option 0.

		// DECISION: Option 3+0. This also aligns well with anti-rollover requirement and leap seconds.

		var g = Guid.NewGuid();

		ticks -= _unixEpochTicks;

		// Omni Stamp is a combo of ms<<12 & custom sub_ms part
		long omniStamp = (ticks / 10000) << 12;
		omniStamp |= (ticks % 10000) >> 2;

		while (true)
		{
			var previousOmniStamp = _prevOmniStamp;
			if (omniStamp <= previousOmniStamp)
			{
				omniStamp = previousOmniStamp + 1;
			}
			if (Interlocked.CompareExchange(ref _prevOmniStamp, omniStamp, previousOmniStamp) == previousOmniStamp)
			{
				break; // success
			}
		}

		// long unix_ts_ms = unix_ticks / 10000;
		// int subMs = (int)((unix_ticks % 10000) >> 2); // available space is 12 bits 0..4095 but we have extra 0..9999, so we drop 2 LSBs and get 4 ticks precision (0..2499). That gives extra 39% space for intra-ms overflows and that's faster than floating point extrapolation.

		/*
		var prevUnixTsMs = _prevUnixTsMs;
		var prevSubMs = _prevSubMs;
		var diff = unix_ts_ms - prevUnixTsMs;
		if (diff < 100 && diff > -100)
		{
			unix_ts_ms = prevUnixTsMs;
			if (subMs <= prevSubMs)
			{
				subMs = Interlocked.Increment(ref _prevSubMs);
				if (subMs > 0xfff)
				{
					// overflow of sub-ms part, need to spin ms forward
					unix_ts_ms = Interlocked.Increment(ref _prevUnixTsMs);
					Interlocked.Exchange(ref _prevSubMs, subMs = 0);
				}
				else
				{
					unix_ts_ms = prevUnixTsMs;
				}
			}
			else
			{
				Interlocked.Exchange(ref _prevSubMs, subMs);
			}
		}
		else
		{
			Interlocked.Exchange(ref _prevUnixTsMs, unix_ts_ms);
			Interlocked.Exchange(ref _prevSubMs, subMs);
		}
		uint a = (uint)((unix_ts_ms >> 16) & 0xFFFFFFFF);
		ushort b = (ushort)unix_ts_ms;
		*/
		byte* bytes = (byte*)&g;

		uint a = (uint)(omniStamp >> 28);
		ushort b = (ushort)(omniStamp >> 12);


		// ushort maxRandB = 0x0FFF; // 12 bits
		// ushort c = (ushort)((((ushort)(maxRandB * (unix_100ns / 10000.0)) | (0x7 << 12)))); // version 7 in high nibble and 12 bits of sub-ms time
		ushort c = (ushort)((((ushort)(omniStamp)) | (0x7 << 12))); // version 7 in high nibble and 12 bits of sub-ms time
		// The change from 3 ticks increment to 4 ticks increment:
		// 1) Allows to avoid floating arythmetics and just do x>>2
		// 2) Reduces rand_b space from full 0-4095 range to only 0-2500. Time remained in ticks is up to 10000 ticks. So it is 40% of the range, the rest is good to avoid spinning ms to compensate overflows.
		// 3) This considered a good compromise because allows to issue bulk of GUIDs in a single ms.

		*(uint*)bytes = a;
		*(ushort*)(bytes + 4) = b;
		*(ushort*)(bytes + 6) = c;

		bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 1, 0x10: RFC 4122


		return g;
	}

	public static unsafe Guid CreateVersion8_Sha256_Dns(string name)
	{
		return CreateVersion8_Sha256(_namespaceDns, name);
	}

	public static unsafe Guid CreateVersion8_Sha256_Url(string url)
	{
		return CreateVersion8_Sha256(_namespaceUrl, url);
	}

	public static unsafe Guid CreateVersion8_Sha256(Guid namespaceId, string name)
	{
		return CreateVersion8_Sha256(namespaceId, _utf8.GetBytes(name));
	}

	static unsafe void ValidateNamespaceId(Guid namespaceId)
	{
		// generating a UUIDv4 or UUIDv7 Namespace ID value is RECOMMENDED according to the spec.
		// I also discourage using default or empty namespace IDs, as they can lead to collisions. no point to use v1-v6 except allocated values

		if (namespaceId == default)
		{
			throw new ArgumentException("Empty namespace ID", nameof(namespaceId));
		}
		/*
		if (namespaceId == Guid.AllBitsSet) // all bits set is not portable, but there is variant check below, so fine...
		{
			throw new ArgumentException("Max Guid namespace ID", nameof(namespaceId));
		}
		*/
		if (namespaceId.GetVariant() != 1)
		{
			// technically I should allow 3+ (other variants), but practically they will never be used and spec evolved to get proper versioning.
			throw new ArgumentException("Do not use variant 0x0 or 0x110 as namespace ID", nameof(namespaceId));
		}
		switch (namespaceId.GetVersion())
		{
			case 0: // not recommended
			case 2: // not recommended
			case 3: // hashbased for ns? no...
			case 5: // hashbased for ns? no...
				throw new ArgumentException("Do not use version 0, 2, 3, 5", nameof(namespaceId));
			case 1: // only legal list, others are not recommended (use v4 or v7 instead)
			{
				// RFC pattern: xxxxxxxx-9dad-11d1-80b4-00c04fd430c8
				uint* uints = (uint*)&namespaceId;
				ulong* ulongs = (ulong*)&namespaceId;
				if (uints[1] != (BitConverter.IsLittleEndian ? 0x11d19dad : 0xad9dd111) || ulongs[1] != (BitConverter.IsLittleEndian ? 0xc830d44fc000b480 : 0x80b400c04fd430c8))
				{
					throw new ArgumentException("Do not use custom v1 namespace IDs, only RFC allocated ones", nameof(namespaceId));
				}
				break;
			}
			case 4: // recommended
			case 7: // recommended
				break;
			default:
				// could be v8 or new future version, can't forbid
				break;
		}
	}

	// static readonly Guid _hashspaceSha256 = new Guid("3fb32780-953c-4464-9cfd-e85dbbe9843d"); // Hashspaces did not survived from the draft
	public static unsafe Guid CreateVersion8_Sha256(Guid namespaceId, byte[] input)
	{
		using var sha256 = SHA256.Create();

		/* Hashspaces did not survived from the draft
		var hashSpaceBuf = _hashspaceSha256.ToByteArray();
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(hashSpaceBuf, 0, 4);
			Array.Reverse(hashSpaceBuf, 4, 2);
			Array.Reverse(hashSpaceBuf, 6, 2);
		}
		sha256.TransformBlock(hashSpaceBuf, 0, 16, null, 0);
		*/

		ValidateNamespaceId(namespaceId);

		var namespaceBuf = namespaceId.ToByteArray();
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(namespaceBuf, 0, 4);
			Array.Reverse(namespaceBuf, 4, 2);
			Array.Reverse(namespaceBuf, 6, 2);
		}

		sha256.TransformBlock(namespaceBuf, 0, 16, null, 0);
		sha256.TransformFinalBlock(input, 0, input.Length);
		var hash = sha256.Hash;

		hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // Set Variant to 0b10xx
		hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // Set version to 8

		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(hash, 0, 4);
			Array.Reverse(hash, 4, 2);
			Array.Reverse(hash, 6, 2);
		}

		return new Guid(hash[..16]);
	}

	static long InterlockedStamp(ref long location, long newValue)
	{
		if (newValue > location)
		{
			location = newValue;
			return newValue;
		}
		location += 4;
		return location;
		/*
		long cur = Interlocked.Read(ref location);
		while (true)
		{
			long next;
			if (newValue > cur)
			{
				next = newValue;
			}
			else if (newValue == cur)
			{
				// v1: ceil(10000 / 4096) = 3
				// v2: x >> 2, so + 4
				next = cur + 4; 
			}
			else
			{
				// candidate < cur: no change, monotonicity preserved.
				return cur;
			}

			long prev = Interlocked.CompareExchange(ref location, next, cur);
			if (prev == cur)
			{
				return next;
			}
			cur = prev; // retry with fresher value
		}
		*/
	}

}
