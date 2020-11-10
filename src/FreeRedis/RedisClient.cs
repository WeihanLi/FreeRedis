﻿using FreeRedis.Internal;
using FreeRedis.Internal.ObjectPool;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections;
using System.Runtime.InteropServices;
using System.Reflection;

namespace FreeRedis
{
    public partial class RedisClient : IDisposable
    {
        internal BaseAdapter Adapter { get; }
        internal string Prefix { get; }
        public event EventHandler<NoticeEventArgs> Notice;
        public List<Func<IInterceptor>> Interceptors { get; } = new List<Func<IInterceptor>>();

        protected RedisClient(BaseAdapter adapter)
        {
            Adapter = adapter;
        }

        /// <summary>
        /// Pooling RedisClient
        /// </summary>
        public RedisClient(ConnectionStringBuilder connectionString, params ConnectionStringBuilder[] slaveConnectionStrings)
        {
            Adapter = new PoolingAdapter(this, connectionString, slaveConnectionStrings);
            Prefix = connectionString.Prefix;
        }

        /// <summary>
        /// Cluster RedisClient
        /// </summary>
        public RedisClient(ConnectionStringBuilder[] clusterConnectionStrings)
        {
            Adapter = new ClusterAdapter(this, clusterConnectionStrings);
            Prefix = clusterConnectionStrings[0].Prefix;
        }

        /// <summary>
        /// Sentinel RedisClient
        /// </summary>
        public RedisClient(ConnectionStringBuilder sentinelConnectionString, string[] sentinels, bool rw_splitting)
        {
            Adapter = new SentinelAdapter(this, sentinelConnectionString, sentinels, rw_splitting);
            Prefix = sentinelConnectionString.Prefix;
        }

        /// <summary>
        /// Single inside RedisClient
        /// </summary>
        protected internal RedisClient(RedisClient topOwner, string host, bool ssl, TimeSpan connectTimeout, TimeSpan receiveTimeout, TimeSpan sendTimeout, Action<RedisClient> connected)
        {
            Adapter = new SingleInsideAdapter(topOwner ?? this, this, host, ssl, connectTimeout, receiveTimeout, sendTimeout, connected);
            Prefix = topOwner.Prefix;
        }

