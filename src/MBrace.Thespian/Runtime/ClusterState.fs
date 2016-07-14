﻿namespace MBrace.Thespian.Runtime

open System
open MBrace.Core
open MBrace.Core.Internals
open MBrace.Library
open MBrace.Runtime
open MBrace.Runtime.Utils
open MBrace.Runtime.Components

/// Runtime instance identifier
type RuntimeId = private { Id : string }
with
    interface IRuntimeId with member x.Id = x.Id
    override x.ToString() = x.Id
    /// Creates a new unique runtime id instance
    static member Create() = { Id = mkUUID() }

/// Serializable MBrace.Thespian cluster state client object.
/// Used for all interactions with the cluster
[<NoEquality; NoComparison; AutoSerializable(true)>]
type ClusterState =
    {
        /// Unique cluster identifier object
        Id : RuntimeId
        /// Thespian address of the runtime state hosting process
        Uri : string
        /// Indicates that the runtime is hosted by an MBrace worker instance.
        IsWorkerHosted : bool
        /// Default serializer instance
        Serializer : ISerializer
        /// Resource factory instace used for instantiating resources
        /// in the cluster state hosting process
        ResourceFactory : ResourceFactory
        /// CloudValue provider instance used by runtime
        StoreCloudValueProvider : StoreCloudValueProvider
        /// Cluster worker monitor
        WorkerManager : WorkerManager
        /// Cloud process instance
        ProcessManager : CloudProcessManager
        /// Cloud work item queue instance
        WorkItemQueue : WorkItemQueue
        /// Misc resources appended to runtime state
        Resources : ResourceRegistry
        /// Local node state factory instance
        LocalStateFactory : LocalStateFactory
    }

    /// <summary>
    ///     Creates a cluster state object that is hosted in the local process
    /// </summary>
    /// <param name="fileStore">File store instance used by cluster.</param>
    /// <param name="isWorkerHosted">Indicates that instance is hosted by worker instance.</param>
    /// <param name="userDataDirectory">Directory used for storing user data.</param>
    /// <param name="workItemsDirectory">Directory used for persisting work items in store.</param>
    /// <param name="assemblyDirectory">Assembly directory used in store.</param>
    /// <param name="cacheDirectory">CloudValue cache directory used in store.</param>
    /// <param name="miscResources">Misc resources passed to cloud workflows.</param>
    static member Create(fileStore : ICloudFileStore, isWorkerHosted : bool, 
                            ?userDataDirectory : string, ?workItemsDirectory : string, ?maxLogWriteInterval : TimeSpan,
                            ?assemblyDirectory : string, ?cacheDirectory : string, ?miscResources : ResourceRegistry) =

        let userDataDirectory = defaultArg userDataDirectory "userData"
        let assemblyDirectory = defaultArg assemblyDirectory "vagabond"
        let workItemsDirectory = defaultArg workItemsDirectory "mbrace-data"
        let cacheDirectory = defaultArg cacheDirectory "cloudValue"

        let serializer = new FsPicklerBinarySerializer()
        let cloudValueStore = fileStore.WithDefaultDirectory cacheDirectory
        let mkCacheInstance () = Config.ObjectCache
