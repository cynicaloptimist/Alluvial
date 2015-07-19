using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Alluvial
{
    /// <summary>
    /// An persistent query over a stream of data, which updates one or more stream aggregators.
    /// </summary>
    /// <typeparam name="TData">The type of the data that the catchup pushes to the aggregators.</typeparam>
    /// <typeparam name="TCursor">The type of the cursor.</typeparam>
    internal abstract class StreamCatchupBase<TData, TCursor> : IStreamCatchup<TData, TCursor>
    {
        protected int? batchCount;

        protected readonly ConcurrentDictionary<Type, IAggregatorSubscription> aggregatorSubscriptions = new ConcurrentDictionary<Type, IAggregatorSubscription>();

        public IDisposable SubscribeAggregator<TProjection>(
            IStreamAggregator<TProjection, TData> aggregator,
            FetchAndSaveProjection<TProjection> fetchAndSaveProjection,
            HandleAggregatorError<TProjection> onError)
        {
            var added = aggregatorSubscriptions.TryAdd(typeof (TProjection),
                                                       new AggregatorSubscription<TProjection, TData>(aggregator,
                                                                                                      fetchAndSaveProjection,
                                                                                                      onError));

            if (!added)
            {
                throw new InvalidOperationException(string.Format("Aggregator for projection of type {0} is already subscribed.", typeof (TProjection)));
            }

            return Disposable.Create(() =>
            {
                IAggregatorSubscription _;
                aggregatorSubscriptions.TryRemove(typeof (TProjection), out _);
            });
        }

        /// <summary>
        /// Consumes a single batch from the source stream and updates the subscribed aggregators.
        /// </summary>
        /// <returns>
        /// The updated cursor position after the batch is consumed.
        /// </returns>
        public abstract Task<ICursor<TCursor>> RunSingleBatch();

        private object tcs;

        protected async Task<ICursor<T>> RunSingleBatch<T>(IStream<TData, T> stream)
        {
            TaskCompletionSource<AggregationBatch<T>> tcs = null;

            tcs = new TaskCompletionSource<AggregationBatch<T>>();

            var exchange = Interlocked.CompareExchange<object>(ref this.tcs, tcs, null);

            if (exchange != null)
            {
                Debug.WriteLine("[Catchup] RunSingleBatch returning early");
                var batch = await ((TaskCompletionSource<AggregationBatch<T>>) this.tcs).Task;
                return batch.Cursor;
            }

            ICursor<T> upstreamCursor = null;

            var projections = new ConcurrentBag<object>();

            Action runQuery = async () =>
            {
                var cursor = projections.OfType<ICursor<T>>().Minimum();
                upstreamCursor = cursor;
                var query = stream.CreateQuery(cursor, batchCount);

                try
                {
                    var batch = await query.NextBatch();

                    tcs.SetResult(new AggregationBatch<T>
                    {
                        Cursor = query.Cursor,
                        Batch = batch
                    });
                }
                catch (Exception exception)
                {
                    tcs.SetException(exception);
                }
            };

            Func<object, Task<AggregationBatch<T>>> awaitData = c =>
            {
                projections.Add(c);

                if (projections.Count >= aggregatorSubscriptions.Count)
                {
                    runQuery();
                }

                return tcs.Task;
            };

            var aggregationTasks = aggregatorSubscriptions
                .Values
                .Select(v => Aggregate(stream, (dynamic) v, awaitData) as Task);

            await Task.WhenAll(aggregationTasks);

            this.tcs = null;

            return upstreamCursor;
        }

        private static Task Aggregate<TProjection, TCursor>(
            IStream<TData, TCursor> stream,
            AggregatorSubscription<TProjection, TData> subscription,
            Func<object, Task<AggregationBatch<TCursor>>> getData)
        {
            return subscription.FetchAndSaveProjection(
                stream.Id,
                async projection =>
                {
                    try
                    {
                        var aggregationBatch = await getData(projection);

                        var data = aggregationBatch.Batch;

                        projection = await subscription.Aggregator.Aggregate(projection, data);

                        var cursor = projection as ICursor<TCursor>;
                        if (cursor != null)
                        {
                            cursor.AdvanceTo(aggregationBatch.Cursor.Position);
                        }
                    }
                    catch (Exception exception)
                    {
                        var error = subscription.OnError.CheckErrorHandler(exception, projection);

                        if (!error.ShouldContinue)
                        {
                            throw;
                        }
                    }

                    return projection;
                });
        }

        protected struct AggregationBatch<TCursor>
        {
            public ICursor<TCursor> Cursor;
            public IStreamBatch<TData> Batch;
        }
    }
}