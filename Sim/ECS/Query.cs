namespace MTile;

// Alloc-free query enumerators. Each is a ref struct that foreach can drive directly
// (GetEnumerator returns itself). Multi-component queries iterate the first store's
// packed array and gate each candidate on Has in the remaining stores; Current
// exposes the EntityId plus ref access to each component so systems can mutate in
// place. See World.Query<...>.

public ref struct Query<T1> where T1 : struct
{
    private readonly ComponentStore<T1> _s1;
    private int _i;

    internal Query(ComponentStore<T1> s1) { _s1 = s1; _i = -1; }

    public Query<T1> GetEnumerator() => this;
    public bool MoveNext() => ++_i < _s1.Count;
    public Row Current => new(_s1, _i);

    public ref struct Row
    {
        private readonly ComponentStore<T1> _s1;
        private readonly int _i;
        internal Row(ComponentStore<T1> s1, int i) { _s1 = s1; _i = i; }
        public EntityId Entity => _s1.EntityAt(_i);
        public ref T1 Component1 => ref _s1.DataAt(_i);
    }
}

public ref struct Query<T1, T2> where T1 : struct where T2 : struct
{
    private readonly ComponentStore<T1> _s1;
    private readonly ComponentStore<T2> _s2;
    private int _i;

    internal Query(ComponentStore<T1> s1, ComponentStore<T2> s2) { _s1 = s1; _s2 = s2; _i = -1; }

    public Query<T1, T2> GetEnumerator() => this;

    public bool MoveNext()
    {
        while (++_i < _s1.Count)
            if (_s2.HasEntity(_s1.EntityAt(_i).Index)) return true;
        return false;
    }

    public Row Current => new(_s1, _s2, _i);

    public ref struct Row
    {
        private readonly ComponentStore<T1> _s1;
        private readonly ComponentStore<T2> _s2;
        private readonly int _i;
        internal Row(ComponentStore<T1> s1, ComponentStore<T2> s2, int i) { _s1 = s1; _s2 = s2; _i = i; }
        public EntityId Entity => _s1.EntityAt(_i);
        public ref T1 Component1 => ref _s1.DataAt(_i);
        public ref T2 Component2 => ref _s2.Get(_s1.EntityAt(_i).Index);
    }
}

public ref struct Query<T1, T2, T3> where T1 : struct where T2 : struct where T3 : struct
{
    private readonly ComponentStore<T1> _s1;
    private readonly ComponentStore<T2> _s2;
    private readonly ComponentStore<T3> _s3;
    private int _i;

    internal Query(ComponentStore<T1> s1, ComponentStore<T2> s2, ComponentStore<T3> s3)
    { _s1 = s1; _s2 = s2; _s3 = s3; _i = -1; }

    public Query<T1, T2, T3> GetEnumerator() => this;

    public bool MoveNext()
    {
        while (++_i < _s1.Count)
        {
            int idx = _s1.EntityAt(_i).Index;
            if (_s2.HasEntity(idx) && _s3.HasEntity(idx)) return true;
        }
        return false;
    }

    public Row Current => new(_s1, _s2, _s3, _i);

    public ref struct Row
    {
        private readonly ComponentStore<T1> _s1;
        private readonly ComponentStore<T2> _s2;
        private readonly ComponentStore<T3> _s3;
        private readonly int _i;
        internal Row(ComponentStore<T1> s1, ComponentStore<T2> s2, ComponentStore<T3> s3, int i)
        { _s1 = s1; _s2 = s2; _s3 = s3; _i = i; }
        public EntityId Entity => _s1.EntityAt(_i);
        public ref T1 Component1 => ref _s1.DataAt(_i);
        public ref T2 Component2 => ref _s2.Get(_s1.EntityAt(_i).Index);
        public ref T3 Component3 => ref _s3.Get(_s1.EntityAt(_i).Index);
    }
}
