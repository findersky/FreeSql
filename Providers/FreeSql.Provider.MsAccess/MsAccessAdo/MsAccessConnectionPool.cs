﻿using FreeSql.Internal.ObjectPool;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.OleDb;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FreeSql.MsAccess
{

    class MsAccessConnectionPool : ObjectPool<DbConnection>
    {

        internal Action availableHandler;
        internal Action unavailableHandler;

        public MsAccessConnectionPool(string name, string connectionString, Action availableHandler, Action unavailableHandler) : base(null)
        {
            this.availableHandler = availableHandler;
            this.unavailableHandler = unavailableHandler;
            var policy = new AccessConnectionPoolPolicy
            {
                _pool = this,
                Name = name
            };
            this.Policy = policy;
            policy.ConnectionString = connectionString;
        }

        public void Return(Object<DbConnection> obj, Exception exception, bool isRecreate = false)
        {
            base.Return(obj, isRecreate);
        }
    }

    class AccessConnectionPoolPolicy : IPolicy<DbConnection>
    {

        internal MsAccessConnectionPool _pool;
        public string Name { get; set; } = $"Microsoft Access OleDbConnection {CoreErrorStrings.S_ObjectPool}";
        public int PoolSize { get; set; } = 100;
        public TimeSpan SyncGetTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.Zero;
        public int AsyncGetCapacity { get; set; } = 10000;
        public bool IsThrowGetTimeoutException { get; set; } = true;
        public bool IsAutoDisposeWithSystem { get; set; } = true;
        public int CheckAvailableInterval { get; set; } = 2;
        public int Weight { get; set; } = 1;

        private string _connectionString;
        public string ConnectionString
        {
            get => _connectionString;
            set
            {
                _connectionString = value ?? "";

                var minPoolSize = 1;
                var pattern = @"Min\s*pool\s*size\s*=\s*(\d+)";
                var m = Regex.Match(_connectionString, pattern, RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    minPoolSize = int.Parse(m.Groups[1].Value);
                    _connectionString = Regex.Replace(_connectionString, pattern, "", RegexOptions.IgnoreCase);
                }

                pattern = @"Max\s*pool\s*size\s*=\s*(\d+)";
                m = Regex.Match(_connectionString, pattern, RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    if (int.TryParse(m.Groups[1].Value, out var poolsize) && poolsize > 0) 
                        PoolSize = poolsize;
                    _connectionString = Regex.Replace(_connectionString, pattern, "", RegexOptions.IgnoreCase);
                }

                pattern = @"Connection\s*LifeTime\s*=\s*(\d+)";
                m = Regex.Match(_connectionString, pattern, RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    IdleTimeout = TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value));
                    _connectionString = Regex.Replace(_connectionString, pattern, "", RegexOptions.IgnoreCase);
                }

                FreeSql.Internal.CommonUtils.PrevReheatConnectionPool(_pool, minPoolSize);
            }
        }

        public bool OnCheckAvailable(Object<DbConnection> obj)
        {
            if (obj.Value == null) return false;
            if (obj.Value.State == ConnectionState.Closed) obj.Value.Open();
            return obj.Value.Ping(true);
        }

        public DbConnection OnCreate()
        {
            var conn = new OleDbConnection(_connectionString);
            return conn;
        }

        public void OnDestroy(DbConnection obj)
        {
            if (obj.State != ConnectionState.Closed) obj.Close();
            obj.Dispose();
        }

        public void OnGet(Object<DbConnection> obj)
        {

            if (_pool.IsAvailable)
            {

                if (obj.Value == null)
                    throw new Exception(CoreErrorStrings.S_ConnectionStringError_Check(this.Name));

                if (obj.Value.State != ConnectionState.Open)
                    obj.Value.Open();
            }
        }

#if net40
#else
        async public Task OnGetAsync(Object<DbConnection> obj)
        {

            if (_pool.IsAvailable)
            {

                if (obj.Value == null)
                    throw new Exception(CoreErrorStrings.S_ConnectionStringError_Check(this.Name));

                if (obj.Value.State != ConnectionState.Open)
                    await obj.Value.OpenAsync();
            }
        }
#endif

        public void OnGetTimeout()
        {

        }

        public void OnReturn(Object<DbConnection> obj)
        {
            if (obj?.Value != null && obj.Value.State != ConnectionState.Closed) try { obj.Value.Close(); } catch { }
        }

        public void OnAvailable()
        {
            _pool.availableHandler?.Invoke();
        }

        public void OnUnavailable()
        {
            _pool.unavailableHandler?.Invoke();
        }
    }

    static class DbConnectionExtensions
    {

        static DbCommand PingCommand(DbConnection conn)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 5;
            cmd.CommandText = "select 1";
            return cmd;
        }
        public static bool Ping(this DbConnection that, bool isThrow = false)
        {
            try
            {
                PingCommand(that).ExecuteNonQuery();
                return true;
            }
            catch
            {
                if (that.State != ConnectionState.Closed) try { that.Close(); } catch { }
                if (isThrow) throw;
                return false;
            }
        }

#if net40
#else
        async public static Task<bool> PingAsync(this DbConnection that, bool isThrow = false)
        {
            try
            {
                await PingCommand(that).ExecuteNonQueryAsync();
                return true;
            }
            catch
            {
                if (that.State != ConnectionState.Closed) try { that.Close(); } catch { }
                if (isThrow) throw;
                return false;
            }
        }
#endif
    }
}