        ~RedisClient() => this.Dispose();
        int _disposeCounter;
        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposeCounter) != 1) return;
            Adapter.Dispose();
        }

        protected void CheckUseTypeOrThrow(params UseType[] useTypes)
        {
            if (useTypes?.Contains(Adapter.UseType) == true) return;
            throw new RedisClientException($"Method cannot be used in {Adapter.UseType} mode.");
        }

        public object Call(CommandPacket cmd) => Adapter.AdapterCall(cmd, rt => rt.ThrowOrValue());
        protected TValue Call<TValue>(CommandPacket cmd, Func<RedisResult, TValue> parse) => Adapter.AdapterCall(cmd, parse);

        internal T LogCall<T>(CommandPacket cmd, Func<T> func)
        {
            cmd.Prefix(Prefix);
            var isnotice = this.Notice != null;
            var isaop = this.Interceptors.Any();
            if (isnotice == false && isaop == false) return func();
            Exception exception = null;
            Stopwatch sw = default;
            if (isnotice)
            {
                sw = new Stopwatch();
                sw.Start();
            }

            T ret = default(T);
            var isaopval = false;
            IInterceptor[] aops = null;
            Stopwatch[] aopsws = null;
            if (isaop) {
                aops = new IInterceptor[this.Interceptors.Count];
                aopsws = new Stopwatch[aops.Length];
                for (var idx = 0; idx < aops.Length; idx++)
                {
                    aopsws[idx] = new Stopwatch();
                    aopsws[idx].Start();
                    aops[idx] = this.Interceptors[idx]?.Invoke();
                    var args = new InterceptorBeforeEventArgs(this, cmd);
                    aops[idx].Before(args);
                    if (args.ValueIsChanged && args.Value is T argsValue)
                    {
                        isaopval = true;
                        ret = argsValue;
                    }
                }
            }
            try
            {
                if (isaopval == false) ret = func();
                return ret;
            }
            catch (Exception ex)
            {
                exception = ex;
                throw ex;
            }
            finally
            {
                if (isaop) {
                    for (var idx = 0; idx < aops.Length; idx++)
                    {
                        aopsws[idx].Stop();
                        var args = new InterceptorAfterEventArgs(this, cmd, ret, exception, aopsws[idx].ElapsedMilliseconds);
                        aops[idx].After(args);
                    }
                }

                if (isnotice)
                {
                    sw.Stop();
                    LogCallFinally(cmd, ret, sw, exception);
                }
            }
        }
        void LogCallFinally<T>(CommandPacket cmd, T result, Stopwatch sw, Exception exception)
        {
            string log;
            if (exception != null) log = $"{exception.Message}";
            else if (result is Array array)
            {
                var sb = new StringBuilder().Append("[");
                var itemindex = 0;
                foreach (var item in array)
                {
                    if (itemindex++ > 0) sb.Append(", ");
                    sb.Append(item.ToInvariantCultureToString());
                }
                log = sb.Append("]").ToString();
                sb.Clear();
            }
            else
                log = $"{result.ToInvariantCultureToString()}";
            this.OnNotice(new NoticeEventArgs(
                NoticeType.Call,
                exception,
                $"{(cmd.WriteHost ?? "Not connected")} ({sw.ElapsedMilliseconds}ms) > {cmd}\r\n{log}",
                result));
        }
        internal bool OnNotice(NoticeEventArgs e)
        {
            this.Notice?.Invoke(this, e);
            return this.Notice != null;
        }

        #region 序列化写入，反序列化
        public Func<object, string> Serialize;
        public Func<string, Type, object> Deserialize;

        internal object SerializeRedisValue(object value)
        {
            switch (value)
            {
                case null: return null;
                case string _:
                case byte[] _:
                case char _:
                    return value;
                case bool b: return b ? "1" : "0";
                case DateTime time: return time.ToString("yyyy-MM-ddTHH:mm:sszzzz", System.Globalization.DateTimeFormatInfo.InvariantInfo);
                case TimeSpan span: return span.Ticks;
                case DateTimeOffset _:
                case Guid _:
                    return value.ToString();
                default:
                    var type = value.GetType();
                    if (type.IsPrimitive && type.IsValueType) return value.ToString();
                    return Adapter.TopOwner.Serialize?.Invoke(value) ?? value.ConvertTo<string>();
            }
        }

        internal T DeserializeRedisValue<T>(byte[] value, Encoding encoding)
        {
            if (value == null) return default;

            var type = typeof(T);
            if (type == typeof(byte[])) return (T)Convert.ChangeType(value, type);
            if (type == typeof(string)) return (T)Convert.ChangeType(encoding.GetString(value), type);
            if (type == typeof(bool[])) return (T)Convert.ChangeType(value.Select(a => a == 49).ToArray(), type);

            var valueStr = encoding.GetString(value);
            if (string.IsNullOrEmpty(valueStr)) return default;

            var isNullable = type.IsValueType && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);
            if (isNullable) type = type.GetGenericArguments().First();

            if (type == typeof(bool)) return (T)(object)(valueStr == "1");
            if (type == typeof(char)) return valueStr.Length > 0 ? (T)(object)valueStr[0] : default;
            if (type == typeof(TimeSpan))
            {
                if (long.TryParse(valueStr, out var i64Result)) return (T)(object)new TimeSpan(i64Result);
                return default;
            }

            var parse = type.GetMethod("TryParse", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string), type.MakeByRefType() }, null);
            if (parse != null)
            {
                var parameters = new object[] { valueStr, null };
                var succeeded = (bool)parse.Invoke(null, parameters);
                if (succeeded) return (T)parameters[1];
                return default;
            }

            return Adapter.TopOwner.Deserialize != null ? (T)Adapter.TopOwner.Deserialize.Invoke(valueStr, typeof(T)) : valueStr.ConvertTo<T>();
        }
        #endregion
    }

    public class RedisClientException : Exception
    {
        public RedisClientException(string message) : base(message) { }
    }

    /// <summary>
    /// redis version >=6.2: Added the GT and LT options.
    /// </summary>
    public enum ZAddThan { gt, lt }
    public enum BitOpOperation { and, or, xor, not }
    public enum ClusterSetSlotType { importing, migrating, stable, node }
    public enum ClusterResetType { hard, soft }
    public enum ClusterFailOverType { force, takeover }
    public enum ClientUnBlockType { timeout, error }
    public enum ClientReplyType { on, off, skip }
    public enum ClientType { normal, master, slave, pubsub }
    public enum Collation { asc, desc }
    public enum Confirm { yes, no }
    public enum GeoUnit { m, km, mi, ft }
    public enum InsertDirection { before, after }
    public enum KeyType { none, @string, list, set, zset, hash, stream }
    public enum RoleType { Master, Slave, Sentinel }

    public class KeyValue<T>
    {
        public readonly string key;
        public readonly T value;
        public KeyValue(string key, T value) { this.key = key; this.value = value; }
    }
    public class ScanResult<T>
    {
        public readonly long cursor;
        public readonly T[] items;
        public readonly long length;
        public ScanResult(long cursor, T[] items) { this.cursor = cursor; this.items = items; this.length = items.LongLength; }
    }


    public class NoticeEventArgs : EventArgs
    {
        public NoticeType NoticeType { get; }
        public Exception Exception { get; }
        public string Log { get; }
        public object Tag { get; }

        public NoticeEventArgs(NoticeType noticeType, Exception exception, string log, object tag)
        {
            this.NoticeType = noticeType;
            this.Exception = exception;
            this.Log = log;
            this.Tag = tag;
        }
    }
    public enum NoticeType
    {
        Call, Info
    }
    public interface IInterceptor
    {
        void Before(InterceptorBeforeEventArgs args);
        void After(InterceptorAfterEventArgs args);
    }
    public class InterceptorBeforeEventArgs
    {
        public RedisClient Client { get; }
        public CommandPacket Command { get; }

        public InterceptorBeforeEventArgs(RedisClient cli, CommandPacket cmd)
        {
            this.Client = cli;
            this.Command = cmd;
        }

        public object Value
        {
            get => _value;
            set
            {
                _value = value;
                this.ValueIsChanged = true;
            }
        }
        private object _value;
        public bool ValueIsChanged { get; private set; }
    }
    public class InterceptorAfterEventArgs
    {
        public RedisClient Client { get; }
        public CommandPacket Command { get; }

        public object Value { get; }
        public Exception Exception { get; }
        public long ElapsedMilliseconds { get; }

        public InterceptorAfterEventArgs(RedisClient cli, CommandPacket cmd, object value, Exception exception, long elapsedMilliseconds)
        {
            this.Client = cli;
            this.Command = cmd;
            this.Value = value;
            this.Exception = exception;
            this.ElapsedMilliseconds = elapsedMilliseconds;
        }
    }
}
