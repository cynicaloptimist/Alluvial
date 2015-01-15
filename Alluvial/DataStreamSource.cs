using System;

namespace Alluvial
{
    public static class DataStreamSource
    {
        public static IStreamSource<TKey, TData> Create<TKey, TData>(Func<TKey, IStream<TData>> open)
        {
            return new AnonymousStreamSource<TKey, TData>(open);
        }
    }
}