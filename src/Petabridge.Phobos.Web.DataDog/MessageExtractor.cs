// -----------------------------------------------------------------------
// <copyright file="Startup.cs" company="Petabridge, LLC">
//      Copyright (C) 2015 - 2023 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System;
using Akka.Cluster.Sharding;

namespace Petabridge.Phobos.Web
{
    public sealed class TraceIdentifierMessageExtractor : IMessageExtractor
    {
        private readonly int _maxNumberOfShards;
        private readonly int _maxNumberOfEntities;
        
        public TraceIdentifierMessageExtractor(int maxNumberOfShards, int maxNumberOfEntities)
        {
            _maxNumberOfShards = maxNumberOfShards;
            _maxNumberOfEntities = maxNumberOfEntities;
        }

        public string EntityId(object message)
            => $"entity-{DecodeTraceIdentifier(message) % _maxNumberOfEntities}";

        public object EntityMessage(object message)
            => message;

        public string ShardId(object message)
            => $"shard-{DecodeTraceIdentifier(message) % _maxNumberOfShards}";

        private static long DecodeTraceIdentifier(object message)
        {
            if(!(message is string str))
                throw new System.NotImplementedException();

            var split = str.Split(':');
            if(split.Length != 2)
                throw new System.NotImplementedException();

            return Convert.ToInt64($"0x{split[1]}", 16);
        }
    }
}