//        let mkLocalCachingFileStore () = (Config.FileSystemStore :> ICloudFileStore).WithDefaultDirectory "cloudValueCache"
        let cloudValueProvider = StoreCloudValueProvider.InitCloudValueProvider(cloudValueStore, mkCacheInstance, (*mkLocalCachingFileStore,*) shadowPersistObjects = true)
        let persistedValueManager = PersistedValueManager.Create(fileStore.WithDefaultDirectory workItemsDirectory, serializer = serializer, persistThreshold = 512L * 1024L)

        let localStateFactory = DomainLocal.Create(fun () ->
            let logger = AttacheableLogger.Create(makeAsynchronous = false)
            let vagabondStoreConfig = StoreAssemblyManagerConfiguration.Create(fileStore.WithDefaultDirectory assemblyDirectory, serializer, container = assemblyDirectory, ignoredAssemblies = [|System.Reflection.Assembly.GetExecutingAssembly()|])
            let assemblyManager = StoreAssemblyManager.Create(vagabondStoreConfig, logger)
            let siftConfig = ClosureSiftConfiguration.Create(cloudValueProvider)
            let siftManager = ClosureSiftManager.Create(siftConfig, logger)
            let sysLogSchema = new DefaultStoreSystemLogSchema(fileStore, getLogDirectorySuffix = fun w -> let u = System.Uri(w.Id) in sprintf "%s-%d" u.Host u.Port)
            let cloudLogSchema = new DefaultStoreCloudLogSchema(fileStore)
            let maxLogWriteInterval = maxLogWriteInterval |> Option.map (fun i -> int i.TotalMilliseconds)
            let storeSystemLogger = StoreSystemLogManager.Create(sysLogSchema, fileStore, ?maxInterval = maxLogWriteInterval, minInterval = 100, minEntries = 500)
            let storeCloudLogger = StoreCloudLogManager.Create(fileStore, cloudLogSchema, sysLogger = logger, ?maxInterval = maxLogWriteInterval, minInterval = 100, minEntries = 500)
            {
                Logger = logger
                PersistedValueManager = persistedValueManager
                AssemblyManager = assemblyManager
                SiftManager = siftManager
                SystemLogManager = storeSystemLogger
                CloudLogManager = storeCloudLogger 
            })

        let id = RuntimeId.Create()
        let resourceFactory = ResourceFactory.Create()
        let workerManager = WorkerManager.Create(localStateFactory)
        let taskManager = CloudProcessManager.Create(localStateFactory)
        let workItemQueue = WorkItemQueue.Create(workerManager, localStateFactory)

        let resources = resource {
            yield new ActorAtomProvider(resourceFactory) :> ICloudAtomProvider
            yield new ActorQueueProvider(resourceFactory) :> ICloudQueueProvider
            yield new ActorDictionaryProvider(resourceFactory) :> ICloudDictionaryProvider
            yield serializer :> ISerializer
            yield new FsPicklerJsonSerializer() :> ITextSerializer
            yield cloudValueProvider :> ICloudValueProvider
            match miscResources with Some r -> yield! r | None -> ()
            yield fileStore.WithDefaultDirectory userDataDirectory
        }

        {
            Id = id
            IsWorkerHosted = isWorkerHosted
            Uri = Config.LocalMBraceUri
            Serializer = serializer :> ISerializer

            ResourceFactory = resourceFactory
            StoreCloudValueProvider = cloudValueProvider
            WorkerManager = workerManager
            ProcessManager = taskManager
            WorkItemQueue = workItemQueue
            Resources = resources
            LocalStateFactory = localStateFactory
        }

    /// <summary>
    ///     Creates a IRuntimeManager instance using provided state
    ///     and given local logger instance.
    /// </summary>
    /// <param name="localLogger">Logger instance bound to local process.</param>
    member state.GetLocalRuntimeManager() =
        new RuntimeManager(state) :> IRuntimeManager

/// Local IRuntimeManager implementation
and [<AutoSerializable(false)>] private RuntimeManager (state : ClusterState) =
    // force initialization of local configuration in the current AppDomain
    let localState = state.LocalStateFactory.Value
    // Install cache in the local application domain
    do state.StoreCloudValueProvider.InstallCacheOnLocalAppDomain()
    let localLogManager = new AttacheableLoggerManager(localState.Logger)
    let cancellationEntryFactory = new ActorCancellationEntryFactory(state.ResourceFactory)
    let counterFactory = new ActorCounterFactory(state.ResourceFactory)
    let resultAggregatorFactory = new ActorResultAggregatorFactory(state.ResourceFactory, state.LocalStateFactory)

    interface IRuntimeManager with
        member x.Id = state.Id :> _
        member x.Serializer = Config.Serializer :> _
        member x.ResourceRegistry: ResourceRegistry = state.Resources

        member x.LocalSystemLogManager : ILocalSystemLogManager = localLogManager :> _
        member x.RuntimeSystemLogManager : IRuntimeSystemLogManager = localState.SystemLogManager :> _
        member x.CloudLogManager : ICloudLogManager = localState.CloudLogManager :> _
        
        member x.AssemblyManager: IAssemblyManager = localState.AssemblyManager :> _
        member x.CancellationEntryFactory: ICancellationEntryFactory = cancellationEntryFactory :> _
        member x.CounterFactory: ICloudCounterFactory = counterFactory :> _
        member x.ResultAggregatorFactory: ICloudResultAggregatorFactory = resultAggregatorFactory :> _
        member x.WorkerManager = state.WorkerManager :> _
        member x.ProcessManager = state.ProcessManager :> _
        member x.WorkItemQueue: ICloudWorkItemQueue = state.WorkItemQueue :> _

        member x.ResetClusterState () = async { return raise <| new System.NotSupportedException("MBrace.Thespian: cluster reset not supported.") }