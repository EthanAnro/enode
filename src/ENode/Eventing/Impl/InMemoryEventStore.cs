﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ECommon.IO;
using ECommon.Logging;

namespace ENode.Eventing.Impl
{
    public class InMemoryEventStore : IEventStore
    {
        private const int Editing = 1;
        private const int UnEditing = 0;
        private readonly object _lockObj = new object();
        private readonly ConcurrentDictionary<string, AggregateInfo> _aggregateInfoDict;
        private readonly ILogger _logger;

        public InMemoryEventStore(ILoggerFactory loggerFactory)
        {
            _aggregateInfoDict = new ConcurrentDictionary<string, AggregateInfo>();
            _logger = loggerFactory.Create(GetType().FullName);
        }

        public IEnumerable<DomainEventStream> QueryAggregateEvents(string aggregateRootId, string aggregateRootTypeName, int minVersion, int maxVersion)
        {
            var eventStreams = new List<DomainEventStream>();

            AggregateInfo aggregateInfo;
            if (!_aggregateInfoDict.TryGetValue(aggregateRootId, out aggregateInfo))
            {
                return eventStreams;
            }

            var min = minVersion > 1 ? minVersion : 1;
            var max = maxVersion < aggregateInfo.CurrentVersion ? maxVersion : aggregateInfo.CurrentVersion;

            return aggregateInfo.EventDict.Where(x => x.Key >= min && x.Key <= max).Select(x => x.Value).ToList();
        }
        public Task<AsyncTaskResult<EventAppendResult>> BatchAppendAsync(IEnumerable<DomainEventStream> eventStreams)
        {
            var eventStreamDict = new Dictionary<string, IList<DomainEventStream>>();
            var aggregateRootIdList = eventStreams.Select(x => x.AggregateRootId).Distinct().ToList();
            foreach (var aggregateRootId in aggregateRootIdList)
            {
                var eventStreamList = eventStreams.Where(x => x.AggregateRootId == aggregateRootId).ToList();
                if (eventStreamList.Count > 0)
                {
                    eventStreamDict.Add(aggregateRootId, eventStreamList);
                }
            }
            var eventAppendResult = new EventAppendResult();
            eventAppendResult.SuccessAggregateRootIdList = new List<string>();
            eventAppendResult.DuplicateCommandIdList = new List<string>();
            eventAppendResult.DuplicateEventAggregateRootIdList = new List<string>();
            foreach (var entry in eventStreamDict)
            {
                BatchAppend(entry.Key, entry.Value, eventAppendResult);
            }
            return Task.FromResult(new AsyncTaskResult<EventAppendResult>(AsyncTaskStatus.Success, eventAppendResult));
        }
        public Task<AsyncTaskResult<DomainEventStream>> FindAsync(string aggregateRootId, int version)
        {
            return Task.FromResult(new AsyncTaskResult<DomainEventStream>(AsyncTaskStatus.Success, null, Find(aggregateRootId, version)));
        }
        public Task<AsyncTaskResult<DomainEventStream>> FindAsync(string aggregateRootId, string commandId)
        {
            return Task.FromResult(new AsyncTaskResult<DomainEventStream>(AsyncTaskStatus.Success, null, Find(aggregateRootId, commandId)));
        }
        public Task<AsyncTaskResult<IEnumerable<DomainEventStream>>> QueryAggregateEventsAsync(string aggregateRootId, string aggregateRootTypeName, int minVersion, int maxVersion)
        {
            return Task.FromResult(new AsyncTaskResult<IEnumerable<DomainEventStream>>(AsyncTaskStatus.Success, null, QueryAggregateEvents(aggregateRootId, aggregateRootTypeName, minVersion, maxVersion)));
        }

        private void BatchAppend(string aggregateRootId, IList<DomainEventStream> eventStreamList, EventAppendResult eventAppendResult)
        {
            lock (_lockObj)
            {
                var aggregateInfo = _aggregateInfoDict.GetOrAdd(aggregateRootId, x => new AggregateInfo());
                var firstEventStream = eventStreamList.First();
                var duplicateEventAggregateRootIdList = new List<string>();

                //检查提交过来的第一个事件的版本号是否是当前聚合根的当前版本号的下一个版本号
                if (firstEventStream.Version != aggregateInfo.CurrentVersion + 1)
                {
                    if (!eventAppendResult.DuplicateEventAggregateRootIdList.Contains(aggregateRootId))
                    {
                        eventAppendResult.DuplicateEventAggregateRootIdList.Add(aggregateRootId);
                    }
                    return;
                }

                //检查重复处理的命令ID
                foreach (DomainEventStream eventStream in eventStreamList)
                {
                    if (aggregateInfo.CommandDict.ContainsKey(eventStream.CommandId))
                    {
                        if (!eventAppendResult.DuplicateCommandIdList.Contains(eventStream.CommandId))
                        {
                            eventAppendResult.DuplicateCommandIdList.Add(eventStream.CommandId);
                        }
                    }
                }
                if (eventAppendResult.DuplicateCommandIdList.Count > 0)
                {
                    return;
                }

                //检查提交过来的事件本身是否满足版本号的递增关系
                for (var i = 0; i < eventStreamList.Count - 1; i++)
                {
                    if (eventStreamList[i + 1].Version != eventStreamList[i].Version + 1)
                    {
                        if (!eventAppendResult.DuplicateEventAggregateRootIdList.Contains(aggregateRootId))
                        {
                            eventAppendResult.DuplicateEventAggregateRootIdList.Add(aggregateRootId);
                        }
                        return;
                    }
                }

                foreach (DomainEventStream eventStream in eventStreamList)
                {
                    aggregateInfo.EventDict[eventStream.Version] = eventStream;
                    aggregateInfo.CommandDict[eventStream.CommandId] = eventStream;
                    aggregateInfo.CurrentVersion = eventStream.Version;
                }

                if (!eventAppendResult.SuccessAggregateRootIdList.Contains(aggregateRootId))
                {
                    eventAppendResult.SuccessAggregateRootIdList.Add(aggregateRootId);
                }
            }
        }
        private DomainEventStream Find(string aggregateRootId, int version)
        {
            AggregateInfo aggregateInfo;
            if (!_aggregateInfoDict.TryGetValue(aggregateRootId, out aggregateInfo))
            {
                return null;
            }

            DomainEventStream eventStream;
            return aggregateInfo.EventDict.TryGetValue(version, out eventStream) ? eventStream : null;
        }
        private DomainEventStream Find(string aggregateRootId, string commandId)
        {
            AggregateInfo aggregateInfo;
            if (!_aggregateInfoDict.TryGetValue(aggregateRootId, out aggregateInfo))
            {
                return null;
            }

            DomainEventStream eventStream;
            return aggregateInfo.CommandDict.TryGetValue(commandId, out eventStream) ? eventStream : null;
        }
        class AggregateInfo
        {
            public long CurrentVersion;
            public ConcurrentDictionary<int, DomainEventStream> EventDict = new ConcurrentDictionary<int, DomainEventStream>();
            public ConcurrentDictionary<string, DomainEventStream> CommandDict = new ConcurrentDictionary<string, DomainEventStream>();
        }
    }
}
