// THIS FILE IS A POLYFILL FOR OLDER FRAMEWORKS
// DO NOT MODIFY IT UNLESS YOU KNOW WHAT YOU ARE DOING
// LINK THIS FILE TO OTHER PROJECTS WHERE NEEDED

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// INIT
#if NETSTANDARD2_0 || NETSTANDARD2_1

namespace System.Runtime.CompilerServices
{
	[EditorBrowsable(EditorBrowsableState.Never)]
	internal static class IsExternalInit { }
}

#endif

// RANGES
#if NETSTANDARD2_0 || NETSTANDARD2_1

namespace System
{
    internal readonly struct Index
    {
        private readonly int _value;
        private readonly bool _fromEnd;

        internal Index(int value, bool fromEnd = false)
        {
            _value = value;
            _fromEnd = fromEnd;
        }

        public int Value => _value;
        public bool IsFromEnd => _fromEnd;

        public static Index Start => new Index(0);
        public static Index End => new Index(0, fromEnd: true);

        public static implicit operator Index(int value) => new Index(value);

        public int GetOffset(int length)
            => _fromEnd ? length - _value : _value;
    }

    internal readonly struct Range
    {
        public Index Start { get; }
        public Index End { get; }

        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public static Range StartAt(Index start) => new Range(start, Index.End);
        public static Range EndAt(Index end) => new Range(Index.Start, end);
        public static Range All => new Range(Index.Start, Index.End);
    }
}

#endif

// RANGE [..16]
#if NETSTANDARD2_0 || NETSTANDARD2_1

namespace System.Runtime.CompilerServices
{
    internal static class RuntimeHelpers
    {
        public static T[] GetSubArray<T>(T[] array, Range range)
        {
            var (offset, length) = GetOffsetAndLength(range, array.Length);
            var result = new T[length];
            Array.Copy(array, offset, result, 0, length);
            return result;
        }

        private static (int Offset, int Length) GetOffsetAndLength(Range range, int arrayLength)
        {
            int start = range.Start.IsFromEnd ? arrayLength - range.Start.Value : range.Start.Value;
            int end = range.End.IsFromEnd ? arrayLength - range.End.Value : range.End.Value;
            int length = end - start;
            return (start, length);
        }
    }
}

#endif
