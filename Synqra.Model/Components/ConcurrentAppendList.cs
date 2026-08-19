using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Synqra;

/// <summary>
/// Append-ordered collection that is safe to enumerate while another thread writes to it, without
/// copying and without blocking either side.
/// <para>
/// Stores are process-wide singletons, so "event-apply mutates on a background thread while a request
/// thread reads" is the ordinary case. A bare <see cref="List{T}"/> turns that into
/// <c>InvalidOperationException: Collection was modified; enumeration operation may not execute</c>.
/// </para>
/// <para>
/// Two obvious fixes were rejected. Locking and handing readers a copy makes every enumeration
/// allocate the whole collection — ruinous for a store that is enumerated on a timer. Copy-on-write
/// makes every append O(n), so replaying a stream of n events costs O(n²).
/// </para>
/// <para>
/// So: a <see cref="ConcurrentQueue{T}"/>, whose enumerator walks segments in place (no copy) and is
/// safe against concurrent writers, which keeps append O(1) and lock-free while preserving insertion
/// order — order is load-bearing here, callers do use "the last one added". Removal is the rare
/// operation (an entity or component being deleted), so it rebuilds the queue under a write lock and
/// swaps it in: O(n), but it does not slow down the append and read paths that actually run hot. A
/// reader that grabbed the previous queue keeps enumerating it and simply observes the pre-removal
/// state, which is the same staleness any concurrent reader already accepts.
/// </para>
/// </summary>
internal sealed class ConcurrentAppendList<T>
	where T : class
{
	// Readers take this reference once and enumerate it; Remove swaps in a replacement.
	volatile ConcurrentQueue<T> _items = new ConcurrentQueue<T>();
	readonly object _writeGate = new object();

	public int Count => _items.Count;

	public void Add(T item)
	{
		_items.Enqueue(item);
	}

	public bool Remove(T item)
	{
		lock (_writeGate)
		{
			var current = _items;
			var replacement = new ConcurrentQueue<T>();
			var removed = false;
			foreach (var existing in current)
			{
				// Identity, not equality: a model that overrides Equals (or is a record) must not
				// cause a different-but-equal instance to be dropped instead of this one.
				if (!removed && ReferenceEquals(existing, item))
				{
					removed = true;
					continue;
				}
				replacement.Enqueue(existing);
			}
			if (removed)
			{
				_items = replacement;
			}
			return removed;
		}
	}

	public bool Contains(T item)
	{
		foreach (var existing in _items)
		{
			if (ReferenceEquals(existing, item))
			{
				return true;
			}
		}
		return false;
	}

	public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

	public void CopyTo(T[] array, int arrayIndex)
	{
		foreach (var existing in _items)
		{
			if (arrayIndex >= array.Length)
			{
				throw new System.ArgumentException("Array is too small to copy the collection.", nameof(array));
			}
			array[arrayIndex++] = existing;
		}
	}

	/// <summary>Positional read. O(n) — kept only because <c>IReadOnlyList&lt;T&gt;</c> is public shape.</summary>
	public T ElementAt(int index)
	{
		if (index >= 0)
		{
			var i = 0;
			foreach (var existing in _items)
			{
				if (i++ == index)
				{
					return existing;
				}
			}
		}
		throw new System.ArgumentOutOfRangeException(nameof(index));
	}
}
