﻿using System;
using System.Collections.Generic;
using System.Linq;
using InfluxDB.Collector.Pipeline.Common;

namespace InfluxDB.Collector.Pipeline.Aggregate
{
    class AggregatePointEmitter : IntervalEmitterBase
    {
        readonly bool _sumIncrements;
        readonly Func<IEnumerable<long>, double> _timesAggregation;
        readonly IPointEmitter _parent;

        public AggregatePointEmitter(TimeSpan timeSpan, bool sumIncrements, Func<IEnumerable<long>, double> timesAggregation, IPointEmitter parent)
            : base(timeSpan)
        {
            _sumIncrements = sumIncrements;
            _timesAggregation = timesAggregation;
            _parent = parent;
        }

        protected override void HandleBatch(IReadOnlyCollection<PointData> batch)
        {
            var grouped = batch.GroupBy(x => new GroupingKey(
                x.UtcTimestamp.HasValue ? x.UtcTimestamp.Value.Ticks / _interval.Ticks : 0,
                DetermineKind(x),
                x.Measurement,
                x.Tags
            ));

            var aggregated = grouped.SelectMany(Aggregate).ToArray();

            _parent.Emit(aggregated);
        }

        IEnumerable<PointData> Aggregate(IGrouping<GroupingKey, PointData> group)
        {
            GroupingKey key = group.Key;
            MeasurementKind kind = key.Kind;

            if (kind == MeasurementKind.Increment && _sumIncrements)
            {
                long sum = group.Sum(x => (long) x.Fields["count"]);
                return new[]
                {
                    new PointData(
                        key.Measurement,
                        new Dictionary<string, object> { { "count", sum } },
                        key.Tags,
                        AverageTime(key))
                };
            }

            if (kind == MeasurementKind.Time && _timesAggregation != null)
            {
                long ticks = (long) _timesAggregation(group.Select(x => ((TimeSpan) x.Fields["value"]).Ticks));
                return new[]
                {
                    new PointData(
                        key.Measurement,
                        new Dictionary<string, object> { { "value", new TimeSpan(ticks) } },
                        key.Tags,
                        AverageTime(key))
                };
            }

            return group;
        }

        private DateTime AverageTime(GroupingKey key)
        {
            return new DateTime(key.Bucket * _interval.Ticks + _interval.Ticks / 2, DateTimeKind.Utc);
        }

        static MeasurementKind DetermineKind(PointData x)
        {
            if (x.Fields.Count != 1) return MeasurementKind.Other;

            if (x.Fields.TryGetValue("count", out var count) && count is long)
            {
                return MeasurementKind.Increment;
            }
            else if (x.Fields.TryGetValue("value", out var value) && value is TimeSpan)
            {
                return MeasurementKind.Time;
            }
            else
            {
                return MeasurementKind.Other;
            }
        }
    }
}