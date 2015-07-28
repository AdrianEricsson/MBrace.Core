﻿#I "../../bin/"

#r "MBrace.Core.dll"
#r "MBrace.Thespian.exe"
#r "MBrace.Runtime.Core.dll"
#r "MBrace.Flow.dll"
#r "Streams.Core.dll"

open System
open MBrace.Core
open MBrace.Library
open MBrace.Thespian
open MBrace.Flow

MBraceThespian.WorkerExecutable <- __SOURCE_DIRECTORY__ + "/../../bin/MBrace.Thespian.exe"

#time "on"

let cluster = MBraceThespian.InitLocal 1
cluster.AttachLogger(new ConsoleLogger())

let workers = cluster.Workers

cloud { return 42 } |> cluster.Run

cluster.ShowProcessInfo()
cluster.ShowWorkerInfo()

let proc = 
    CloudFlow.OfHttpFileByLine "http://www.textfiles.com/etext/AUTHORS/SHAKESPEARE/shakespeare-alls-11.txt"
    |> CloudFlow.length
    |> cluster.CreateProcess

proc.AwaitResult() |> Async.RunSynchronously


let test = cloud {
    let cell = ref 0
    let! results = Cloud.Parallel [ for i in 1 .. 10 -> cloud { incr cell } ]
    return !cell
}

cluster.RunLocally(test, memoryEmulation = MemoryEmulation.Shared)
cluster.RunLocally(test, memoryEmulation = MemoryEmulation.Copied)

let test' = cloud {
    return box(new System.IO.MemoryStream())
}

cluster.RunLocally(test', memoryEmulation = MemoryEmulation.Shared)
cluster.RunLocally(test', memoryEmulation = MemoryEmulation.EnsureSerializable)

let pflow =
    CloudFlow.OfArray [|1 .. 100|]
    |> CloudFlow.collect (fun i -> seq { for j in 1L .. 10000L -> int64 i * j })
    |> CloudFlow.filter (fun i -> i % 3L <> 0L)
    |> CloudFlow.map (fun i -> sprintf "Lorem ipsum dolor sit amet #%d" i)
    |> CloudFlow.cache
    |> cluster.Run

pflow |> CloudFlow.length |> cluster.Run


let values =
    cloud { return! CloudValue.NewArrayPartitioned(seq {1L .. 10000000L}, partitionThreshold = 100000L, storageLevel = StorageLevel.Disk) }
    |> cluster.RunLocally

values |> Array.sumBy (fun v -> v.Size)

cluster.ShowWorkerInfo()

cluster.ShowProcessInfo()

cluster.Run (cloud { return 42})