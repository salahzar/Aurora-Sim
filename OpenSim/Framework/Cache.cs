using System;
using System.Collections.Generic;
using libsecondlife;

namespace Opensim.Framework
{
    // The delegate we will use for performing fetch from backing store
    //
    public delegate Object FetchDelegate(LLUUID index);
    public delegate bool ExpireDelegate(LLUUID index);

    // Strategy
    //
    // Conservative = Minimize memory. Expire items quickly.
    // Balanced = Expire items with few hits quickly.
    // Aggressive = Keep cache full. Expire only when over 90% and adding
    //
    public enum CacheStrategy
    {
        Conservative = 0,
        Balanced = 1,
        Aggressive = 2
    }

    // Select classes to store data on different media
    //
    public enum CacheMedium
    {
        Memory = 0,
        File = 1
    }

    public enum CacheFlags
    {
        CacheMissing = 1,
        AllowUpdate = 2
    }

    // The base class of all cache objects. Implements comparison and sorting
    // by the LLUUID member.
    //
    // This is not abstract because we need to instantiate it briefly as a
    // method parameter
    //
    public class CacheItemBase : IEquatable<CacheItemBase>, IComparable<CacheItemBase>
    {
        public LLUUID uuid;
        public DateTime entered;
        public DateTime lastUsed;
        public DateTime expires = new DateTime(0);
        public int hits = 0;

        public virtual Object Retrieve()
        {
            return null;
        }

        public virtual void Store(Object data)
        {
        }

        public CacheItemBase(LLUUID index)
        {
            uuid = index;
            entered = DateTime.Now;
            lastUsed = entered;
        }

        public CacheItemBase(LLUUID index, DateTime ttl)
        {
            uuid = index;
            entered = DateTime.Now;
            lastUsed = entered;
            expires = ttl;
        }

        public virtual bool Equals(CacheItemBase item)
        {
            return uuid == item.uuid;
        }

        public virtual int CompareTo(CacheItemBase item)
        {
            return uuid.CompareTo(item.uuid);
        }

        public virtual bool IsLocked()
        {
            return false;
        }
    }

    // Simple in-memory storage. Boxes the object and stores it in a variable
    //
    public class MemoryCacheItem : CacheItemBase
    {
        private Object m_Data;

        public MemoryCacheItem(LLUUID index) :
            base(index)
        {
        }

        public MemoryCacheItem(LLUUID index, DateTime ttl) :
            base(index, ttl)
        {
        }

        public MemoryCacheItem(LLUUID index, Object data) :
            base(index)
        {
            Store(data);
        }

        public MemoryCacheItem(LLUUID index, DateTime ttl, Object data) :
            base(index, ttl)
        {
            Store(data);
        }

        public override Object Retrieve()
        {
            return m_Data;
        }

        public override void Store(Object data)
        {
            m_Data = data;
        }
    }

    // Simple persistent file storage
    //
    public class FileCacheItem : CacheItemBase
    {
        public FileCacheItem(LLUUID index) :
            base(index)
        {
        }

        public FileCacheItem(LLUUID index, DateTime ttl) :
            base(index, ttl)
        {
        }

        public FileCacheItem(LLUUID index, Object data) :
            base(index)
        {
            Store(data);
        }

        public FileCacheItem(LLUUID index, DateTime ttl, Object data) :
            base(index, ttl)
        {
            Store(data);
        }

        public override Object Retrieve()
        {
            //TODO: Add file access code
            return null;
        }

        public override void Store(Object data)
        {
            //TODO: Add file access code
        }
    }

    // The main cache class. This is the class you instantiate to create
    // a cache
    //
    public class Cache
    {
        private List<CacheItemBase> m_Index = new List<CacheItemBase>();
		private Dictionary<LLUUID, CacheItemBase> m_Lookup =
				new Dictionary<LLUUID, CacheItemBase>();

        private CacheStrategy m_Strategy;
        private CacheMedium m_Medium;
        private CacheFlags m_Flags = 0;
        private int m_Size = 1024;
        private TimeSpan m_DefaultTTL = new TimeSpan(0);
        public ExpireDelegate OnExpire;

        // Comparison interfaces
        //
        private class SortLRU : IComparer<CacheItemBase>
        {
            public int Compare(CacheItemBase a, CacheItemBase b)
            {
                if (a == null && b == null)
                    return 0;
                if (a == null)
                    return -1;
                if (b == null)
                    return 1;

                return(a.lastUsed.CompareTo(b.lastUsed));
            }
        }

        // Convenience constructors
        //
        public Cache()
        {
            m_Strategy = CacheStrategy.Balanced;
            m_Medium = CacheMedium.Memory;
            m_Flags = 0;
        }

        public Cache(CacheMedium medium) :
            this(medium, CacheStrategy.Balanced)
        {
        }

        public Cache(CacheMedium medium, CacheFlags flags) :
            this(medium, CacheStrategy.Balanced, flags)
        {
        }

        public Cache(CacheMedium medium, CacheStrategy strategy) :
            this(medium, strategy, 0)
        {
        }

        public Cache(CacheStrategy strategy, CacheFlags flags) :
            this(CacheMedium.Memory, strategy, flags)
        {
        }

        public Cache(CacheFlags flags) :
            this(CacheMedium.Memory, CacheStrategy.Balanced, flags)
        {
        }

        public Cache(CacheMedium medium, CacheStrategy strategy,
                CacheFlags flags)
        {
            m_Strategy = strategy;
            m_Medium = medium;
            m_Flags = flags;
        }

