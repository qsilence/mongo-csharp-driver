﻿/* Copyright 2010-2011 10gen Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver.Internal;

namespace MongoDB.Driver {
    /// <summary>
    /// Represents a MongoDB server (either a single instance or a replica set) and the settings used to access it. This class is thread-safe.
    /// </summary>
    public class MongoServer {
        #region private static fields
        private static object staticLock = new object();
        private static Dictionary<MongoServerSettings, MongoServer> servers = new Dictionary<MongoServerSettings, MongoServer>();
        private static int maxServerCount = 100;
        #endregion

        #region private fields
        private object serverLock = new object();
        private MongoServerSettings settings;
        private MongoServerState state = MongoServerState.Disconnected;
        private object stateLock = new object(); // synchronize state changes
        private int connectionAttempt;
        private List<MongoServerInstance> instances = new List<MongoServerInstance>();
        private MongoServerInstance primary;
        private string replicaSetName;
        private int loadBalancingInstanceIndex; // used to distribute reads across secondaries in round robin fashion
        private Dictionary<MongoDatabaseSettings, MongoDatabase> databases = new Dictionary<MongoDatabaseSettings, MongoDatabase>();
        private Dictionary<int, Request> requests = new Dictionary<int, Request>(); // tracks threads that have called RequestStart
        private IndexCache indexCache = new IndexCache();
        #endregion

        #region constructors
        /// <summary>
        /// Creates a new instance of MongoServer. Normally you will use one of the Create methods instead
        /// of the constructor to create instances of this class.
        /// </summary>
        /// <param name="settings">The settings for this instance of MongoServer.</param>
        public MongoServer(
            MongoServerSettings settings
        ) {
            this.settings = settings.Freeze();

            if (settings.ConnectionMode == ConnectionMode.ReplicaSet) {
                // initialize the set of server instances from the seed list (might change once we connect)
                foreach (var address in settings.Servers) {
                    var serverInstance = new MongoServerInstance(this, address);
                    AddInstance(serverInstance);
                }
            } else {
                // initialize the server instance to the first (or only) address provided
                var serverInstance = new MongoServerInstance(this, settings.Servers.First());
                AddInstance(serverInstance);
            }
        }
        #endregion

        #region factory methods
        /// <summary>
        /// Creates a new instance or returns an existing instance of MongoServer. Only one instance
        /// is created for each combination of server settings.
        /// </summary>
        /// <returns>
        /// A new or existing instance of MongoServer.
        /// </returns>
        public static MongoServer Create() {
            return Create("mongodb://localhost");
        }

        /// <summary>
        /// Creates a new instance or returns an existing instance of MongoServer. Only one instance
        /// is created for each combination of server settings.
        /// </summary>
        /// <param name="builder">Server settings in the form of a MongoConnectionStringBuilder.</param>
        /// <returns>
        /// A new or existing instance of MongoServer.
        /// </returns>
        public static MongoServer Create(
            MongoConnectionStringBuilder builder
        ) {
            return Create(builder.ToServerSettings());
        }

        /// <summary>
        /// Creates a new instance or returns an existing instance of MongoServer. Only one instance
        /// is created for each combination of server settings.
        /// </summary>
        /// <param name="settings">Server settings.</param>
        /// <returns>
        /// A new or existing instance of MongoServer.
        /// </returns>
        public static MongoServer Create(
            MongoServerSettings settings
        ) {
            lock (staticLock) {
                MongoServer server;
                if (!servers.TryGetValue(settings, out server)) {
                    if (servers.Count >= maxServerCount) {
                        var message = string.Format("MongoServer.Create has already created {0} servers which is the maximum number of servers allowed.", maxServerCount);
                        throw new MongoException(message);
                    }
                    server = new MongoServer(settings);
                    servers.Add(settings, server);
                }
                return server;
            }
        }

        /// <summary>
        /// Creates a new instance or returns an existing instance of MongoServer. Only one instance
        /// is created for each combination of server settings.
        /// </summary>
        /// <param name="url">Server settings in the form of a MongoUrl.</param>
        /// <returns>
        /// A new or existing instance of MongoServer.
        /// </returns>
        public static MongoServer Create(
            MongoUrl url
        ) {
            return Create(url.ToServerSettings());
        }

        /// <summary>
        /// Creates a new instance or returns an existing instance of MongoServer. Only one instance
        /// is created for each combination of server settings.
        /// </summary>
        /// <param name="connectionString">Server settings in the form of a connection string.</param>
        /// <returns>
        /// A new or existing instance of MongoServer.
        /// </returns>
        public static MongoServer Create(
            string connectionString
        ) {
            if (connectionString.StartsWith("mongodb://")) {
                var url = MongoUrl.Create(connectionString);
                return Create(url);
            } else {
                var builder = new MongoConnectionStringBuilder(connectionString);
                return Create(builder);
            }
        }

        /// <summary>
        /// Creates a new instance or returns an existing instance of MongoServer. Only one instance
        /// is created for each combination of server settings.
        /// </summary>
        /// <param name="uri">Server settings in the form of a Uri.</param>
        /// <returns>
        /// A new or existing instance of MongoServer.
        /// </returns>
        public static MongoServer Create(
            Uri uri
        ) {
            var url = MongoUrl.Create(uri.ToString());
            return Create(url);
        }

        /// <summary>
        /// Unregisters a server from the dictionary used by Create to remember which servers have already been created.
        /// </summary>
        /// <param name="server">The server to unregister.</param>
        public static void UnregisterServer(
            MongoServer server
        ) {
            try { server.Disconnect(); } catch { } // ignore exceptions
            lock (staticLock) {
                servers.Remove(server.settings);
            }
        }
        #endregion

        #region public static properties
        /// <summary>
        /// Gets or sets the maximum number of instances of MongoServer that will be allowed to be created.
        /// </summary>
        public static int MaxServerCount {
            get { return maxServerCount; }
            set { maxServerCount = value; }
        }

        /// <summary>
        /// Gets the number of instances of MongoServer that have been created.
        /// </summary>
        public static int ServerCount {
            get {
                lock (staticLock) {
                    return servers.Count;
                }
            }
        }
        #endregion

        #region public properties
        /// <summary>
        /// Gets the admin database for this server.
        /// </summary>
        public virtual MongoDatabase AdminDatabase {
            get { return GetDatabase("admin"); }
        }

        /// <summary>
        /// Gets the arbiter instances.
        /// </summary>
        public MongoServerInstance[] Arbiters {
            get {
                lock (stateLock) {
                    return instances.Where(i => i.IsArbiter).ToArray();
                }
            }
        }

        /// <summary>
        /// Gets the build info of the server.
        /// </summary>
        public MongoServerBuildInfo BuildInfo {
            get {
                lock (stateLock) {
                    MongoServerInstance instance;
                    if (settings.ConnectionMode == ConnectionMode.ReplicaSet) {
                        instance = Primary;
                        if (instance == null) {
                            throw new InvalidOperationException("Primary not found.");
                        }
                    } else {
                        instance = instances.First();
                    }
                    return instance.BuildInfo;
                }
            }
        }

        /// <summary>
        /// Gets the most recent connection attempt number.
        /// </summary>
        public int ConnectionAttempt {
            get { return connectionAttempt; }
        }

        /// <summary>
        /// Gets the index cache (used by EnsureIndex) for this server.
        /// </summary>
        public virtual IndexCache IndexCache {
            get { return indexCache; }
        }

        /// <summary>
        /// Gets the one and only instance for this server.
        /// </summary>
        public virtual MongoServerInstance Instance {
            get {
                lock (stateLock) {
                    switch (instances.Count) {
                        case 0: return null;
                        case 1: return instances[0];
                        default:
                            throw new InvalidOperationException("Instance property cannot be used when there is more than one instance.");
                    }
                }
            }
        }

        /// <summary>
        /// Gets the instances for this server.
        /// </summary>
        public virtual MongoServerInstance[] Instances {
            get {
                lock (stateLock) {
                    return instances.ToArray();
                }
            }
        }

        /// <summary>
        /// Gets the passive instances.
        /// </summary>
        public MongoServerInstance[] Passives {
            get {
                lock (stateLock) {
                    return instances.Where(i => i.IsPassive).ToArray();
                }
            }
        }

        /// <summary>
        /// Gets the primary instance (null if there is no primary).
        /// </summary>
        public MongoServerInstance Primary {
            get {
                lock (stateLock) {
                    return primary;
                }
            }
        }

        /// <summary>
        /// Gets the name of the replica set (null if not connected to a replica set).
        /// </summary>
        public virtual string ReplicaSetName {
            get { return replicaSetName; }
            internal set { replicaSetName = value; }
        }

        /// <summary>
        /// Gets the connection reserved by the current RequestStart scope (null if not in the scope of a RequestStart).
        /// </summary>
        public virtual MongoConnection RequestConnection {
            get {
                lock (serverLock) {
                    int threadId = Thread.CurrentThread.ManagedThreadId;
                    Request request;
                    if (requests.TryGetValue(threadId, out request)) {
                        return request.Connection;
                    } else {
                        return null;
                    }
                }
            }
        }

        /// <summary>
        /// Gets the RequestStart nesting level for the current thread.
        /// </summary>
        public virtual int RequestNestingLevel {
            get {
                lock (serverLock) {
                    int threadId = Thread.CurrentThread.ManagedThreadId;
                    Request request;
                    if (requests.TryGetValue(threadId, out request)) {
                        return request.NestingLevel;
                    } else {
                        return 0;
                    }
                }
            }
        }

        /// <summary>
        /// Gets the secondary instances.
        /// </summary>
        public MongoServerInstance[] Secondaries {
            get {
                lock (stateLock) {
                    return instances.Where(i => i.IsSecondary).ToArray();
                }
            }
        }

        /// <summary>
        /// Gets the settings for this server.
        /// </summary>
        public virtual MongoServerSettings Settings {
            get { return settings; }
        }

        /// <summary>
        /// Gets the current state of this server (as of the last operation, not updated until another operation is performed).
        /// </summary>
        public virtual MongoServerState State {
            get { return state; }
        }
        #endregion

        #region public indexers
        /// <summary>
        /// Gets a MongoDatabase instance representing a database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase this[
            string databaseName
        ] {
            get { return GetDatabase(databaseName); }
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing a database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <param name="credentials">The credentials to use with this database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase this[
            string databaseName,
            MongoCredentials credentials
        ] {
            get { return GetDatabase(databaseName, credentials); }
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing a database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="databaseSettings">The settings to use with this database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase this[
            MongoDatabaseSettings databaseSettings
        ] {
            get { return GetDatabase(databaseSettings); }
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing a database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <param name="credentials">The credentials to use with this database.</param>
        /// <param name="safeMode">The safe mode to use with this database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase this[
            string databaseName,
            MongoCredentials credentials,
            SafeMode safeMode
        ] {
            get { return GetDatabase(databaseName, credentials, safeMode); }
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing a database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <param name="safeMode">The safe mode to use with this database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase this[
            string databaseName,
            SafeMode safeMode
        ] {
            get { return GetDatabase(databaseName, safeMode); }
        }
        #endregion

        #region public methods
        /// <summary>
        /// Connects to the server. Normally there is no need to call this method as
        /// the driver will connect to the server automatically when needed.
        /// </summary>
        public virtual void Connect() {
            Connect(settings.ConnectTimeout);
        }

        /// <summary>
        /// Connects to the server. Normally there is no need to call this method as
        /// the driver will connect to the server automatically when needed.
        /// </summary>
        /// <param name="waitFor">What to wait for before returning.</param>
        public virtual void Connect(
            ConnectWaitFor waitFor
        ) {
            Connect(settings.ConnectTimeout, waitFor);
        }

        /// <summary>
        /// Connects to the server. Normally there is no need to call this method as
        /// the driver will connect to the server automatically when needed.
        /// </summary>
        /// <param name="timeout">How long to wait before timing out.</param>
        public virtual void Connect(
            TimeSpan timeout
        ) {
            var waitFor = settings.SlaveOk ? ConnectWaitFor.AnySlaveOk : ConnectWaitFor.Primary;
            Connect(timeout, waitFor);
        }

        /// <summary>
        /// Connects to the server. Normally there is no need to call this method as
        /// the driver will connect to the server automatically when needed.
        /// </summary>
        /// <param name="timeout">How long to wait before timing out.</param>
        /// <param name="waitFor">What to wait for before returning.</param>
        public virtual void Connect(
            TimeSpan timeout,
            ConnectWaitFor waitFor
        ) {
            lock (serverLock) {
                switch (settings.ConnectionMode) {
                    case ConnectionMode.Direct:
                        if (state == MongoServerState.Disconnected) {
                            var directConnector = new DirectConnector(this, ++connectionAttempt);
                            directConnector.Connect(timeout);
                        }
                        return;
                    case ConnectionMode.ReplicaSet:
                        var timeoutAt = DateTime.UtcNow + timeout;
                        while (true) {
                            lock (stateLock) {
                                switch (waitFor) {
                                    case ConnectWaitFor.All:
                                        if (instances.All(i => i.State == MongoServerState.Connected)) {
                                            return;
                                        }
                                        break;
                                    case ConnectWaitFor.AnySlaveOk:
                                        if (instances.Any(i => (i.IsPrimary || i.IsSecondary || i.IsPassive) && i.State == MongoServerState.Connected)) {
                                            return;
                                        }
                                        break;
                                    case ConnectWaitFor.Primary:
                                        if (primary != null && primary.State == MongoServerState.Connected) {
                                            return;
                                        }
                                        break;
                                    default:
                                        throw new ArgumentException("Invalid ConnectWaitMode.");
                                }
                            }

                            // a replica set connector might have exited early and still be working in the background
                            if (state == MongoServerState.Connecting) {
                                if (DateTime.UtcNow > timeoutAt) {
                                    throw new TimeoutException("Timeout while connecting to server.");
                                }
                                Thread.Sleep(TimeSpan.FromMilliseconds(20));
                            } else {
                                break;
                            }
                        }

                        var replicaSetConnector = new ReplicaSetConnector(this, ++connectionAttempt);
                        var remainingTimeout = timeoutAt - DateTime.UtcNow;
                        replicaSetConnector.Connect(remainingTimeout, waitFor);
                        return;
                    default:
                        throw new MongoInternalException("Invalid ConnectionMode.");
                }
            }
        }

        // TODO: fromHost parameter?
        /// <summary>
        /// Copies a database.
        /// </summary>
        /// <param name="from">The name of an existing database.</param>
        /// <param name="to">The name of the new database.</param>
        public virtual void CopyDatabase(
            string from,
            string to
        ) {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Creates an instance of MongoDatabaseSettings for the named database with the rest of the settings inherited.
        /// You can override some of these settings before calling GetDatabase.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <returns>An instance of MongoDatabase for <paramref name="databaseName"/>.</returns>
        public virtual MongoDatabaseSettings CreateDatabaseSettings(
            string databaseName
        ) {
            return new MongoDatabaseSettings(
                databaseName,
                settings.DefaultCredentials,
                settings.GuidRepresentation,
                settings.SafeMode,
                settings.SlaveOk
            );
        }

        /// <summary>
        /// Tests whether a database exists.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <returns>True if the database exists.</returns>
        public virtual bool DatabaseExists(
            string databaseName
        ) {
            return GetDatabaseNames().Contains(databaseName);
        }

        /// <summary>
        /// Disconnects from the server. Normally there is no need to call this method so
        /// you should be sure to have a good reason to call it.
        /// </summary>
        public virtual void Disconnect() {
            // normally called from a connection when there is a SocketException
            // but anyone can call it if they want to close all sockets to the server
            lock (serverLock) {
                foreach (var instance in Instances) {
                    instance.Disconnect();
                }

                // note: server state should have changed in response to InstanceStateChanged events
                if (state != MongoServerState.Disconnected) {
                    throw new MongoInternalException("Disconnect failed to change MongoServerState to Disconnected.");
                }
            }
        }

        /// <summary>
        /// Drops a database.
        /// </summary>
        /// <param name="databaseName">The name of the database to be dropped.</param>
        /// <returns>A <see cref="CommandResult"/>.</returns>
        public virtual CommandResult DropDatabase(
            string databaseName
        ) {
            MongoDatabase database = GetDatabase(databaseName);
            var command = new CommandDocument("dropDatabase", 1);
            return database.RunCommand(command);
        }

        /// <summary>
        /// Fetches the document referred to by the DBRef.
        /// </summary>
        /// <param name="dbRef">The <see cref="MongoDBRef"/> to fetch.</param>
        /// <returns>A BsonDocument (or null if the document was not found).</returns>
        public virtual BsonDocument FetchDBRef(
            MongoDBRef dbRef
        ) {
            return FetchDBRefAs<BsonDocument>(dbRef);
        }

        /// <summary>
        /// Fetches the document referred to by the DBRef, deserialized as a <typeparamref name="TDocument"/>.
        /// </summary>
        /// <typeparam name="TDocument">The nominal type of the document to fetch.</typeparam>
        /// <param name="dbRef">The <see cref="MongoDBRef"/> to fetch.</param>
        /// <returns>A <typeparamref name="TDocument"/> (or null if the document was not found).</returns>
        public virtual TDocument FetchDBRefAs<TDocument>(
            MongoDBRef dbRef
        ) {
            return (TDocument) FetchDBRefAs(typeof(TDocument), dbRef);
        }

        /// <summary>
        /// Fetches the document referred to by the DBRef.
        /// </summary>
        /// <param name="documentType">The nominal type of the document to fetch.</param>
        /// <param name="dbRef">The <see cref="MongoDBRef"/> to fetch.</param>
        /// <returns>The document (or null if the document was not found).</returns>
        public virtual object FetchDBRefAs(
            Type documentType,
            MongoDBRef dbRef
        ) {
            if (dbRef.DatabaseName == null) {
                throw new ArgumentException("MongoDBRef DatabaseName missing.");
            }

            var database = GetDatabase(dbRef.DatabaseName);
            return database.FetchDBRefAs(documentType, dbRef);
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing the admin database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="credentials">The credentials to use with the admin database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase GetAdminDatabase(
            MongoCredentials credentials
        ) {
            return GetDatabase("admin", credentials);
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing the admin database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="credentials">The credentials to use with the admin database.</param>
        /// <param name="safeMode">The safe mode to use with the admin database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase GetAdminDatabase(
            MongoCredentials credentials,
            SafeMode safeMode
        ) {
            return GetDatabase("admin", credentials, safeMode);
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing the admin database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="safeMode">The safe mode to use with the admin database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase GetAdminDatabase(
            SafeMode safeMode
        ) {
            return GetDatabase("admin", safeMode);
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing a database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="databaseSettings">The settings to use with this database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase GetDatabase(
            MongoDatabaseSettings databaseSettings
        ) {
            lock (serverLock) {
                MongoDatabase database;
                if (!databases.TryGetValue(databaseSettings, out database)) {
                    database = new MongoDatabase(this, databaseSettings);
                    databases.Add(databaseSettings, database);
                }
                return database;
            }
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing a database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase GetDatabase(
            string databaseName
        ) {
            var databaseSettings = CreateDatabaseSettings(databaseName);
            return GetDatabase(databaseSettings);
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing a database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <param name="credentials">The credentials to use with this database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase GetDatabase(
            string databaseName,
            MongoCredentials credentials
        ) {
            var databaseSettings = CreateDatabaseSettings(databaseName);
            databaseSettings.Credentials = credentials;
            return GetDatabase(databaseSettings);
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing a database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <param name="credentials">The credentials to use with this database.</param>
        /// <param name="safeMode">The safe mode to use with this database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase GetDatabase(
            string databaseName,
            MongoCredentials credentials,
            SafeMode safeMode
        ) {
            var databaseSettings = CreateDatabaseSettings(databaseName);
            databaseSettings.Credentials = credentials;
            databaseSettings.SafeMode = safeMode;
            return GetDatabase(databaseSettings);
        }

        /// <summary>
        /// Gets a MongoDatabase instance representing a database on this server. Only one instance
        /// is created for each combination of database settings.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <param name="safeMode">The safe mode to use with this database.</param>
        /// <returns>A new or existing instance of MongoDatabase.</returns>
        public virtual MongoDatabase GetDatabase(
            string databaseName,
            SafeMode safeMode
        ) {
            var databaseSettings = CreateDatabaseSettings(databaseName);
            databaseSettings.SafeMode = safeMode;
            return GetDatabase(databaseSettings);
        }

        /// <summary>
        /// Gets the names of the databases on this server.
        /// </summary>
        /// <returns>A list of database names.</returns>
        public virtual IEnumerable<string> GetDatabaseNames() {
            var result = AdminDatabase.RunCommand("listDatabases");
            var databaseNames = new List<string>();
            foreach (BsonDocument database in result.Response["databases"].AsBsonArray.Values) {
                string databaseName = database["name"].AsString;
                databaseNames.Add(databaseName);
            }
            databaseNames.Sort();
            return databaseNames;
        }

        /// <summary>
        /// Gets the last error (if any) that occurred on this connection. You MUST be within a RequestStart to call this method.
        /// </summary>
        /// <returns>The last error (<see cref=" GetLastErrorResult"/>)</returns>
        public virtual GetLastErrorResult GetLastError() {
            if (RequestNestingLevel == 0) {
                throw new InvalidOperationException("GetLastError can only be called if RequestStart has been called first.");
            }
            var adminDatabase = GetAdminDatabase((MongoCredentials) null); // no credentials needed for getlasterror
            return adminDatabase.RunCommandAs<GetLastErrorResult>("getlasterror"); // use all lowercase for backward compatibility
        }

        /// <summary>
        /// Checks whether the server is alive (throws an exception if not).
        /// </summary>
        public virtual void Ping() {
            var command = new CommandDocument("ping", 1);
            RunAdminCommand(command);
        }

        /// <summary>
        /// Reconnects to the server. Normally there is no need to call this method. All connections
        /// are closed and new connections will be opened as needed. Calling
        /// this method frequently will result in connection thrashing.
        /// </summary>
        public virtual void Reconnect() {
            lock (serverLock) {
                Disconnect();
                Connect();
            }
        }

        /// <summary>
        /// Lets the server know that this thread is done with a series of related operations. Instead of calling this method it is better
        /// to put the return value of RequestStart in a using statement.
        /// </summary>
        public virtual void RequestDone() {
            lock (serverLock) {
                int threadId = Thread.CurrentThread.ManagedThreadId;
                Request request;
                if (requests.TryGetValue(threadId, out request)) {
                    if (--request.NestingLevel == 0) {
                        requests.Remove(threadId);
                        ReleaseConnection(request.Connection);
                    }
                } else {
                    throw new InvalidOperationException("Thread is not in a request (did you call RequestStart?).");
                }
            }
        }

        /// <summary>
        /// Lets the server know that this thread is about to begin a series of related operations that must all occur
        /// on the same connection. The return value of this method implements IDisposable and can be placed in a
        /// using statement (in which case RequestDone will be called automatically when leaving the using statement).
        /// </summary>
        /// <param name="initialDatabase">One of the databases involved in the related operations.</param>
        /// <returns>A helper object that implements IDisposable and calls <see cref="RequestDone"/> from the Dispose method.</returns>
        public virtual IDisposable RequestStart(
            MongoDatabase initialDatabase
        ) {
            return RequestStart(initialDatabase, false); // not slaveOk
        }

        /// <summary>
        /// Lets the server know that this thread is about to begin a series of related operations that must all occur
        /// on the same connection. The return value of this method implements IDisposable and can be placed in a
        /// using statement (in which case RequestDone will be called automatically when leaving the using statement).
        /// </summary>
        /// <param name="initialDatabase">One of the databases involved in the related operations.</param>
        /// <param name="slaveOk">Whether queries should be sent to secondary servers.</param>
        /// <returns>A helper object that implements IDisposable and calls <see cref="RequestDone"/> from the Dispose method.</returns>
        public virtual IDisposable RequestStart(
            MongoDatabase initialDatabase,
            bool slaveOk
        ) {
            lock (serverLock) {
                int threadId = Thread.CurrentThread.ManagedThreadId;
                Request request;
                if (requests.TryGetValue(threadId, out request)) {
                    if (!slaveOk && request.SlaveOk) {
                        throw new InvalidOperationException("A nested call to RequestStart with slaveOk false is not allowed when the original call to RequestStart was made with slaveOk true.");
                    }
                    request.NestingLevel++;
                } else {
                    var connection = AcquireConnection(initialDatabase, slaveOk);
                    request = new Request(connection, slaveOk);
                    requests.Add(threadId, request);
                }

                return new RequestStartResult(this);
            }
        }

        /// <summary>
        /// Removes all entries in the index cache used by EnsureIndex. Call this method
        /// when you know (or suspect) that a process other than this one may have dropped one or
        /// more indexes.
        /// </summary>
        public virtual void ResetIndexCache() {
            indexCache.Reset();
        }

        /// <summary>
        /// Runs a command on the admin database.
        /// </summary>
        /// <param name="command">The command to run.</param>
        /// <returns>The result of the command (see <see cref="CommandResult"/>).</returns>
        public virtual CommandResult RunAdminCommand(
            IMongoCommand command
        ) {
            return RunAdminCommandAs<CommandResult>(command);
        }

        /// <summary>
        /// Runs a command on the admin database.
        /// </summary>
        /// <param name="commandName">The name of the command to run.</param>
        /// <returns>The result of the command (as a <see cref="CommandResult"/>).</returns>
        public virtual CommandResult RunAdminCommand(
            string commandName
        ) {
            return RunAdminCommandAs<CommandResult>(commandName);
        }

        /// <summary>
        /// Runs a command on the admin database.
        /// </summary>
        /// <typeparam name="TCommandResult">The type to use for the command result.</typeparam>
        /// <param name="command">The command to run.</param>
        /// <returns>The result of the command (as a <typeparamref name="TCommandResult"/>).</returns>
        public virtual TCommandResult RunAdminCommandAs<TCommandResult>(
            IMongoCommand command
        ) where TCommandResult : CommandResult, new() {
            return (TCommandResult) RunAdminCommandAs(typeof(TCommandResult), command);
        }

        /// <summary>
        /// Runs a command on the admin database.
        /// </summary>
        /// <typeparam name="TCommandResult">The type to use for the command result.</typeparam>
        /// <param name="commandName">The name of the command to run.</param>
        /// <returns>The result of the command (as a <typeparamref name="TCommandResult"/>).</returns>
        public virtual TCommandResult RunAdminCommandAs<TCommandResult>(
            string commandName
        ) where TCommandResult : CommandResult, new() {
            return (TCommandResult) RunAdminCommandAs(typeof(TCommandResult), commandName);
        }

        /// <summary>
        /// Runs a command on the admin database.
        /// </summary>
        /// <param name="commandResultType">The type to use for the command result.</param>
        /// <param name="command">The command to run.</param>
        /// <returns>The result of the command.</returns>
        public virtual object RunAdminCommandAs(
            Type commandResultType,
            IMongoCommand command
        ) {
            return AdminDatabase.RunCommandAs(commandResultType, command);
        }

        /// <summary>
        /// Runs a command on the admin database.
        /// </summary>
        /// <param name="commandResultType">The type to use for the command result.</param>
        /// <param name="commandName">The name of the command to run.</param>
        /// <returns>The result of the command.</returns>
        public virtual object RunAdminCommandAs(
            Type commandResultType,
            string commandName
        ) {
            return AdminDatabase.RunCommandAs(commandResultType, commandName);
        }

        /// <summary>
        /// Shuts down the server.
        /// </summary>
        public virtual void Shutdown() {
            lock (serverLock) {
                try {
                    RunAdminCommand("shutdown");
                } catch (EndOfStreamException) {
                    // we expect an EndOfStreamException when the server shuts down so we ignore it
                }
            }
        }
        #endregion

        #region internal methods
        internal MongoConnection AcquireConnection(
            MongoDatabase database,
            bool slaveOk
        ) {
            lock (serverLock) {
                // if a thread has called RequestStart it wants all operations to take place on the same connection
                int threadId = Thread.CurrentThread.ManagedThreadId;
                Request request;
                if (requests.TryGetValue(threadId, out request)) {
                    if (!slaveOk && request.SlaveOk) {
                        throw new InvalidOperationException("A call to AcquireConnection with slaveOk false is not allowed when the current RequestStart was made with slaveOk true.");
                    }
                    request.Connection.CheckAuthentication(database); // will throw exception if authentication fails
                    return request.Connection;
                }

                var serverInstance = ChooseServerInstance(slaveOk);
                return serverInstance.AcquireConnection(database);
            }
        }

        internal MongoConnection AcquireConnection(
            MongoDatabase database,
            MongoServerInstance serverInstance
        ) {
            lock (serverLock) {
                // if a thread has called RequestStart it wants all operations to take place on the same connection
                int threadId = Thread.CurrentThread.ManagedThreadId;
                Request request;
                if (requests.TryGetValue(threadId, out request)) {
                    if (request.Connection.ServerInstance != serverInstance) {
                        var message = string.Format(
                            "AcquireConnection called for server instance '{0}' but thread is in a RequestStart for server instance '{1}'.",
                            serverInstance.Address,
                            request.Connection.ServerInstance.Address
                        );
                        throw new MongoConnectionException(message);
                    }
                    request.Connection.CheckAuthentication(database); // will throw exception if authentication fails
                    return request.Connection;
                }

                return serverInstance.AcquireConnection(database);
            }
        }

        internal void AddInstance(
            MongoServerInstance instance
        ) {
            lock (stateLock) {
                if (instances.Any(i => i.Address == instance.Address)) {
                    var message = string.Format("A server instance already exists for address '{0}'.", instance.Address);
                    throw new ArgumentException(message);
                }
                instances.Add(instance);
                instance.StateChanged += InstanceStateChanged;
                InstanceStateChanged(instance, null); // adding an instance can change server state
            }
        }

        internal MongoServerInstance ChooseServerInstance(
            bool slaveOk
        ) {
            lock (serverLock) {
                var waitFor = slaveOk ? ConnectWaitFor.AnySlaveOk : ConnectWaitFor.Primary;
                Connect(waitFor);

                if (settings.ConnectionMode == ConnectionMode.ReplicaSet) {
                    if (slaveOk) {
                        // round robin the connected secondaries, fall back to primary if no secondary found
                        lock (stateLock) {
                            for (int i = 0; i < instances.Count; i++) {
                                loadBalancingInstanceIndex = (loadBalancingInstanceIndex + 1) % instances.Count; // round robin
                                var instance = instances[loadBalancingInstanceIndex];
                                if (instance.State == MongoServerState.Connected && (instance.IsSecondary || instance.IsPassive)) {
                                    return instance;
                                }
                            }
                        }
                    }

                    lock (stateLock) {
                        if (primary == null) {
                            throw new MongoConnectionException("Primary server not found.");
                        }
                        return primary;
                    }
                } else {
                    return Instance;
                }
            }
        }

        internal void ReleaseConnection(
            MongoConnection connection
        ) {
            lock (serverLock) {
                // if the thread has called RequestStart just verify that the connection it is releasing is the right one
                int threadId = Thread.CurrentThread.ManagedThreadId;
                Request request;
                if (requests.TryGetValue(threadId, out request)) {
                    if (connection != request.Connection) {
                        throw new ArgumentException("Connection being released is not the one assigned to the thread by RequestStart.", "connection");
                    }
                    return; // hold on to the connection until RequestDone is called
                }

                connection.ServerInstance.ReleaseConnection(connection);
            }
        }

        internal void RemoveInstance(
            MongoServerInstance instance
        ) {
            lock (stateLock) {
                instance.StateChanged -= InstanceStateChanged;
                instances.Remove(instance);
                InstanceStateChanged(instance, null); // removing an instance can change server state
            }
        }
        #endregion

        #region private methods
        private void InstanceStateChanged(
            object sender,
            object args
        ) {
            lock (stateLock) {
                var instance = (MongoServerInstance) sender;

                if (instances.Contains(instance)) {
                    if (instance.IsPrimary && instance.State == MongoServerState.Connected && primary != instance) {
                        primary = instance; // new primary
                    }
                } else {
                    if (primary == instance) {
                        primary = null; // no primary until we find one again
                    }
                }

                if (instances.All(i => i.State == MongoServerState.Connected)) {
                    state = MongoServerState.Connected;
                } else if (instances.Any(i => i.State == MongoServerState.Connecting)) {
                    state = MongoServerState.Connecting;
                } else if (instances.Any(i => i.State == MongoServerState.Connected)) {
                    state = MongoServerState.ConnectedToSubset;
                } else {
                    state = MongoServerState.Disconnected;
                }
            }
        }
        #endregion

        #region private nested classes
        private class Request {
            #region private fields
            private MongoConnection connection;
            private bool slaveOk;
            private int nestingLevel;
            #endregion

            #region constructors
            public Request(
                MongoConnection connection,
                bool slaveOk
            ) {
                this.connection = connection;
                this.slaveOk = slaveOk;
                this.nestingLevel = 1;
            }
            #endregion

            #region public properties
            public MongoConnection Connection {
                get { return connection; }
                set { connection = value; }
            }

            public int NestingLevel {
                get { return nestingLevel; }
                set { nestingLevel = value; }
            }

            public bool SlaveOk {
                get { return slaveOk; }
                internal set { slaveOk = value; }
            }
            #endregion
        }

        private class RequestStartResult : IDisposable {
            #region private fields
            private MongoServer server;
            #endregion

            #region constructors
            public RequestStartResult(
                MongoServer server
            ) {
                this.server = server;
            }
            #endregion

            #region public methods
            public void Dispose() {
                server.RequestDone();
            }
            #endregion
        }
        #endregion
    }
}
