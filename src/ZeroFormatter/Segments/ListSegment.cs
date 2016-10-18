﻿using System;
using System.Collections;
using System.Collections.Generic;
using ZeroFormatter.Formatters;
using ZeroFormatter.Internal;

namespace ZeroFormatter.Segments
{
    public abstract class ListSegment<T> : IList<T>, IReadOnlyList<T>
    {
        protected readonly ArraySegment<byte> originalBytes;
        protected readonly Formatter<T> formatter;

        protected DirtyTracker tracker;

        protected int length;
        protected T[] cache;        // if modified, use cache start
        protected bool[] isCached;  // if modified, isCached is smaller than cache.length; be careful
        protected bool isAllCached;

        internal ListSegment(DirtyTracker tracker, int length)
        {
            this.isAllCached = true;
            this.cache = new T[length];
            this.length = length;
            this.tracker = tracker.CreateChild();
            this.formatter = Formatters.Formatter<T>.Default;
        }

        public ListSegment(DirtyTracker tracker, ArraySegment<byte> originalBytes, int length)
        {
            this.originalBytes = originalBytes;
            this.formatter = Formatters.Formatter<T>.Default;
            this.length = length;
            this.tracker = tracker.CreateChild();
        }

        protected void CreateCacheWhenNotYet()
        {
            if (cache == null)
            {
                cache = new T[length];
                isCached = new bool[length];
            }
        }

        protected void CacheAllWhenNotYet()
        {
            CreateCacheWhenNotYet();
            if (!isAllCached)
            {
                var array = originalBytes.Array;
                for (int i = 0; i < length; i++)
                {
                    if (!isCached[i])
                    {
                        var offset = GetOffset(i);
                        int _;
                        cache[i] = formatter.Deserialize(ref array, offset, tracker, out _);
                        isCached[i] = true;
                    }
                }
                isAllCached = true;
            }
        }

        public abstract T this[int index] { get; set; }

        protected abstract int GetOffset(int index);

        public int Count
        {
            get
            {
                return length;
            }
        }

        public bool IsReadOnly
        {
            get
            {
                return false;
            }
        }

        public bool Contains(T item)
        {
            return IndexOf(item) != -1;
        }

        public int IndexOf(T item)
        {
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < length; i++)
            {
                if (comparer.Equals(this[i], item)) return i;
            }
            return -1;
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            if (!isAllCached)
            {
                for (int i = 0, j = arrayIndex; i < length; i++, j++)
                {
                    array[j] = this[i];
                }
            }
            else
            {
                Array.Copy(cache, 0, array, arrayIndex, length);
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < this.Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        // If use mutable operation, cache all.

        public void Add(T item)
        {
            CacheAllWhenNotYet();

            if (cache.Length == length)
            {
                Array.Resize(ref cache, (length == 0) ? 4 : length * 2);
            }
            cache[length] = item;
            length++;
            tracker.Dirty();
        }

        public void Clear()
        {
            isAllCached = true; // cached:)
            if (cache != null)
            {
                Array.Clear(cache, 0, cache.Length);
            }
            else
            {
                cache = new T[0];
            }
            length = 0;
            tracker.Dirty();
        }

        public void Insert(int index, T item)
        {
            if (index > this.length)
            {
                throw new ArgumentOutOfRangeException("index is out of range:" + index);
            }

            CacheAllWhenNotYet();

            if (cache.Length == length)
            {
                Array.Resize(ref cache, (length == 0) ? 4 : length * 2);
            }

            if (index < this.length)
            {
                Array.Copy(this.cache, index, this.cache, index + 1, this.length - index);
            }
            cache[index] = item;
            length++;
            tracker.Dirty();
        }

        public bool Remove(T item)
        {
            CacheAllWhenNotYet();

            var i = IndexOf(item);
            if (i != -1)
            {
                RemoveAt(i);
                return true;
            }
            return false;
        }

        public void RemoveAt(int index)
        {
            tracker.Dirty();
            if (index >= this.length)
            {
                throw new ArgumentOutOfRangeException("index is out of range:" + index);
            }
            CacheAllWhenNotYet();

            this.length--;
            if (index < this.length)
            {
                Array.Copy(this.cache, index + 1, this.cache, index, this.length - index);
            }
            this.cache[this.length] = default(T);
            tracker.Dirty();
        }
    }

    // Layout: FixedSize -> [count:int][t format...] if count== -1 is null
    public class FixedListSegment<T> : ListSegment<T>, IZeroFormatterSegment
    {
        readonly int elementSize;

        internal static FixedListSegment<T> Create(DirtyTracker tracker, byte[] bytes, int offset, out int byteSize)
        {
            if (offset == -1)
            {
                byteSize = 0;
                return null;
            }

            var formatter = Formatters.Formatter<T>.Default;
            var formatterLength = formatter.GetLength();
            if (formatterLength == null) throw new InvalidOperationException("T should be fixed length. Type: " + typeof(T).Name);

            var length = BinaryUtil.ReadInt32(ref bytes, offset);
            if (length == -1)
            {
                byteSize = 4;
                return null;
            }

            byteSize = formatterLength.Value * length + 4;
            var list = new FixedListSegment<T>(tracker, new ArraySegment<byte>(bytes, offset, byteSize), length);
            return list;
        }

        FixedListSegment(DirtyTracker tracker, ArraySegment<byte> originalBytes, int length)
            : base(tracker, originalBytes, length)
        {
            var formatterLength = formatter.GetLength();
            if (formatterLength == null) throw new InvalidOperationException("T should be fixed length. Type: " + typeof(T).Name);

            elementSize = formatterLength.Value;
        }