        // Count of the items currently in cache
        //
        public int Count
        {
            get { lock (m_Index) { return m_Index.Count; } }
        }

        // Maximum number of items this cache will hold
        //
        public int Size
        {
            get { return m_Size; }
            set { SetSize(value); }
        }

        private void SetSize(int newSize)
        {
            lock (m_Index)
            {
                if (Count <= Size)
                    return;

                m_Index.Sort(new SortLRU());
                m_Index.Reverse();

                m_Index.RemoveRange(newSize, Count - newSize);
                m_Size = newSize;

				m_Lookup.Clear();

				foreach (CacheItemBase item in m_Index)
					m_Lookup[item.uuid] = item;
            }
        }

        public TimeSpan DefaultTTL
        {
            get { return m_DefaultTTL; }
            set { m_DefaultTTL = value; }
        }

        // Get an item from cache. Return the raw item, not it's data
        //
        protected virtual CacheItemBase GetItem(LLUUID index)
        {
            CacheItemBase item = null;

            lock (m_Index)
            {
				if(m_Lookup.ContainsKey(index))
					item = m_Lookup[index];
            }

            if (item == null)
            {
                Expire(true);
                return null;
            }

            item.hits++;
            item.lastUsed = DateTime.Now;

            Expire(true);

            return item;
        }

        // Get an item from cache. Do not try to fetch from source if not
        // present. Just return null
        //
        public virtual Object Get(LLUUID index)
        {
            CacheItemBase item = GetItem(index);

            if (item == null)
                return null;

            return item.Retrieve();
        }

        // Fetch an object from backing store if not cached, serve from
        // cache if it is.
        //
        public virtual Object Get(LLUUID index, FetchDelegate fetch)
        {
            Object item = Get(index);
            if (item != null)
                return item;

            Object data = fetch(index);
            if (data == null)
            {
                if ((m_Flags & CacheFlags.CacheMissing) != 0)
                {
                    lock (m_Index)
                    {
                        CacheItemBase missing = new CacheItemBase(index);
                        if (!m_Index.Contains(missing))
						{
                            m_Index.Add(missing);
							m_Lookup[index] = missing;
						}
                    }
                }
                return null;
            }

            Store(index, data);

            return data;
        }

		// Find an object in cache by delegate.
		//
		public Object Find(Predicate<Opensim.Framework.CacheItemBase> d)
		{
			CacheItemBase item = m_Index.Find(d);

			if(item == null)
				return null;

			return item.Retrieve();
		}

        public virtual void Store(LLUUID index, Object data)
        {
            Type container;

            switch (m_Medium)
            {
            case CacheMedium.Memory:
                container = typeof(MemoryCacheItem);
                break;
            case CacheMedium.File:
                return;
            default:
                return;
            }
            
            Store(index, data, container);
        }

        public virtual void Store(LLUUID index, Object data, Type container)
        {
            Store(index, data, container, new Object[] { index });
        }

        public virtual void Store(LLUUID index, Object data, Type container,
                Object[] parameters)
        {
            Expire(false);

            CacheItemBase item;

            lock (m_Index)
            {
                if (m_Index.Contains(new CacheItemBase(index)))
                {
                    if ((m_Flags & CacheFlags.AllowUpdate) != 0)
                    {
                        item = GetItem(index);
                        
                        item.hits++;
                        item.lastUsed = DateTime.Now;
                        if (m_DefaultTTL.Ticks != 0)
                            item.expires = DateTime.Now + m_DefaultTTL;

                        item.Store(data);
                    }
                    return;
                }

                item = (CacheItemBase)Activator.CreateInstance(container,
                        parameters);

                if (m_DefaultTTL.Ticks != 0)
                    item.expires = DateTime.Now + m_DefaultTTL;

                m_Index.Add(item);
				m_Lookup[index] = item;
            }
            item.Store(data);
        }

        protected virtual void Expire(bool getting)
        {
            if (getting && (m_Strategy == CacheStrategy.Aggressive))
                return;

            if (m_DefaultTTL.Ticks != 0)
            {
                DateTime now= System.DateTime.Now;

                foreach (CacheItemBase item in new List<CacheItemBase>(m_Index))
                {
                    if (item.expires.Ticks == 0 ||
                            item.expires <= now)
					{
                        m_Index.Remove(item);
						m_Lookup.Remove(item.uuid);
					}
                }
            }

            switch (m_Strategy)
            {
            case CacheStrategy.Aggressive:
                if (Count < Size)
                    return;

                lock (m_Index)
                {
                    m_Index.Sort(new SortLRU());
                    m_Index.Reverse();

                    int target = (int)((float)Size * 0.9);
                    if (target == Count) // Cover ridiculous cache sizes
                        return;

                    ExpireDelegate doExpire = OnExpire;

                if (doExpire != null)
                    {
                        List<CacheItemBase> candidates =
                                m_Index.GetRange(target, Count - target);

                        foreach (CacheItemBase i in candidates)
                        {
                            if (doExpire(i.uuid))
							{
                                m_Index.Remove(i);
								m_Lookup.Remove(i.uuid);
							}
                        }
                    }
                    else
                    {
                        m_Index.RemoveRange(target, Count - target);

						m_Lookup.Clear();

						foreach (CacheItemBase item in m_Index)
							m_Lookup[item.uuid] = item;
                    }
                }
                break;
            default:
                break;
            }
        }
    }
}
