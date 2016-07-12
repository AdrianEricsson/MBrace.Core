﻿namespace MBrace.Runtime

open System

open MBrace.Core
open MBrace.Core.Internals

/// Runtime provided worker identifier
/// must implement equality and comparison semantics
type IWorkerId =
    inherit IComparable
    /// Worker identifier
    abstract Id : string

/// Worker execution status for specific item
[<NoEquality; NoComparison>]
type WorkerExecutionStatus =
    /// Worker dequeueing work items normally
    | Running
    /// Worker has been stopped manually
    | Stopped
    /// Error dequeueing work items
    | QueueFault of ExceptionDispatchInfo
    /// Worker declared dead by cluster
    | WorkerDeath

    override s.ToString() =
        match s with
        | Running -> "Running"
        | Stopped -> "Stopped"
        | QueueFault _ -> "Queue Fault"
        | WorkerDeath -> "Worker Death"


/// Worker metadata as specified by the instance itself
[<NoEquality; NoComparison>]
type WorkerInfo =
    {
        /// Machine hostname
        Hostname : string
        /// Machine ProcessId
        ProcessId : int
        /// Number of cores in worker
        ProcessorCount : int
        /// Maximum number of executing work items
        MaxWorkItemCount : int
        /// Heartbeat rate designated by worker instance
        HeartbeatInterval : TimeSpan
        /// Maximum designated heartbeat interval before declaring worker dead
        HeartbeatThreshold : TimeSpan
    }

/// Worker state object
[<NoEquality; NoComparison>]
type WorkerState =
    {
        /// Worker reference unique identifier
        Id : IWorkerId
        /// Worker metadata as specified by the worker
        Info : WorkerInfo
        /// Current number of executing work items
        CurrentWorkItemCount : int
        /// Last Heartbeat submitted by worker
        LastHeartbeat : DateTimeOffset
        /// Time of worker initialization/subscription
        InitializationTime : DateTimeOffset
        /// Worker work item execution status
        ExecutionStatus : WorkerExecutionStatus
        /// Latest worker performance metrics
        PerformanceMetrics : PerformanceInfo
    }

/// Worker manager abstraction; must be serializable
type IWorkerManager =

    /// <summary>
    ///     Asynchronously returns current worker information
    ///     for provided worker reference.
    /// </summary>
    /// <param name="target">Worker identifier to be fetched.</param>
    abstract TryGetWorkerState : id:IWorkerId -> Async<WorkerState option>

    /// <summary>
    ///     Subscribe a new worker instance to the cluster.
    /// </summary>
    /// <param name="worker">Worker instance to be subscribed.</param>
    /// <param name="info">Worker metadata for the instance.</param>
    /// <returns>Unsubscribe disposable. Disposing should cause the runtime to remove subscription for worker.</returns>
    abstract SubscribeWorker : id:IWorkerId * info:WorkerInfo -> Async<IDisposable>

    /// <summary>
    ///     Asynchronously declares that the worker active work item count has increased by one.
    /// </summary>
    /// <param name="id">Worker identifier.</param>
    abstract IncrementWorkItemCount : id:IWorkerId -> Async<unit>

    /// <summary>
    ///     Asynchronously declares that the worker active work item count has decreased by one.
    /// </summary>
    /// <param name="id">Worker identifier.</param>
    abstract DecrementWorkItemCount : id:IWorkerId -> Async<unit>

    /// <summary>
    ///     Asynchronously declares the current worker work item execution status.
    /// </summary>
    /// <param name="id">Worker identifier.</param>
    /// <param name="status">work item execution status to be set.</param>
    abstract DeclareWorkerStatus : id:IWorkerId * status:WorkerExecutionStatus -> Async<unit>

    /// <summary>
    ///     Asynchronously submits node performance metrics for provided worker.
    /// </summary>
    /// <param name="id">Worker id declaring performance metrics.</param>
    /// <param name="perf">Performance metrics for given worker.</param>
    abstract SubmitPerformanceMetrics : id:IWorkerId * perf:PerformanceInfo -> Async<unit>

    /// Asynchronously requests node performance metrics for all nodes.
    abstract GetAvailableWorkers : unit -> Async<WorkerState []>