        protected override int GetOffset(int index)
        {
            return originalBytes.Offset + 4 + (elementSize * index);
        }

        public override T this[int index]
        {
            get
            {
                if (index > length)
                {
                    throw new ArgumentOutOfRangeException("index > Count");
                }

                if (!isAllCached)
                {
                    var array = originalBytes.Array;
                    var offset = GetOffset(index);
                    int _;
                    return formatter.Deserialize(ref array, offset, tracker, out _);
                }
                else
                {
                    return cache[index];
                }
            }

            set
            {
                if (index > Count)
                {
                    throw new ArgumentOutOfRangeException("index > Count");
                }

                if (!isAllCached)
                {
                    // FixedList[set] does not do dirty.
                    var array = originalBytes.Array;
                    var offset = 4 + (elementSize * index);
                    formatter.Serialize(ref array, originalBytes.Offset + offset, value);
                }
                else
                {
                    cache[index] = value;
                    tracker.Dirty();
                }
            }
        }

        public bool CanDirectCopy()
        {
            return (tracker == null) ? false : !tracker.IsDirty && (originalBytes != null);
        }

        public ArraySegment<byte> GetBufferReference()
        {
            return originalBytes;
        }

        public int Serialize(ref byte[] bytes, int offset)
        {
            if (CanDirectCopy())
            {
                BinaryUtil.EnsureCapacity(ref bytes, offset, originalBytes.Count);
                Buffer.BlockCopy(originalBytes.Array, originalBytes.Offset, bytes, offset, originalBytes.Count);
                return originalBytes.Count;
            }
            else
            {
                var writeSize = this.Count * elementSize + 4;
                if (bytes == null)
                {
                    bytes = new byte[writeSize];
                }

                offset += BinaryUtil.WriteInt32(ref bytes, offset, this.Count);
                for (int i = 0; i < this.Count; i++)
                {
                    offset += formatter.Serialize(ref bytes, offset, this[i]);
                }

                return writeSize;
            }
        }
    }

    // Layout: VariableSize -> [int byteSize][count:int][elementOffset:int...][t format...]
    // if byteSize == -1 is null
    public class VariableListSegment<T> : ListSegment<T>, IZeroFormatterSegment
    {
        internal static VariableListSegment<T> Create(DirtyTracker tracker, byte[] bytes, int offset, out int byteSize)
        {
            if (offset == -1)
            {
                byteSize = 0;
                return null;
            }

            byteSize = BinaryUtil.ReadInt32(ref bytes, offset);
            if (byteSize == -1)
            {
                byteSize = 4;
                return null;
            }

            var length = BinaryUtil.ReadInt32(ref bytes, offset + 4);
            var list = new VariableListSegment<T>(tracker, new ArraySegment<byte>(bytes, offset, byteSize), length);
            return list;
        }

        VariableListSegment(DirtyTracker tracker, ArraySegment<byte> originalBytes, int length)
            : base(tracker, originalBytes, length)
        {
            var formatterLength = formatter.GetLength();
            if (formatterLength != null) throw new InvalidOperationException("T has fixed length, use FixedListSegement instead. Type: " + typeof(T).Name);
        }

        protected override int GetOffset(int index)
        {
            var array = originalBytes.Array;
            return BinaryUtil.ReadInt32(ref array, originalBytes.Offset + 8 + (4 * index));
        }

        public override T this[int index]
        {
            get
            {
                if (index > length)
                {
                    throw new ArgumentOutOfRangeException("index > Count");
                }
                CreateCacheWhenNotYet();

                if (!isAllCached && !isCached[index])
                {
                    var array = originalBytes.Array;
                    var offset = GetOffset(index);
                    int _;
                    cache[index] = formatter.Deserialize(ref array, offset, tracker, out _);
                    isCached[index] = true;
                }

                return cache[index];
            }

            set
            {
                if (index > Count)
                {
                    throw new ArgumentOutOfRangeException("index > Count");
                }
                CreateCacheWhenNotYet();

                cache[index] = value;
                if (!isAllCached)
                {
                    isCached[index] = true;
                }
                tracker.Dirty();
            }
        }

        public bool CanDirectCopy()
        {
            return (tracker == null) ? false : !tracker.IsDirty && (originalBytes != null);
        }

        public ArraySegment<byte> GetBufferReference()
        {
            return originalBytes;
        }

        public int Serialize(ref byte[] bytes, int offset)
        {
            if (CanDirectCopy())
            {
                BinaryUtil.EnsureCapacity(ref bytes, offset, originalBytes.Count);
                Buffer.BlockCopy(originalBytes.Array, originalBytes.Offset, bytes, offset, originalBytes.Count);
                return originalBytes.Count;
            }
            else
            {
                var startoffset = offset;

                var count = 0;
                offset = (startoffset + 8) + (Count * 4);
                for (int i = 0; i < Count; i++)
                {
                    var item = this[i];

                    var size = formatter.Serialize(ref bytes, offset, item);
                    BinaryUtil.WriteInt32(ref bytes, (startoffset + 8) + count * 4, offset);
                    offset += size;
                    count++;
                }

                BinaryUtil.WriteInt32(ref bytes, startoffset + 4, Count);

                var totalBytes = offset - startoffset;
                BinaryUtil.WriteInt32(ref bytes, startoffset, totalBytes);

                return totalBytes;
            }
        }
    }
}